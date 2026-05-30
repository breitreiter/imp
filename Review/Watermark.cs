namespace Imp.Review;

// Persists the last fully-reviewed commit SHA at imp/_meta/review-watermark.
// One line, no frontmatter — this is a marker, not a substrate entry.
// "--since-last" reads it; orchestrator advances it only on full completion
// (see plans/review-mode.md, step 10 of orchestrator pipeline).

public static class Watermark
{
    public const string RelativePath = "imp/_meta/review-watermark";

    public static string PathFor(string repoRoot) =>
        Path.Combine(repoRoot, RelativePath);

    public static string? Read(string repoRoot)
    {
        var p = PathFor(repoRoot);
        if (!File.Exists(p)) return null;
        var raw = File.ReadAllText(p).Trim();
        return string.IsNullOrEmpty(raw) ? null : raw;
    }

    public static void Write(string repoRoot, string headSha)
    {
        var p = PathFor(repoRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllText(p, headSha.Trim() + "\n");
    }
}
