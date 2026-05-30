using System.Diagnostics;
using System.Text.RegularExpressions;
using Imp.Infrastructure;

namespace Imp.Review;

// Mechanical-analysis pre-pass that runs before any qwen invocation. Output
// serves two roles:
//   1. Pre-pass findings (Roslyn warnings/errors, dotnet-format violations)
//      land directly in the report as "verdicts" — they don't need qwen.
//   2. Per-file diagnostic excerpts are passed into qwen axis briefs as
//      anchors ("the analyzer already named these — find what it can't"),
//      lowering qwen's false-positive rate.
//
// Determinism: builds in a fresh worktree at the window's head SHA, thrown
// away after, so a dirty cwd doesn't poison the SARIF.
// ReSharper is v2; not run here.

public sealed record Diagnostic(
    string RelativePath,
    int Line,
    string Severity,   // "error" | "warning" | "info"
    string Code,       // e.g. CA1822, CS0168
    string Message);

public sealed record FormatViolation(string RelativePath);

public sealed record PrePassResult(
    IReadOnlyList<Diagnostic> AllDiagnostics,
    IReadOnlyList<FormatViolation> FormatViolations,
    string? BuildError,           // populated if dotnet build crashed (couldn't restore, etc.)
    string? FormatError,          // populated if dotnet format crashed
    string WorktreePath);         // for trace/debug — orchestrator deletes after

public static class PrePass
{
    public static PrePassResult Run(string repoRoot, ReviewWindow window)
    {
        var worktreePath = CreateDetachedWorktree(repoRoot, window.HeadSha);
        var diagnostics = new List<Diagnostic>();
        var formatViolations = new List<FormatViolation>();
        string? buildError = null;
        string? formatError = null;

        try
        {
            (buildError, var parsed) = RunDotnetBuild(worktreePath);
            diagnostics.AddRange(parsed);

            (formatError, var fmtViolations) = RunDotnetFormatVerify(worktreePath);
            formatViolations.AddRange(fmtViolations);
        }
        catch (Exception ex)
        {
            ImpLog.Warn($"prepass: unexpected failure: {ex.GetType().Name}: {ex.Message}");
            buildError ??= $"prepass crashed: {ex.GetType().Name}: {ex.Message}";
        }

        return new PrePassResult(diagnostics, formatViolations, buildError, formatError, worktreePath);
    }

    public static void Cleanup(string repoRoot, string worktreePath)
    {
        // `git worktree remove --force` cleans up both the dir and the
        // .git/worktrees entry. Fall back to plain rmdir if git refuses
        // (e.g. detached HEAD edge cases) — the dir is in a tmp-ish path
        // and harmless to leave behind, just noisy.
        try
        {
            GitCommand.Run(repoRoot, "worktree", "remove", "--force", worktreePath);
        }
        catch (Exception ex)
        {
            ImpLog.Warn($"prepass: worktree remove failed: {ex.Message}");
        }
        if (Directory.Exists(worktreePath))
        {
            try { Directory.Delete(worktreePath, recursive: true); }
            catch (Exception ex) { ImpLog.Warn($"prepass: directory cleanup failed: {ex.Message}"); }
        }
    }

    static string CreateDetachedWorktree(string repoRoot, string sha)
    {
        var name = $"imp-review-{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..6]}";
        var path = Path.Combine(Path.GetTempPath(), name);
        var r = GitCommand.Run(repoRoot, "worktree", "add", "--detach", path, sha);
        if (!r.Ok)
            throw new InvalidOperationException($"git worktree add failed: {r.Stderr.Trim()}");
        return path;
    }

    // Matches MSBuild diagnostic lines:
    //   /abs/path/Foo.cs(42,13): warning CA1822: message text [/abs/Imp.csproj]
    static readonly Regex DiagnosticLine = new(
        @"^(?<path>[^()]+)\((?<line>\d+),\d+\):\s*(?<sev>error|warning|info)\s+(?<code>[A-Z]+\d+):\s*(?<msg>.+?)(?:\s+\[[^\]]+\])?$",
        RegexOptions.Compiled);

    static (string? Error, List<Diagnostic> Diagnostics) RunDotnetBuild(string cwd)
    {
        var r = RunProcess("dotnet", new[] { "build", "--nologo", "-v", "minimal" }, cwd, timeoutMs: 300_000);
        var diagnostics = new List<Diagnostic>();

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in r.Stdout.Split('\n').Concat(r.Stderr.Split('\n')))
        {
            var m = DiagnosticLine.Match(line.Trim());
            if (!m.Success) continue;

            var absPath = m.Groups["path"].Value.Trim();
            string rel;
            try { rel = Path.GetRelativePath(cwd, absPath).Replace('\\', '/'); }
            catch { rel = absPath; }

            // De-duplicate identical diagnostics that the build emits per
            // referencing project (common with multi-target setups).
            var key = $"{rel}:{m.Groups["line"].Value}:{m.Groups["code"].Value}";
            if (!seen.Add(key)) continue;

            diagnostics.Add(new Diagnostic(
                RelativePath: rel,
                Line: int.Parse(m.Groups["line"].Value),
                Severity: m.Groups["sev"].Value,
                Code: m.Groups["code"].Value,
                Message: m.Groups["msg"].Value.Trim()));
        }

        // Build exit-code != 0 with no parsed diagnostics → restore/IO crash.
        // We surface the message but keep going — qwen runs may still be useful.
        string? error = null;
        if (r.ExitCode != 0 && diagnostics.Count == 0)
        {
            var tail = string.Join('\n', r.Stdout.Split('\n').Concat(r.Stderr.Split('\n'))
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .TakeLast(10));
            error = $"dotnet build exit={r.ExitCode}: {tail}";
        }
        return (error, diagnostics);
    }

    static (string? Error, List<FormatViolation> Violations) RunDotnetFormatVerify(string cwd)
    {
        var r = RunProcess("dotnet", new[] { "format", "--verify-no-changes", "--no-restore" }, cwd, timeoutMs: 180_000);
        var violations = new List<FormatViolation>();

        // `dotnet format --verify-no-changes` exit codes:
        //   0 — no changes needed
        //   2 — changes would be applied (i.e. violations exist)
        //   other — tool error
        // Output names files that would be edited, one per line. The exact
        // format varies by sdk version; we just collect any .cs paths
        // mentioned in stdout/stderr and dedupe.
        if (r.ExitCode != 0 && r.ExitCode != 2)
        {
            return ($"dotnet format exit={r.ExitCode}: {r.Stderr.Trim()}", violations);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pathLike = new Regex(@"(?<path>[^\s'""]+\.cs)\b", RegexOptions.Compiled);
        foreach (var line in r.Stdout.Split('\n').Concat(r.Stderr.Split('\n')))
        {
            foreach (Match m in pathLike.Matches(line))
            {
                var p = m.Groups["path"].Value;
                string rel;
                try { rel = Path.GetRelativePath(cwd, Path.GetFullPath(Path.Combine(cwd, p))).Replace('\\', '/'); }
                catch { rel = p; }
                if (seen.Add(rel)) violations.Add(new FormatViolation(rel));
            }
        }
        return (null, violations);
    }

    static GitCommand.GitResult RunProcess(string fileName, string[] args, string cwd, int timeoutMs)
    {
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };
        foreach (var a in args) proc.StartInfo.ArgumentList.Add(a);
        proc.Start();
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        if (!proc.WaitForExit(timeoutMs))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            return new GitCommand.GitResult(-1, "", $"{fileName} timed out after {timeoutMs}ms");
        }
        return new GitCommand.GitResult(proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }
}
