using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Imp.Infrastructure;

// Append-only JSONL sidecar. One line per event, flushed immediately so an
// ungraceful exit still preserves what we observed up to the crash.
//
// Event types: start | turn | tool_call | directive | end
//
// Design rule: the worktree carries file content; the trace carries signal.
// Tool results aren't logged verbatim (the worktree diff tells that story).
// What IS logged:
//   - mutations and bash: full args (so we can reconstruct intent)
//   - errors: full error text (so we can diagnose)
//   - reads/searches: args_hash only (cheap dedup + loop-detection signal)

public sealed class TraceWriter : IDisposable
{
    const int ResultPreviewChars = 200;
    const int ErrorPreviewChars = 2000;
    const int TurnTextPreviewChars = 4000;

    static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    readonly StreamWriter _writer;

    public TraceWriter(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        // UTF-8 without BOM — Encoding.UTF8 emits a BOM which breaks JSONL parsers on line 1.
        _writer = new StreamWriter(filePath, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true };
    }

    public void WriteStart(string taskId, string? title, string? goal, string? provider, string worktreePath, string branch)
        => Write(new
        {
            type = "start",
            version = "1",
            timestamp = DateTime.UtcNow,
            task_id = taskId,
            title,
            goal,
            provider,
            worktree_path = worktreePath,
            branch,
        });

    public void WriteTurn(int n, long durationMs, long? tokensIn, long? tokensOut, string? finishReason, bool hadToolCalls, string? text)
        => Write(new
        {
            type = "turn",
            timestamp = DateTime.UtcNow,
            n,
            duration_ms = durationMs,
            tokens_in = tokensIn,
            tokens_out = tokensOut,
            finish_reason = finishReason,
            had_tool_calls = hadToolCalls,
            text = TruncateText(text, TurnTextPreviewChars),
        });

    public void WriteToolCall(
        int turn,
        string callId,
        string name,
        IDictionary<string, object?>? args,
        bool success,
        long durationMs,
        string? resultPreview,
        string? error)
    {
        var argsJson = args is null ? "{}" : JsonSerializer.Serialize(args, Options);
        var hash = ShortHash(argsJson);
        var shouldLogArgs = IsMutationOrCommand(name) || error != null;

        Write(new
        {
            type = "tool_call",
            timestamp = DateTime.UtcNow,
            turn,
            call_id = callId,
            name,
            args_hash = hash,
            args = shouldLogArgs ? argsJson : null,
            success,
            duration_ms = durationMs,
            result_preview = resultPreview,
            error,
        });
    }

    // A steering message the executor injects into the conversation (not a
    // model turn or tool call) — e.g. the budget-proximity warn. Recorded so a
    // trace reader can see it fired and at what call count, which is exactly
    // what tuning BudgetWarnFraction from observed traces needs.
    public void WriteDirective(string kind, string message, int afterTurn, int toolCalls, int toolBudget)
        => Write(new
        {
            type = "directive",
            timestamp = DateTime.UtcNow,
            kind,
            after_turn = afterTurn,
            tool_calls = toolCalls,
            tool_budget = toolBudget,
            message = TruncateText(message, TurnTextPreviewChars),
        });

    public void WriteEnd(string terminalState, int toolCallCount, int turnCount)
        => Write(new
        {
            type = "end",
            timestamp = DateTime.UtcNow,
            terminal_state = terminalState,
            tool_call_count = toolCallCount,
            turn_count = turnCount,
        });

    public static string Preview(string? result, bool isError)
    {
        if (string.IsNullOrEmpty(result)) return "";
        var limit = isError ? ErrorPreviewChars : ResultPreviewChars;
        return result.Length <= limit ? result : result[..limit] + $"… [+{result.Length - limit} chars]";
    }

    static string? TruncateText(string? text, int budget)
    {
        if (string.IsNullOrEmpty(text)) return null;
        return text.Length <= budget ? text : text[..budget] + $"… [+{text.Length - budget} chars]";
    }

    void Write(object entry)
    {
        var json = JsonSerializer.Serialize(entry, Options);
        _writer.WriteLine(json);
    }

    public void Dispose() => _writer.Dispose();

    static bool IsMutationOrCommand(string toolName) => toolName switch
    {
        // Mutations: we want full args for forensics (what did it try to write).
        "write_file" or "edit_file" or "apply_patch" or "bash" => true,
        // `finish_work` isn't a mutation but its args ARE load-bearing —
        // they carry the self-check / closeout verdicts. Logging the hash
        // only loses the content we most want to debug.
        "finish_work" => true,
        // `finish_research` carries the entire research report; same
        // reasoning — the args ARE what we want to debug.
        "finish_research" => true,
        _ => false,
    };

    static string ShortHash(string input)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..12];
    }
}
