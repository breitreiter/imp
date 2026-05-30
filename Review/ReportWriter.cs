using System.Text;
using Imp.Research;

namespace Imp.Review;

// Renders the two-zone markdown report at imp/reviews/<date>.md.
// Layout follows the "Report shape" section in plans/review-mode.md.
//
// Engagement-instruction BLUF, mechanical zone assignment from
// EvidenceTagger, verify-against framing on every lead. No integer
// confidence scores — those live in trace.jsonl.

public sealed record ReportInputs(
    DateTimeOffset GeneratedAt,
    ReviewWindow Window,
    int CommitsInWindow,
    int FilesTouched,
    int QwenRunCount,
    int SkippedRunCount,
    int DroppedByConfidence,
    string? BudgetBanner,        // populated when --max-runs cut things off
    PrePassResult? PrePass,
    IReadOnlyList<TaggedFinding> TaggedFindings,
    IReadOnlyList<AxisFinding> UntestedFindings);

public static class ReportWriter
{
    public static string Render(ReportInputs r)
    {
        var sb = new StringBuilder();
        var date = r.GeneratedAt.UtcDateTime.ToString("yyyy-MM-dd");
        sb.AppendLine($"# Review — {date}");
        sb.AppendLine($"Window: {r.Window.Description}");
        sb.AppendLine($"{r.CommitsInWindow} commits, {r.FilesTouched} files, {r.QwenRunCount} qwen runs, {r.SkippedRunCount} skipped, {r.DroppedByConfidence} dropped by confidence filter.");
        if (r.Window.WarningMessage is not null)
            sb.AppendLine($"⚠ {r.Window.WarningMessage}");
        sb.AppendLine();

        if (!string.IsNullOrEmpty(r.BudgetBanner))
        {
            sb.AppendLine("> **Budget banner**");
            sb.AppendLine($"> {r.BudgetBanner}");
            sb.AppendLine();
        }

        var prepassVerdicts = BuildPrePassVerdicts(r.PrePass);
        var qwenVerdicts = r.TaggedFindings.Where(f => f.Zone == ReportZone.Verdict).ToList();
        var corroborated = r.TaggedFindings.Where(f => f.Zone == ReportZone.CorroboratedLead).ToList();
        var singleSource = r.TaggedFindings.Where(f => f.Zone == ReportZone.SingleSourceLead).ToList();

        // --- BLUF ---
        sb.AppendLine("## BLUF");
        sb.AppendLine($"- {prepassVerdicts.Count + qwenVerdicts.Count} verdicts to act on (analyzer + multi-corroborated)");
        sb.AppendLine($"- {corroborated.Count} corroborated leads to verify (qwen + at least one other signal)");
        sb.AppendLine($"- {singleSource.Count} single-source leads to skim (qwen-only, may be noise)");
        sb.AppendLine($"- Untested additions: {r.UntestedFindings.Count} flagged");
        sb.AppendLine("- Trend: (deferred until ≥7 prior reports)");
        sb.AppendLine();

        // --- Verdicts ---
        sb.AppendLine("## Verdicts");
        sb.AppendLine("Stated flatly. Analyzer output plus qwen findings that pre-pass also flagged on the same line/region. Treat as actionable.");
        sb.AppendLine();
        if (prepassVerdicts.Count == 0 && qwenVerdicts.Count == 0)
            sb.AppendLine("_None._");
        int n = 1;
        foreach (var v in prepassVerdicts)
        {
            sb.AppendLine($"{n++}. {v.Description} — `{v.Path}:{v.Line}`");
            sb.AppendLine($"   Source: {v.Source}");
            sb.AppendLine();
        }
        foreach (var f in qwenVerdicts)
        {
            sb.AppendLine($"{n++}. {f.Finding.Claim} — `{f.RelativePath}:{f.Line}`");
            sb.AppendLine($"   Source: {f.SourceAxis} + analyzer (corroborated)");
            RenderTags(sb, f.Tags);
            sb.AppendLine($"   Verify: {f.Finding.Reasoning}");
            RenderExcerpts(sb, f.Finding);
            sb.AppendLine();
        }
        sb.AppendLine();

        // --- Corroborated leads ---
        sb.AppendLine("## Corroborated leads");
        sb.AppendLine("Qwen findings backed by at least one composing signal: multi-axis hit, rules cited, recurring across reports. Treat as worth verifying.");
        sb.AppendLine();
        if (corroborated.Count == 0) sb.AppendLine("_None._");
        n = 1;
        foreach (var f in corroborated)
        {
            sb.AppendLine($"{n++}. {f.Finding.Claim} — `{f.RelativePath}:{f.Line}`");
            sb.AppendLine($"   Source: {f.SourceAxis}");
            RenderTags(sb, f.Tags);
            sb.AppendLine($"   Verify: {f.Finding.Reasoning}");
            RenderExcerpts(sb, f.Finding);
            sb.AppendLine();
        }
        sb.AppendLine();

        // --- Single-source leads ---
        sb.AppendLine("## Single-source leads");
        sb.AppendLine("Qwen-only, one axis, no corroboration. Triage — skim, drop the obvious noise, spot-check the rest.");
        sb.AppendLine();
        if (singleSource.Count == 0) sb.AppendLine("_None._");
        n = 1;
        foreach (var f in singleSource)
        {
            sb.AppendLine($"{n++}. {f.Finding.Claim} — `{f.RelativePath}:{f.Line}`");
            sb.AppendLine($"   Source: {f.SourceAxis}");
            sb.AppendLine($"   Verify: {f.Finding.Reasoning}");
            RenderExcerpts(sb, f.Finding);
            sb.AppendLine();
        }
        sb.AppendLine();

        // --- Untested additions ---
        sb.AppendLine("## Untested additions");
        if (r.UntestedFindings.Count == 0)
            sb.AppendLine("_None flagged this window._");
        else
        {
            n = 1;
            foreach (var u in r.UntestedFindings)
            {
                var first = u.Finding.Citations.FirstOrDefault(c => c.Kind == CitationKind.File);
                var loc = first is null ? "" : $" — `{first.Path}:{first.LineStart}`";
                sb.AppendLine($"{n++}. {u.Finding.Claim}{loc}");
                sb.AppendLine($"   Verify: {u.Finding.Reasoning}");
                sb.AppendLine();
            }
        }
        sb.AppendLine();

        // --- Analyzer pre-pass full output ---
        sb.AppendLine("## Analyzer pre-pass (full)");
        if (r.PrePass is null)
        {
            sb.AppendLine("_Pre-pass did not run._");
        }
        else
        {
            var bySeverity = r.PrePass.AllDiagnostics
                .GroupBy(d => d.Severity, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
            sb.AppendLine($"- Roslyn: {bySeverity.GetValueOrDefault("error")} errors, {bySeverity.GetValueOrDefault("warning")} warnings, {bySeverity.GetValueOrDefault("info")} info.");
            sb.AppendLine($"- dotnet format: {(r.PrePass.FormatViolations.Count == 0 ? "clean" : $"{r.PrePass.FormatViolations.Count} files with violations")}.");
            if (!string.IsNullOrEmpty(r.PrePass.BuildError))
                sb.AppendLine($"- ⚠ Build error: {r.PrePass.BuildError}");
            if (!string.IsNullOrEmpty(r.PrePass.FormatError))
                sb.AppendLine($"- ⚠ Format check error: {r.PrePass.FormatError}");
        }
        sb.AppendLine();

        sb.AppendLine("## Trend");
        sb.AppendLine("_Deferred — needs ≥7 prior reports for comparison._");
        sb.AppendLine();

        return sb.ToString();
    }

    static void RenderTags(StringBuilder sb, IReadOnlyList<string> tags)
    {
        if (tags.Count == 0) return;
        sb.Append("   Tags: ");
        sb.AppendLine(string.Join(", ", tags.Select(t => $"[{t}]")));
    }

    static void RenderExcerpts(StringBuilder sb, Finding f)
    {
        var first = f.Citations.FirstOrDefault();
        if (first is null) return;
        foreach (var excerpt in first.Excerpts.Take(1))
        {
            sb.AppendLine("   ```");
            foreach (var line in excerpt.Split('\n').Take(6))
                sb.Append("   ").AppendLine(line.TrimEnd());
            sb.AppendLine("   ```");
        }
    }

    sealed record PrePassVerdict(string Description, string Path, int Line, string Source);

    static List<PrePassVerdict> BuildPrePassVerdicts(PrePassResult? prepass)
    {
        if (prepass is null) return new();
        // Only errors flow to Verdicts. Warnings/info collapse into the
        // analyzer-pre-pass section as a count to keep the verdict zone tight.
        return prepass.AllDiagnostics
            .Where(d => string.Equals(d.Severity, "error", StringComparison.OrdinalIgnoreCase))
            .Select(d => new PrePassVerdict(
                Description: $"{d.Code}: {d.Message}",
                Path: d.RelativePath,
                Line: d.Line,
                Source: $"Roslyn {d.Code}"))
            .ToList();
    }
}
