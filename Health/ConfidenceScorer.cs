using Microsoft.Extensions.AI;
using Imp.Infrastructure;
using Imp.Research;

namespace Imp.Health;

// Internal-only confidence filter. Score never reaches the report
// (plans/review-mode.md — surfacing an integer invites anchoring).
// Findings scoring < DropThreshold are dropped before evidence tagging.
//
// Single tool registered (finish_confidence) — no read_file. The brief
// hands qwen the citation excerpts already; re-reading the file was
// optional in the design and dropping it both simplifies the schema (one
// less surface where Qwen's OpenAI-compat endpoint can 400) and cuts the
// per-finding cost roughly in half. If signal degrades we can re-introduce
// read access, but ship-and-learn says start narrow.

public sealed record ConfidenceScore(int Score, string Justification);

public static class ConfidenceScorer
{
    public const int DropThreshold = 80;

    public static async Task<ConfidenceScore?> ScoreAsync(
        IChatClient chat,
        string systemPrompt,
        Finding finding,
        CancellationToken ct = default)
    {
        var state = new ResearchTools.ConfidenceState();
        var finishTool = ResearchTools.BuildFinishConfidenceTool(state);

        var history = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, BuildBrief(finding)),
        };
        var options = new ChatOptions
        {
            MaxOutputTokens = 4096,
            Tools = new List<AITool> { finishTool },
        };

        // Up to 3 round-trips: initial call → tool call → optional retry
        // nudge if the model stopped without calling the tool. Qwen
        // sometimes hedges; one nudge is usually enough.
        for (int turn = 0; turn < 3; turn++)
        {
            ChatResponse response;
            try { response = await chat.GetResponseAsync(history, options, ct); }
            catch (Exception ex)
            {
                ImpLog.Warn($"confidence: chat call failed: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
            foreach (var m in response.Messages) history.Add(m);

            var calls = response.Messages.SelectMany(m => m.Contents).OfType<FunctionCallContent>().ToList();
            if (calls.Count == 0)
            {
                if (state.Score is not null) break;
                history.Add(new ChatMessage(ChatRole.User,
                    "Call finish_confidence(score, justification) now. This is your only remaining action."));
                continue;
            }

            var results = new List<AIContent>();
            foreach (var call in calls)
            {
                if (call.Name != "finish_confidence")
                {
                    results.Add(new FunctionResultContent(call.CallId,
                        $"ERROR: only finish_confidence is available in this pass."));
                    continue;
                }
                var args = new AIFunctionArguments(call.Arguments);
                try
                {
                    var r = await finishTool.InvokeAsync(args, ct);
                    results.Add(new FunctionResultContent(call.CallId, r?.ToString() ?? ""));
                }
                catch (Exception ex)
                {
                    results.Add(new FunctionResultContent(call.CallId,
                        $"ERROR: {ex.GetType().Name}: {ex.Message}"));
                }
            }
            history.Add(new ChatMessage(ChatRole.Tool, results));
            if (state.Score is not null) break;
        }

        return state.Score is { } s
            ? new ConfidenceScore(s, state.Justification)
            : null;
    }

    static string BuildBrief(Finding finding)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# Score this finding");
        sb.AppendLine();
        sb.Append("**Claim:** ").AppendLine(finding.Claim);
        sb.AppendLine();
        sb.Append("**Reasoning (verify-against hint):** ").AppendLine(finding.Reasoning);
        sb.AppendLine();
        sb.AppendLine("**Citations:**");
        foreach (var c in finding.Citations)
        {
            if (c.Kind == CitationKind.File)
                sb.AppendLine($"- {c.Path}:{c.LineStart}-{c.LineEnd}");
            else if (c.Kind == CitationKind.Url)
                sb.AppendLine($"- {c.Url}");
            foreach (var excerpt in c.Excerpts)
            {
                sb.AppendLine("  ```");
                foreach (var line in excerpt.Split('\n'))
                    sb.Append("  ").AppendLine(line);
                sb.AppendLine("  ```");
            }
        }
        sb.AppendLine();
        sb.AppendLine("Score per the rubric. Call finish_confidence exactly once.");
        return sb.ToString();
    }
}
