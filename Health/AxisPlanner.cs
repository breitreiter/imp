namespace Imp.Health;

// Pure function over (path, size, diff-size) → set of axes that fire.
// Same code feeds --dry-run and the real dispatch. Thresholds documented
// inline; revisit after first real run rather than calibrating in advance
// (see [[feedback_ship_derivative_v1]] in memory).

public enum HealthAxis
{
    BugScan,            // axis 1 — per-file
    SimplifyComments,   // axis 2 — per-file (bundled)
    Untested,           // axis 6 — cross-cutting, planned once
}

public sealed record FilePlan(
    TouchedFile File,
    IReadOnlyList<HealthAxis> Axes);

public sealed record HealthPlan(
    IReadOnlyList<FilePlan> Files,
    bool RunUntestedAxis,
    int EstimatedRunCount);

public static class AxisPlanner
{
    const int SimplifyFileLineFloor = 100;
    const int SimplifyDiffLineFloor = 50;

    public static HealthPlan Plan(IReadOnlyList<TouchedFile> touched)
    {
        var files = new List<FilePlan>();
        foreach (var f in touched)
        {
            var axes = new List<HealthAxis>();

            // Bug-scan: any production-code file with ≥1 line of change.
            if (f.IsProductionCode && f.DiffLineCount >= 1)
                axes.Add(HealthAxis.BugScan);

            // Simplify+comments: file >100 lines OR diff >50 lines.
            if (f.IsProductionCode &&
                (f.CurrentLineCount > SimplifyFileLineFloor ||
                 f.DiffLineCount > SimplifyDiffLineFloor))
                axes.Add(HealthAxis.SimplifyComments);

            if (axes.Count > 0)
                files.Add(new FilePlan(f, axes));
        }

        var runUntested = touched.Count > 0;
        var perFile = files.Sum(f => f.Axes.Count);
        var total = perFile + (runUntested ? 1 : 0);

        return new HealthPlan(files, runUntested, total);
    }

    // Sort order for sequential dispatch: largest diff first (more surface
    // area per run; early exit yields high-coverage findings). Within a file,
    // SimplifyComments before BugScan — simplify reads with a wider lens and
    // tends to surface drift the bug-scan brief then references.
    //
    // Interleaved per-file rather than per-axis so that --max-runs cutting
    // the run short yields *balanced* coverage (some bugs + some simplify
    // findings across a few files), not "all simplify findings, no bugs."
    // The original per-axis ordering starved bug-scan when budget bit.
    public static IEnumerable<(FilePlan File, HealthAxis Axis)> DispatchOrder(HealthPlan plan)
    {
        foreach (var fp in plan.Files.OrderByDescending(f => f.File.DiffLineCount))
        {
            if (fp.Axes.Contains(HealthAxis.SimplifyComments))
                yield return (fp, HealthAxis.SimplifyComments);
            if (fp.Axes.Contains(HealthAxis.BugScan))
                yield return (fp, HealthAxis.BugScan);
        }
    }
}
