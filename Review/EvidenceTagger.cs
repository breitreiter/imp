using Imp.Research;

namespace Imp.Review;

// Composes evidence tags per qwen finding and assigns each to a report zone
// (verdict / corroborated-lead / single-source). See "Evidence tags" in
// plans/review-mode.md. Tag count is the credibility signal — no integer
// scores reach the report.
//
// v1 tags:
//   - analyzer-corroborated — pre-pass diagnostic on same file:line region.
//     Promotes the finding to the verdict zone.
//   - multi-axis — same file flagged by >1 qwen axis at adjacent line.
//
// Deferred (v2+):
//   - rules-cited (needs review-rules axis)
//   - recurring (needs ≥7 prior reports)

public enum ReportZone { Verdict, CorroboratedLead, SingleSourceLead }

public sealed record TaggedFinding(
    Finding Finding,
    string SourceAxis,       // "bug-scan" | "simplify-comments" | "untested"
    string RelativePath,
    int Line,
    IReadOnlyList<string> Tags,
    ReportZone Zone);

public sealed record AxisFinding(Finding Finding, string SourceAxis);

public static class EvidenceTagger
{
    const int LineProximity = 5;

    public static List<TaggedFinding> Tag(
        IReadOnlyList<AxisFinding> qwenFindings,
        IReadOnlyList<Diagnostic> prePassDiagnostics)
    {
        // Index pre-pass diagnostics by file for O(1) per-finding lookup.
        var diagByPath = prePassDiagnostics
            .GroupBy(d => d.RelativePath, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        // Per-file index of (axis, line) tuples for multi-axis detection.
        var findingsByPath = new Dictionary<string, List<(string Axis, int Line)>>(StringComparer.Ordinal);
        var prepared = new List<(AxisFinding Source, string Path, int Line)>();
        foreach (var af in qwenFindings)
        {
            var first = af.Finding.Citations.FirstOrDefault(c => c.Kind == CitationKind.File);
            if (first is null || string.IsNullOrEmpty(first.Path)) continue;
            var line = first.LineStart ?? 0;
            prepared.Add((af, first.Path!, line));
            if (!findingsByPath.TryGetValue(first.Path!, out var list))
                findingsByPath[first.Path!] = list = new();
            list.Add((af.SourceAxis, line));
        }

        var tagged = new List<TaggedFinding>();
        foreach (var (af, path, line) in prepared)
        {
            var tags = new List<string>();

            if (diagByPath.TryGetValue(path, out var diags) &&
                diags.Any(d => Math.Abs(d.Line - line) <= LineProximity))
            {
                tags.Add("analyzer-corroborated");
            }

            // Multi-axis: another finding from a *different* axis sits within
            // proximity on the same file.
            if (findingsByPath.TryGetValue(path, out var siblings) &&
                siblings.Any(s => s.Axis != af.SourceAxis && Math.Abs(s.Line - line) <= LineProximity))
            {
                tags.Add($"multi-axis:{af.SourceAxis}");
            }

            var zone = tags.Contains("analyzer-corroborated")
                ? ReportZone.Verdict
                : tags.Count > 0
                    ? ReportZone.CorroboratedLead
                    : ReportZone.SingleSourceLead;

            tagged.Add(new TaggedFinding(
                Finding: af.Finding,
                SourceAxis: af.SourceAxis,
                RelativePath: path,
                Line: line,
                Tags: tags,
                Zone: zone));
        }
        return tagged;
    }
}
