using System.Diagnostics;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

using Imp.Build;
using Imp.Tools;
using Imp.Infrastructure;

namespace Imp.Research;

// The bounded tool-call loop for research mode. Mirrors Executor.RunCloseoutAsync
// in shape (system prompt + user brief, fresh history, fresh tool list, single
// finish-tool that captures into per-run state) but with the research-shape
// finish tool, mode-resolved tool list, and report-shape output.
//
// Returns the captured report wrapped with envelope fields (mode, question,
// timing, usage, git provenance) — the top-level dispatcher writes that to
// disk and to stdout.

public static class ResearchExecutor
{
    const int ToolBudgetDefault = 60;
    const int MaxOutputTokensDefault = 16384;

    // When tool-call consumption crosses this fraction of the budget, inject a
    // one-time directive telling the model to stop searching and call
    // finish_research. The existing no-tool-call nudge only fires when the model
    // stops on its own; a model that keeps grepping toward the wall never sees it
    // and gets cut off mid-stride with BlockedCategory.Abandon. This catches that.
    const double BudgetWarnFraction = 0.85;

    public sealed record RunOutcome(
        ResearchReport? Report,
        TerminalState Terminal,
        BlockedQuestion? BlockedReason,
        int Turns);

    public static async Task<RunOutcome> RunAsync(
        IChatClient chat,
        ModeDefinition mode,
        TaskDescriptor descriptor,
        string workingDirectory,
        string traceDirectory,
        string? providerName,
        string? modelName,
        SandboxConfig sandbox,
        IConfiguration config,
        int maxOutputTokens,
        int toolBudget,
        CancellationToken ct)
    {
        var startedAt = DateTime.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        var state = new ResearchState();

        var ctx = new ToolContext(workingDirectory, sandbox, config);
        var resolvedTools = ToolRegistry.ResolveForMode(mode, ctx).ToList();
        resolvedTools.Add(mode.FinishToolFactory(state));

        var systemPrompt = LoadModeSystemPrompt(mode);
        var userPrompt = BuildUserPrompt(descriptor, toolBudget);

        var history = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, userPrompt),
        };

        var options = new ChatOptions
        {
            MaxOutputTokens = maxOutputTokens,
            Tools = resolvedTools,
        };

        var tracePath = Path.Combine(traceDirectory, "trace.jsonl");
        var transcriptPath = Path.Combine(traceDirectory, "transcript.md");
        Directory.CreateDirectory(traceDirectory);

        long tokensIn = 0;
        long tokensOut = 0;
        int turns = 0;
        int toolCalls = 0;
        bool budgetWarned = false;
        TerminalState terminal = TerminalState.Failure;
        BlockedQuestion? blocked = null;

        using var trace = new TraceWriter(tracePath);
        trace.WriteStart(
            taskId: descriptor.ResearchId,
            title: descriptor.Question,
            goal: null,
            provider: providerName,
            worktreePath: workingDirectory,
            branch: "");

        try
        {
            while (true)
            {
                if (toolCalls >= toolBudget)
                {
                    terminal = TerminalState.Blocked;
                    blocked = new BlockedQuestion(
                        BlockedCategory.Abandon,
                        $"Tool-call budget ({toolBudget}) exhausted before finish_research was called.",
                        null);
                    break;
                }

                if (!budgetWarned && state.Captured is null
                    && toolCalls >= (int)(toolBudget * BudgetWarnFraction))
                {
                    history.Add(new ChatMessage(ChatRole.User,
                        $"Budget check: you have used {toolCalls} of {toolBudget} tool calls. Stop searching now and call finish_research with your best-supported findings. A partial-but-cited report beats exhausting the budget and returning nothing."));
                    budgetWarned = true;
                }

                turns++;
                var turnStart = Stopwatch.GetTimestamp();
                ChatResponse response;
                try
                {
                    response = await chat.GetResponseAsync(history, options, ct);
                }
                catch (Exception ex)
                {
                    var turnMs = (long)Stopwatch.GetElapsedTime(turnStart).TotalMilliseconds;
                    trace.WriteTurn(turns, turnMs, null, null, $"exception:{ex.GetType().Name}", false, null);
                    terminal = TerminalState.Blocked;
                    blocked = new BlockedQuestion(
                        BlockedCategory.TransientRetry,
                        $"Chat provider call failed: {ex.GetType().Name}: {ex.Message}",
                        null);
                    break;
                }
                var turnDuration = (long)Stopwatch.GetElapsedTime(turnStart).TotalMilliseconds;

                tokensIn += response.Usage?.InputTokenCount ?? 0;
                tokensOut += response.Usage?.OutputTokenCount ?? 0;

                foreach (var m in response.Messages) history.Add(m);

                var calls = response.Messages
                    .SelectMany(m => m.Contents)
                    .OfType<FunctionCallContent>()
                    .ToList();

                trace.WriteTurn(
                    turns,
                    turnDuration,
                    response.Usage?.InputTokenCount,
                    response.Usage?.OutputTokenCount,
                    response.FinishReason?.ToString(),
                    hadToolCalls: calls.Count > 0,
                    text: response.Text);

                if (calls.Count == 0)
                {
                    if (response.FinishReason == ChatFinishReason.Length)
                    {
                        terminal = TerminalState.Blocked;
                        blocked = new BlockedQuestion(
                            BlockedCategory.RescopeOrCapability,
                            $"Model hit per-turn output-token ceiling ({maxOutputTokens}) at turn {turns} without emitting a tool call.",
                            null);
                        break;
                    }
                    if (response.FinishReason == ChatFinishReason.ContentFilter)
                    {
                        terminal = TerminalState.Blocked;
                        blocked = new BlockedQuestion(
                            BlockedCategory.Abandon,
                            $"Model response blocked by content filter at turn {turns}.",
                            null);
                        break;
                    }
                    // Stopped without calling finish_research. Try once to nudge.
                    if (state.Captured is null)
                    {
                        history.Add(new ChatMessage(ChatRole.User,
                            "You haven't called finish_research yet. Call it now with the structured report. This is your only remaining action."));
                        continue;
                    }
                    break;
                }

                var results = new List<AIContent>();
                foreach (var call in calls)
                {
                    if (toolCalls >= toolBudget) break;

                    var callStart = Stopwatch.GetTimestamp();
                    var result = await InvokeTool(resolvedTools, call, ct);
                    var callMs = (long)Stopwatch.GetElapsedTime(callStart).TotalMilliseconds;
                    var isError = result.StartsWith("ERROR:", StringComparison.Ordinal);

                    trace.WriteToolCall(
                        turn: turns,
                        callId: call.CallId,
                        name: call.Name,
                        args: call.Arguments,
                        success: !isError,
                        durationMs: callMs,
                        resultPreview: TraceWriter.Preview(result, isError: false),
                        error: isError ? TraceWriter.Preview(result, isError: true) : null);

                    results.Add(new FunctionResultContent(call.CallId, result));
                    toolCalls++;
                }

                history.Add(new ChatMessage(ChatRole.Tool, results));

                if (state.SafetyBreach is { } breach)
                {
                    terminal = TerminalState.Blocked;
                    blocked = new BlockedQuestion(
                        Category: breach.Category,
                        Summary: breach.Summary,
                        OffendingInput: breach.OffendingInput);
                    break;
                }

                if (state.Captured is not null)
                {
                    terminal = TerminalState.Success;
                    break;
                }
            }
        }
        finally
        {
            trace.WriteEnd(terminal.ToString().ToLowerInvariant(), toolCalls, turns);
        }

        try
        {
            TranscriptRenderer.Render(tracePath, transcriptPath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[imp] research transcript render failed: {ex.GetType().Name}: {ex.Message}");
        }

        ResearchReport? report = null;
        if (state.Captured is not null)
        {
            stopwatch.Stop();
            var (gitSha, dirty) = TryReadGitProvenance(workingDirectory);
            report = new ResearchReport(
                Mode: mode.Name,
                Question: descriptor.Question,
                StartedAt: startedAt,
                CompletedAt: DateTime.UtcNow,
                Usage: new ResearchUsage(
                    ToolCallCount: toolCalls,
                    TokensIn: tokensIn,
                    TokensOut: tokensOut,
                    WallSeconds: Math.Round(stopwatch.Elapsed.TotalSeconds, 2),
                    EstimatedCostUsd: Pricing.Estimate(modelName, tokensIn, tokensOut)),
                Synthesis: state.Captured.Synthesis,
                Coverage: state.Captured.Coverage,
                Findings: PopulateGitSha(state.Captured.Findings, gitSha),
                Conflicts: (IReadOnlyList<Conflict>?)state.Captured.Conflicts ?? Array.Empty<Conflict>(),
                FollowUps: (IReadOnlyList<string>?)state.Captured.FollowUps ?? Array.Empty<string>(),
                BlockedQuestions: (IReadOnlyList<ResearchBlockedQuestion>?)state.Captured.BlockedQuestions ?? Array.Empty<ResearchBlockedQuestion>(),
                WorktreeDirty: dirty,
                GitSha: gitSha);
        }

        return new RunOutcome(report, terminal, blocked, turns);
    }

    static async Task<string> InvokeTool(IList<AITool> tools, FunctionCallContent call, CancellationToken ct)
    {
        var tool = tools.OfType<AIFunction>().FirstOrDefault(t => t.Name == call.Name);
        if (tool is null) return $"ERROR: unknown tool '{call.Name}'";
        try
        {
            var args = new AIFunctionArguments(call.Arguments);
            var result = await tool.InvokeAsync(args, ct);
            return result?.ToString() ?? "";
        }
        catch (Exception ex)
        {
            return $"ERROR: tool '{call.Name}' threw: {ex.GetType().Name}: {ex.Message}";
        }
    }

    static string LoadModeSystemPrompt(ModeDefinition mode)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Prompts", mode.SystemPromptFileName);
        if (!File.Exists(path))
            throw new FileNotFoundException(
                $"System prompt for mode '{mode.Name}' not found at {path}.");
        return File.ReadAllText(path);
    }

    static string BuildUserPrompt(TaskDescriptor d, int toolBudget)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# Research brief");
        sb.AppendLine();
        sb.Append("**Question:** ").AppendLine(d.Question);
        sb.AppendLine();
        sb.Append("**Tool-call budget:** ").Append(toolBudget).AppendLine(" total (read_file / grep / list_dir / finish_research combined). Apply the convergence cadence from the system prompt: by ~50% you should have a working answer, by ~75% you should be writing the report. Pre-commit beats timing out.");
        if (d.SubQuestions.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("**Sub-questions:**");
            foreach (var q in d.SubQuestions) sb.Append("- ").AppendLine(q);
        }
        if (d.SuggestedSources.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("**Suggested starting points:**");
            foreach (var s in d.SuggestedSources) sb.Append("- ").AppendLine(s);
        }
        if (d.Forbidden.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("**Out of scope (do not explore):**");
            foreach (var f in d.Forbidden) sb.Append("- ").AppendLine(f);
        }
        if (!string.IsNullOrWhiteSpace(d.Background))
        {
            sb.AppendLine();
            sb.AppendLine("**Background:**");
            sb.AppendLine(d.Background.Trim());
        }
        if (!string.IsNullOrWhiteSpace(d.ExpectedOutput))
        {
            sb.AppendLine();
            sb.AppendLine("**Expected output:**");
            sb.AppendLine(d.ExpectedOutput.Trim());
        }
        sb.AppendLine();
        sb.AppendLine("Begin researching. Call `finish_research` once you can answer the question with cited evidence.");
        return sb.ToString();
    }

    // Reads HEAD's commit SHA and whether the worktree has uncommitted changes.
    // The git_sha is stamped onto every file-citation so the report carries
    // provenance the parent can re-fetch against. Failures degrade to (null, null) —
    // a non-git directory shouldn't crash a research run.
    static (string? Sha, bool? Dirty) TryReadGitProvenance(string cwd)
    {
        try
        {
            var sha = RunGit(cwd, "rev-parse", "HEAD")?.Trim();
            var status = RunGit(cwd, "status", "--porcelain");
            var dirty = status is { Length: > 0 } && !string.IsNullOrWhiteSpace(status);
            return (sha, dirty);
        }
        catch
        {
            return (null, null);
        }
    }

    static IReadOnlyList<Finding> PopulateGitSha(IReadOnlyList<Finding> findings, string? sha)
    {
        if (sha is null) return findings;
        return findings.Select(f => f with
        {
            Citations = f.Citations.Select(c => c.Kind == CitationKind.File && c.GitSha is null
                ? c with { GitSha = sha }
                : c).ToList(),
        }).ToList();
    }

    static string? RunGit(string cwd, params string[] args)
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
        var stdout = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(10_000);
        return proc.ExitCode == 0 ? stdout : null;
    }

    public static int DefaultToolBudget => ToolBudgetDefault;
    public static int DefaultMaxOutputTokens => MaxOutputTokensDefault;
}
