using System.Text;
using Microsoft.Extensions.Configuration;
using Imp.Infrastructure;
using Imp.Research;

namespace Imp.Health;

// Pipeline driver for `imp health`. Orchestrates:
//   window → file enumeration → pre-pass → axis planning → sequential
//   per-file qwen dispatch → cross-cutting untested run → confidence
//   filter → evidence tagging → report write → watermark advance.
//
// See plans/review-mode.md "Orchestrator pipeline" for the canonical
// step list. v1 wires axes 1, 2, 6; everything else is deferred.

public sealed record HealthOptions(
    string? SinceFlag,    // "24h", "3d", or null
    bool SinceLast,       // --since-last
    int? MaxRuns,         // hard cap on qwen invocations
    bool DryRun);

public sealed record HealthOutcome(
    string ReportPath,
    bool WatermarkAdvanced,
    int QwenRunCount,
    int SkippedRunCount);

public static class HealthOrchestrator
{
    public static async Task<HealthOutcome?> RunAsync(
        IConfiguration config,
        string repoRoot,
        HealthOptions opts,
        CancellationToken ct = default)
    {
        var generatedAt = DateTimeOffset.UtcNow;
        var window = Window.Resolve(repoRoot, opts.SinceFlag, opts.SinceLast);
        Console.Error.WriteLine($"[health] window: {window.Description}");
        if (window.WarningMessage is not null)
            Console.Error.WriteLine($"[health] warn: {window.WarningMessage}");

        var touched = FileEnumerator.Enumerate(repoRoot, window);
        Console.Error.WriteLine($"[health] {touched.Count} touched files (after filter)");

        // Empty window: write a minimal report and advance watermark.
        if (touched.Count == 0 || window.IsEmpty)
        {
            var emptyInputs = new ReportInputs(
                GeneratedAt: generatedAt,
                Window: window,
                CommitsInWindow: 0,
                FilesTouched: 0,
                QwenRunCount: 0,
                SkippedRunCount: 0,
                DroppedByConfidence: 0,
                BudgetBanner: null,
                PrePass: null,
                TaggedFindings: Array.Empty<TaggedFinding>(),
                UntestedFindings: Array.Empty<AxisFinding>());
            var emptyPath = WriteReport(repoRoot, generatedAt, emptyInputs);
            Watermark.Write(repoRoot, window.HeadSha);
            return new HealthOutcome(emptyPath, WatermarkAdvanced: true, QwenRunCount: 0, SkippedRunCount: 0);
        }

        var plan = AxisPlanner.Plan(touched);
        Console.Error.WriteLine($"[health] axis plan: {plan.Files.Count} files with axes, {plan.EstimatedRunCount} total runs estimated");

        if (opts.DryRun)
        {
            PrintDryRun(window, touched, plan, opts.MaxRuns);
            return null;
        }

        // --- Pre-pass ---
        Console.Error.WriteLine("[health] pre-pass: building fresh worktree at HEAD…");
        PrePassResult prepass;
        try { prepass = PrePass.Run(repoRoot, window); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[health] pre-pass failed: {ex.GetType().Name}: {ex.Message}");
            prepass = new PrePassResult(
                Array.Empty<Diagnostic>(), Array.Empty<FormatViolation>(),
                BuildError: $"{ex.GetType().Name}: {ex.Message}", FormatError: null, WorktreePath: "");
        }
        Console.Error.WriteLine($"[health] pre-pass: {prepass.AllDiagnostics.Count} diagnostics, {prepass.FormatViolations.Count} format violations");

        // --- Sequential per-file dispatch ---
        var maxRuns = opts.MaxRuns ?? int.MaxValue;
        int runCount = 0;
        int skipped = 0;
        var qwenFindings = new List<AxisFinding>();

        foreach (var (filePlan, axis) in AxisPlanner.DispatchOrder(plan))
        {
            if (runCount >= maxRuns) { skipped++; continue; }
            var file = filePlan.File;

            var modeName = axis switch
            {
                HealthAxis.BugScan => "review-bug",
                HealthAxis.SimplifyComments => "review-simplify-comments",
                _ => throw new InvalidOperationException($"per-file axis cannot be {axis}"),
            };

            var descriptor = BuildPerFileDescriptor(repoRoot, window, file, axis, prepass);
            runCount++;
            Console.Error.WriteLine($"[health] {runCount}/{plan.EstimatedRunCount} {modeName} on {file.RelativePath}");

            try
            {
                var resultJson = await ResearchRunner.RunAsync(
                    config: config,
                    modeName: modeName,
                    descriptor: descriptor,
                    repoRoot: repoRoot,
                    keepArchive: false);

                var finding = ExtractFindings(resultJson, axisLabel: axis == HealthAxis.BugScan ? "bug-scan" : "simplify-comments");
                qwenFindings.AddRange(finding);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[health] axis run failed ({modeName} on {file.RelativePath}): {ex.GetType().Name}: {ex.Message}");
                skipped++;
            }
        }

        // --- Cross-cutting untested axis ---
        var untestedFindings = new List<AxisFinding>();
        if (plan.RunUntestedAxis && runCount < maxRuns)
        {
            try
            {
                var descriptor = BuildUntestedDescriptor(repoRoot, window, touched);
                runCount++;
                Console.Error.WriteLine($"[health] {runCount}/{plan.EstimatedRunCount} review-untested (cross-cutting)");
                var resultJson = await ResearchRunner.RunAsync(
                    config: config,
                    modeName: "review-untested",
                    descriptor: descriptor,
                    repoRoot: repoRoot,
                    keepArchive: false);
                untestedFindings.AddRange(ExtractFindings(resultJson, axisLabel: "untested"));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[health] untested axis failed: {ex.GetType().Name}: {ex.Message}");
                skipped++;
            }
        }
        else if (plan.RunUntestedAxis)
        {
            skipped++;
        }

        // --- Confidence filter ---
        int droppedByConfidence = 0;
        var keptQwen = await FilterByConfidence(config, repoRoot, qwenFindings, ct);
        droppedByConfidence += qwenFindings.Count - keptQwen.Count;
        var keptUntested = await FilterByConfidence(config, repoRoot, untestedFindings, ct);
        droppedByConfidence += untestedFindings.Count - keptUntested.Count;
        Console.Error.WriteLine($"[health] confidence filter: kept {keptQwen.Count + keptUntested.Count}, dropped {droppedByConfidence}");

        // --- Evidence tagging ---
        var tagged = EvidenceTagger.Tag(keptQwen, prepass.AllDiagnostics);

        // --- Build budget banner if we cut things off ---
        string? banner = null;
        var fullCompletion = runCount <= maxRuns && skipped == 0;
        if (!fullCompletion && opts.MaxRuns is { } cap && runCount >= cap)
            banner = $"Budget hit at {runCount}/{plan.EstimatedRunCount} runs ({skipped} skipped); watermark not advanced. Raise --max-runs or narrow window.";

        // --- Write report ---
        var inputs = new ReportInputs(
            GeneratedAt: generatedAt,
            Window: window,
            CommitsInWindow: CountCommits(repoRoot, window),
            FilesTouched: touched.Count,
            QwenRunCount: runCount,
            SkippedRunCount: skipped,
            DroppedByConfidence: droppedByConfidence,
            BudgetBanner: banner,
            PrePass: prepass,
            TaggedFindings: tagged,
            UntestedFindings: keptUntested);
        var reportPath = WriteReport(repoRoot, generatedAt, inputs);
        Console.Error.WriteLine($"[health] report: {reportPath}");

        // --- Cleanup worktree ---
        if (!string.IsNullOrEmpty(prepass.WorktreePath))
        {
            PrePass.Cleanup(repoRoot, prepass.WorktreePath);
        }

        // --- Watermark advance ---
        bool advanced = false;
        if (fullCompletion)
        {
            Watermark.Write(repoRoot, window.HeadSha);
            advanced = true;
            Console.Error.WriteLine($"[health] watermark advanced to {window.HeadSha[..8]}");
        }
        else
        {
            Console.Error.WriteLine("[health] watermark NOT advanced (partial completion)");
        }

        return new HealthOutcome(reportPath, advanced, runCount, skipped);
    }

