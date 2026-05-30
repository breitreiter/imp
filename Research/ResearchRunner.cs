using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

using Imp.Build;
using Imp.Infrastructure;

namespace Imp.Research;

// Top-level orchestration for `imp research`. Mirrors LifecycleCommands.Build:
//   - resolve the cwd as a git repo
//   - construct the descriptor (from --brief or free-text)
//   - allocate the archive directory
//   - run the executor
//   - write brief.md / report.json / findings.jsonl / meta.json
//   - emit a ResearchResult envelope on stdout
//
// The executor itself lives in ResearchExecutor; this file is the glue.

public record ResearchResult(
    [property: JsonPropertyName("research_id")] string ResearchId,
    [property: JsonPropertyName("mode")] string Mode,
    [property: JsonPropertyName("question")] string Question,
    [property: JsonPropertyName("terminal_state")] TerminalState TerminalState,
    [property: JsonPropertyName("report")] ResearchReport? Report,
    [property: JsonPropertyName("blocked_reason")] BlockedQuestion? BlockedReason,
    [property: JsonPropertyName("archive_dir")] string? ArchiveDir,
    [property: JsonPropertyName("trace_path")] string? TracePath,
    [property: JsonPropertyName("transcript_path")] string? TranscriptPath);

public static class ResearchRunner
{
    // CLI entry: free-text question or --brief markdown. Resolves the cwd
    // as a repo, parses the descriptor, and delegates to the in-memory
    // overload below.
    public static async Task<string> RunAsync(
        IConfiguration config,
        string modeName,
        string? freeTextQuestion,
        string? briefPath,
        bool keepArchive = false,
        string? providerOverride = null)
    {
        ImpLog.Info($"research: start mode={modeName} brief={briefPath ?? "<free-text>"} cwd={Directory.GetCurrentDirectory()}");

        var (repoRoot, repoError) = ResolveRepoRoot();
        if (repoRoot is null)
            return SerializeError(modeName, "repo-resolve", repoError!);

        TaskDescriptor descriptor;
        try
        {
            descriptor = !string.IsNullOrEmpty(briefPath)
                ? BriefParser.ParseFile(briefPath, repoRoot)
                : BriefParser.FromFreeText(freeTextQuestion ?? "", repoRoot);
        }
        catch (Exception ex)
        {
            return SerializeError(modeName, "brief-parse", ex.Message);
        }

        return await RunAsync(config, modeName, descriptor, repoRoot, keepArchive: keepArchive, providerOverride: providerOverride);
    }

    // In-memory entry: caller has already constructed a descriptor and
    // resolved repoRoot. Used by `imp wiki`, which dispatches one research
    // run per in-scope directory and doesn't want to round-trip through
    // markdown brief files. toolBudgetOverride lets the wiki orchestrator
    // pass `Wiki:ToolBudget` (tighter, scope-bounded) instead of the
    // ad-hoc `Research:ToolBudget`; null falls back to the Research budget
    // so existing CLI behaviour is unchanged.
    public static async Task<string> RunAsync(
        IConfiguration config,
        string modeName,
        TaskDescriptor descriptor,
        string repoRoot,
        int? toolBudgetOverride = null,
        bool keepArchive = true,
        string? providerOverride = null)
    {
        ModeDefinition mode;
        try
        {
            mode = Modes.Get(modeName);
        }
        catch (Exception ex)
        {
            return SerializeError(modeName, "mode-resolve", ex.Message);
        }

        var archiveDir = ResearchArchive.DirectoryFor(repoRoot, descriptor);
        Directory.CreateDirectory(archiveDir);
        ResearchArchive.WriteBrief(archiveDir, descriptor);

        var providerName = ResolveProviderName(config, mode, providerOverride);
        if (string.IsNullOrEmpty(providerName))
            return SerializeError(modeName, "provider-resolve",
                "No provider configured. Set ActiveProvider, Modes:<name>:Provider, or pass --provider.");

        IChatClient chat;
        try
        {
            chat = Providers.CreateForProvider(config, providerName);
        }
        catch (Exception ex)
        {
            return SerializeError(modeName, "provider-construct", ex.Message);
        }

        var providerSection = ResolveProviderSection(config, providerName);
        var modelName = providerSection?["Model"];
        var maxOutputTokens = ParseInt(providerSection, "MaxOutputTokens") ?? ResearchExecutor.DefaultMaxOutputTokens;
        var sandbox = SandboxConfig.FromConfiguration(config);
        var toolBudget = toolBudgetOverride
            ?? ParseInt(config.GetSection("Research"), "ToolBudget")
            ?? ResearchExecutor.DefaultToolBudget;

        ImpLog.Info($"research: executor starting researchId={descriptor.ResearchId} mode={mode.Name} provider={providerName} model={modelName}");

        var outcome = await ResearchExecutor.RunAsync(
            chat: chat,
            mode: mode,
            descriptor: descriptor,
            workingDirectory: repoRoot,
            traceDirectory: archiveDir,
            providerName: providerName,
            modelName: modelName,
            sandbox: sandbox,
            config: config,
            maxOutputTokens: maxOutputTokens,
            toolBudget: toolBudget,
            ct: CancellationToken.None);

        ImpLog.Info($"research: executor completed researchId={descriptor.ResearchId} terminal={outcome.Terminal} turns={outcome.Turns}");

        if (outcome.Report is not null)
            ResearchArchive.WriteReport(archiveDir, outcome.Report);

        ResearchArchive.WriteMeta(archiveDir, descriptor, mode.Name, providerName, modelName, outcome.Report, outcome.Terminal);

        // Default is to delete the archive on successful completion — research
        // is one-shot by shape, and accumulated sidecars discourage casual use.
        // Wiki dispatch passes keepArchive: true (it reads the archive later).
        // On exception we leave the archive in place so failures stay
        // debuggable; the delete only fires on the success path here.
        if (!keepArchive)
        {
            try { Directory.Delete(archiveDir, recursive: true); }
            catch (Exception ex)
            {
                ImpLog.Warn($"research: failed to clean up archive {archiveDir}: {ex.Message}");
            }
        }

        var result = new ResearchResult(
            ResearchId: descriptor.ResearchId,
            Mode: mode.Name,
            Question: descriptor.Question,
            TerminalState: outcome.Terminal,
            Report: outcome.Report,
            BlockedReason: outcome.BlockedReason,
            ArchiveDir: keepArchive ? archiveDir : null,
            TracePath: keepArchive ? Path.Combine(archiveDir, "trace.jsonl") : null,
            TranscriptPath: keepArchive ? Path.Combine(archiveDir, "transcript.md") : null);

        return JsonSerializer.Serialize(result, ResultOptions);
    }

