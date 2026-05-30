namespace Imp.Review;

// Resolves the review window — the commit range whose diff feeds the per-file
// axes. Two input modes:
//   --since <duration>   (e.g., "24h", "3d") → resolved against HEAD
//   --since-last         → resolved against the watermark
//
// Edge cases (plans/review-mode.md, Window.cs slot):
//   - Watermark SHA unreachable: warn, fall back to "24h"
//   - Empty window or HEAD == watermark: IsEmpty=true, orchestrator writes a
//     minimal report and advances the watermark anyway.
//   - Missing watermark on first run: behave as "24h" from HEAD.

public sealed record ReviewWindow(
    string HeadSha,
    string? FromSha,         // null when DurationHint is set (no concrete watermark)
    string? DurationHint,    // e.g., "24 hours ago"; null when FromSha is set
    bool IsEmpty,            // no commits between FromSha and HeadSha
    string Description,      // human-readable for the report banner
    string? WarningMessage); // populated on watermark-unreachable fallback etc.

public static class Window
{
    public static ReviewWindow Resolve(string repoRoot, string? sinceFlag, bool sinceLast)
    {
        var head = GitCommand.HeadSha(repoRoot)
            ?? throw new InvalidOperationException(
                $"Cannot resolve HEAD in {repoRoot} — not a git repository or empty repo?");

        // --since takes priority when both are passed; otherwise default to
        // --since-last if a watermark exists, else --since=24h.
        if (!string.IsNullOrEmpty(sinceFlag))
            return FromDuration(head, sinceFlag, warning: null);

        if (sinceLast)
        {
            var mark = Watermark.Read(repoRoot);
            if (string.IsNullOrEmpty(mark))
                return FromDuration(head, "24h", warning: "no watermark on disk; falling back to --since 24h");
            if (!GitCommand.RefReachable(repoRoot, mark))
                return FromDuration(head, "24h",
                    warning: $"watermark SHA {mark[..Math.Min(8, mark.Length)]} unreachable (force-push or rebase?); falling back to --since 24h");
            return FromSha(head, mark);
        }

        // Default: --since-last if watermark exists, else 24h.
        var existing = Watermark.Read(repoRoot);
        if (!string.IsNullOrEmpty(existing) && GitCommand.RefReachable(repoRoot, existing))
            return FromSha(head, existing);
        return FromDuration(head, "24h", warning: null);
    }

    static ReviewWindow FromSha(string head, string fromSha)
    {
        var empty = fromSha == head;
        return new ReviewWindow(
            HeadSha: head,
            FromSha: fromSha,
            DurationHint: null,
            IsEmpty: empty,
            Description: empty
                ? $"watermark {fromSha[..Math.Min(8, fromSha.Length)]} == HEAD; no commits to review"
                : $"{fromSha[..Math.Min(8, fromSha.Length)]}..{head[..Math.Min(8, head.Length)]}",
            WarningMessage: null);
    }

    static ReviewWindow FromDuration(string head, string flag, string? warning)
    {
        var asGitSince = TranslateDuration(flag);
        return new ReviewWindow(
            HeadSha: head,
            FromSha: null,
            DurationHint: asGitSince,
            IsEmpty: false, // can't tell without running git log; orchestrator infers from empty file list
            Description: $"--since {flag} (HEAD={head[..Math.Min(8, head.Length)]})",
            WarningMessage: warning);
    }

    // "24h" / "3d" → strings git understands for --since=. Falls through
    // verbatim when the user passes something more elaborate (e.g.
    // "2 hours ago").
    static string TranslateDuration(string flag)
    {
        var t = flag.Trim();
        if (t.EndsWith('h') && int.TryParse(t[..^1], out var h)) return $"{h} hours ago";
        if (t.EndsWith('d') && int.TryParse(t[..^1], out var d)) return $"{d} days ago";
        return t;
    }
}