    static async Task<List<AxisFinding>> FilterByConfidence(
        IConfiguration config, string repoRoot, List<AxisFinding> findings, CancellationToken ct)
    {
        if (findings.Count == 0) return findings;

        var providerName = config["Modes:review-confidence:Provider"] ?? config["ActiveProvider"];
        if (string.IsNullOrEmpty(providerName))
        {
            ImpLog.Warn("review-confidence: no provider configured; skipping filter (keeping all findings)");
            return findings;
        }

        Microsoft.Extensions.AI.IChatClient chat;
        try { chat = Providers.CreateForProvider(config, providerName); }
        catch (Exception ex)
        {
            ImpLog.Warn($"review-confidence: provider construct failed: {ex.Message}; keeping all findings");
            return findings;
        }

        var promptPath = Path.Combine(AppContext.BaseDirectory, "Prompts", "review-confidence.md");
        if (!File.Exists(promptPath))
        {
            ImpLog.Warn($"review-confidence: prompt not found at {promptPath}; keeping all findings");
            return findings;
        }
        var systemPrompt = await File.ReadAllTextAsync(promptPath, ct);

        var kept = new List<AxisFinding>();
        foreach (var af in findings)
        {
            var score = await ConfidenceScorer.ScoreAsync(chat, systemPrompt, af.Finding, ct);
            if (score is null) { kept.Add(af); continue; } // failed → keep, don't drop on tooling error
            if (score.Score >= ConfidenceScorer.DropThreshold) kept.Add(af);
        }
        return kept;
    }