    static IConfigurationSection? ResolveProviderSection(IConfiguration config, string? activeProvider)
    {
        if (string.IsNullOrEmpty(activeProvider)) return null;
        return config.GetSection("ChatProviders").GetChildren()
            .FirstOrDefault(p => string.Equals(p["Name"], activeProvider, StringComparison.OrdinalIgnoreCase));
    }

    // Resolution priority for which ChatProviders[] entry to run a research
    // mode against:
    //   1. CLI override (`--provider <name>`).
    //   2. Per-mode config key `Modes:<name>:Provider`.
    //   3. Code-side ModeDefinition.PreferredProvider.
    //   4. `Wiki:Provider` for the deprecated wiki mode only (back-compat;
    //      dies with the wiki mode per plans/wiki-deprecation.md).
    //   5. Global `ActiveProvider`.
    // Returns null only if nothing is set at any layer.
    static string? ResolveProviderName(IConfiguration config, ModeDefinition mode, string? providerOverride)
    {
        if (!string.IsNullOrEmpty(providerOverride)) return providerOverride;
        var modeKey = config[$"Modes:{mode.Name}:Provider"];
        if (!string.IsNullOrEmpty(modeKey)) return modeKey;
        if (!string.IsNullOrEmpty(mode.PreferredProvider)) return mode.PreferredProvider;
        if (string.Equals(mode.Name, "wiki", StringComparison.OrdinalIgnoreCase))
        {
            var legacy = config["Wiki:Provider"];
            if (!string.IsNullOrEmpty(legacy)) return legacy;
        }
        return config["ActiveProvider"];
    }

    static int? ParseInt(IConfiguration? section, string key)
    {
        var raw = section?[key];
        return int.TryParse(raw, out var n) && n > 0 ? n : null;
    }

    // Same git-repo check as LifecycleCommands.ResolveTargetRepo but local — research
    // runs against the current checkout, not a worktree.
    static (string? Resolved, string? Error) ResolveRepoRoot()
    {
        string candidate;
        try { candidate = Path.GetFullPath(Directory.GetCurrentDirectory()); }
        catch (Exception ex) { return (null, $"Could not resolve cwd: {ex.Message}"); }

        if (!Directory.Exists(candidate))
            return (null, $"cwd does not exist: {candidate}");

        var dotGit = Path.Combine(candidate, ".git");
        if (!Directory.Exists(dotGit) && !File.Exists(dotGit))
            return (null, $"cwd is not a git repository (no .git entry): {candidate}");

        return (candidate, null);
    }

    static string SerializeError(string mode, string stage, string message)
    {
        var stub = new ResearchResult(
            ResearchId: "R-???",
            Mode: mode,
            Question: "",
            TerminalState: TerminalState.Rejected,
            Report: null,
            BlockedReason: new BlockedQuestion(BlockedCategory.ReviseContract, $"{stage}: {message}", null),
            ArchiveDir: "",
            TracePath: "",
            TranscriptPath: "");
        return JsonSerializer.Serialize(stub, ResultOptions);
    }

    static readonly JsonSerializerOptions ResultOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DictionaryKeyPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
