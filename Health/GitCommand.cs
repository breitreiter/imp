using System.Diagnostics;

namespace Imp.Health;

// Thin wrapper around `git` for review-mode operations. Mirrors the shape of
// Build/Worktree.cs's helpers (concurrent stdout/stderr drain, hard timeout)
// but exposes only the ops review needs: ref resolution, log --name-only with
// --numstat, log -p for a single path, file content at SHA, worktree add/
// remove. Kept local to Health/ so we don't accrete review concerns into
// Build/Worktree.cs (separate domain, see CLAUDE.md layout).

public static class GitCommand
{
    const int TimeoutMs = 30_000;

    public sealed record GitResult(int ExitCode, string Stdout, string Stderr)
    {
        public bool Ok => ExitCode == 0;
    }

    public static GitResult Run(string cwd, params string[] args)
    {
        using var proc = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "git",
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

        if (!proc.WaitForExit(TimeoutMs))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            return new GitResult(-1, "", $"git {string.Join(' ', args)} timed out after {TimeoutMs}ms");
        }
        return new GitResult(proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }

    // True if `ref` resolves to a commit in this repo. Used to detect a
    // watermark that's been orphaned by a force-push or rebase on main.
    public static bool RefReachable(string repoRoot, string @ref) =>
        Run(repoRoot, "rev-parse", "--verify", "--quiet", $"{@ref}^{{commit}}").Ok;

    public static string? HeadSha(string repoRoot)
    {
        var r = Run(repoRoot, "rev-parse", "HEAD");
        return r.Ok ? r.Stdout.Trim() : null;
    }
}