    static List<AxisFinding> ExtractFindings(string resultJson, string axisLabel)
    {
        try
        {
            var doc = System.Text.Json.JsonDocument.Parse(resultJson);
            if (!doc.RootElement.TryGetProperty("report", out var report)
                || report.ValueKind != System.Text.Json.JsonValueKind.Object)
                return new();
            var json = report.GetRawText();
            var parsed = System.Text.Json.JsonSerializer.Deserialize<ResearchReport>(json, ResearchReportJson.Options);
            if (parsed is null) return new();
            return parsed.Findings.Select(f => new AxisFinding(f, axisLabel)).ToList();
        }
        catch (Exception ex)
        {
            ImpLog.Warn($"review: failed to extract findings ({axisLabel}): {ex.Message}");
            return new();
        }
    }

    static TaskDescriptor BuildPerFileDescriptor(
        string repoRoot, HealthWindow window, TouchedFile file, HealthAxis axis, PrePassResult prepass)
    {
        var bg = new StringBuilder();
        bg.AppendLine($"You are reviewing changes to `{file.RelativePath}` in a {window.Description} window.");
        bg.AppendLine();

        // Diff hunk for this file across the window.
        var diff = GetDiffForPath(repoRoot, window, file.RelativePath);
        if (!string.IsNullOrEmpty(diff))
        {
            bg.AppendLine("## Diff in window");
            bg.AppendLine("```diff");
            bg.AppendLine(Truncate(diff, 12_000));
            bg.AppendLine("```");
            bg.AppendLine();
        }

        // Pre-pass SARIF excerpt for this file.
        var fileDiags = prepass.AllDiagnostics
            .Where(d => string.Equals(d.RelativePath, file.RelativePath, StringComparison.Ordinal)
                     || d.RelativePath.EndsWith("/" + file.RelativePath, StringComparison.Ordinal)
                     || file.RelativePath.EndsWith("/" + d.RelativePath, StringComparison.Ordinal))
            .ToList();
        if (fileDiags.Count > 0)
        {
            bg.AppendLine("## Pre-pass diagnostics (analyzer already named these — find what it can't)");
            foreach (var d in fileDiags.Take(40))
                bg.AppendLine($"- L{d.Line} [{d.Severity} {d.Code}]: {d.Message}");
            bg.AppendLine();
        }

        // By-file index page if present in substrate.
        var indexPath = Path.Combine(repoRoot, "imp", "_index", "by-file", file.RelativePath + ".md");
        if (File.Exists(indexPath))
        {
            try
            {
                bg.AppendLine("## What to know first (imp/_index/by-file)");
                bg.AppendLine(Truncate(File.ReadAllText(indexPath), 4_000));
                bg.AppendLine();
            }
            catch { /* ignore */ }
        }

        // Inline file content if small.
        if (file.CurrentLineCount <= 800)
        {
            var abs = Path.Combine(repoRoot, file.RelativePath);
            if (File.Exists(abs))
            {
                try
                {
                    bg.AppendLine($"## Current file content (`{file.RelativePath}`)");
                    bg.AppendLine("```");
                    bg.AppendLine(Truncate(File.ReadAllText(abs), 30_000));
                    bg.AppendLine("```");
                    bg.AppendLine();
                }
                catch { /* ignore */ }
            }
        }
        else
        {
            bg.AppendLine($"File is {file.CurrentLineCount} lines; not inlined. Use `read_file` on specific regions as needed.");
            bg.AppendLine();
        }

        var question = axis switch
        {
            HealthAxis.BugScan =>
                $"Bug-scan `{file.RelativePath}` for correctness issues introduced or exposed by the window's diff. Output findings shaped as leads, not verdicts.",
            HealthAxis.SimplifyComments =>
                $"Run the bundled simplification + comment-drift pass over `{file.RelativePath}`. Output findings shaped as leads, not verdicts.",
            _ => throw new InvalidOperationException($"unsupported axis {axis}"),
        };

        var id = $"Rv-{axis.ToString().ToLowerInvariant()}-{Math.Abs(file.RelativePath.GetHashCode()):x8}";
        var slug = SafeSlug(axis + "-" + file.RelativePath);

        return new TaskDescriptor(
            ResearchId: id,
            Slug: slug,
            Question: question,
            SubQuestions: Array.Empty<string>(),
            SuggestedSources: Array.Empty<string>(),
            Forbidden: Array.Empty<string>(),
            Background: bg.ToString(),
            ExpectedOutput: "",
            SourceMarkdown: "");
    }

    static TaskDescriptor BuildUntestedDescriptor(
        string repoRoot, HealthWindow window, IReadOnlyList<TouchedFile> touched)
    {
        var bg = new StringBuilder();
        bg.AppendLine($"Review window: {window.Description}.");
        bg.AppendLine();

        // List of new public methods. v1 heuristic: grep added lines for
        // `public` declarations across the window. Cheap and lossy — false
        // positives are fine because the qwen pass triages.
        bg.AppendLine("## New / changed public symbols in window");
        int rowCount = 0;
        foreach (var f in touched.Where(t => t.IsProductionCode))
        {
            var diff = GetDiffForPath(repoRoot, window, f.RelativePath);
            if (string.IsNullOrEmpty(diff)) continue;
            foreach (var line in diff.Split('\n'))
            {
                if (!line.StartsWith('+') || line.StartsWith("+++")) continue;
                var stripped = line[1..].TrimStart();
                if (stripped.Contains("public ", StringComparison.Ordinal)
                    && (stripped.Contains('(') || stripped.Contains("class ") || stripped.Contains("record ")))
                {
                    bg.AppendLine($"- `{f.RelativePath}`: `{stripped.Trim()}`");
                    if (++rowCount >= 200) break;
                }
            }
            if (rowCount >= 200) break;
        }
        if (rowCount == 0) bg.AppendLine("_(none detected)_");
        bg.AppendLine();

        // Touched test files (no-op in this repo today but shape is ready).
        bg.AppendLine("## Test files touched in window");
        var testFiles = touched
            .Where(t => t.RelativePath.EndsWith("Tests.cs", StringComparison.Ordinal)
                     || t.RelativePath.Contains("/Tests/", StringComparison.Ordinal))
            .ToList();
        if (testFiles.Count == 0) bg.AppendLine("_(none)_");
        else foreach (var t in testFiles) bg.AppendLine($"- `{t.RelativePath}`");
        bg.AppendLine();

        return new TaskDescriptor(
            ResearchId: "Rv-untested",
            Slug: "review-untested",
            Question: "For each new public symbol, decide whether it's load-bearing enough to want a test. Output one finding per load-bearing untested symbol; skip the rest.",
            SubQuestions: Array.Empty<string>(),
            SuggestedSources: Array.Empty<string>(),
            Forbidden: Array.Empty<string>(),
            Background: bg.ToString(),
            ExpectedOutput: "",
            SourceMarkdown: "");
    }

    static string GetDiffForPath(string repoRoot, HealthWindow window, string relPath)
    {
        var args = new List<string> { "log", "-p", "--no-color", "--pretty=format:" };
        if (window.FromSha is not null) args.Add($"{window.FromSha}..{window.HeadSha}");
        else if (window.DurationHint is not null) args.Add($"--since={window.DurationHint}");
        args.Add("--");
        args.Add(relPath);
        var r = GitCommand.Run(repoRoot, args.ToArray());
        return r.Ok ? r.Stdout : "";
    }

    static int CountCommits(string repoRoot, HealthWindow window)
    {
        var args = new List<string> { "log", "--oneline", "--pretty=format:%H" };
        if (window.FromSha is not null) args.Add($"{window.FromSha}..{window.HeadSha}");
        else if (window.DurationHint is not null) args.Add($"--since={window.DurationHint}");
        var r = GitCommand.Run(repoRoot, args.ToArray());
        if (!r.Ok) return 0;
        return r.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
    }

    static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "\n…[truncated]";

    static string SafeSlug(string raw)
    {
        var sb = new StringBuilder();
        foreach (var c in raw)
        {
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var s = sb.ToString().Trim('-');
        return s.Length > 60 ? s[..60] : s;
    }

    static string WriteReport(string repoRoot, DateTimeOffset generatedAt, ReportInputs inputs)
    {
        var reviewsDir = Path.Combine(repoRoot, "imp", "health");
        Directory.CreateDirectory(reviewsDir);
        var path = Path.Combine(reviewsDir, $"{generatedAt.UtcDateTime:yyyy-MM-dd}.md");
        File.WriteAllText(path, ReportWriter.Render(inputs));
        return path;
    }

    static void PrintDryRun(HealthWindow window, IReadOnlyList<TouchedFile> touched, HealthPlan plan, int? maxRuns)
    {
        Console.WriteLine($"# Health dry-run");
        Console.WriteLine($"Window: {window.Description}");
        Console.WriteLine($"Touched files (after filter): {touched.Count}");
        Console.WriteLine($"Files with axes: {plan.Files.Count}");
        Console.WriteLine($"Estimated qwen runs: {plan.EstimatedRunCount}");
        if (maxRuns is { } cap)
            Console.WriteLine($"--max-runs cap: {cap}  → would skip {Math.Max(0, plan.EstimatedRunCount - cap)} runs");
        Console.WriteLine();
        Console.WriteLine("## Plan");
        foreach (var fp in plan.Files)
        {
            var axes = string.Join(", ", fp.Axes.Select(a => a.ToString()));
            Console.WriteLine($"- `{fp.File.RelativePath}` ({fp.File.CurrentLineCount} lines, +{fp.File.DiffLineCount} diff) → {axes}");
        }
        if (plan.RunUntestedAxis) Console.WriteLine($"- (cross-cutting) review-untested");
    }
}
