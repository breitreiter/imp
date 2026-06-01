using Microsoft.Extensions.AI;
using Imp.Tools;

namespace Imp.Research;

// Research-mode declarations. A mode is a (toolset, sandbox-profile,
// system-prompt, finish-tool) tuple — the toolset is the customization
// axis (consumers swap in their own to get OLAP / Lucene / graph modes
// without forking the loop), and the rest follows from it.
//
// SandboxProfile is the per-mode shape declaration ("I want network=none
// and mount=ro"). The runtime SandboxConfig (Host vs Docker, image, etc.)
// composes with it: in Docker mode the profile becomes structural; in
// Host mode it's documentary, since the structural guarantee already
// comes from the tool surface (a mode that lists no write tool can't
// write, regardless of sandbox).

public enum MountPolicy
{
    None,       // no repo bind-mount (web mode)
    ReadOnly,   // bind-mounted ro (fs mode)
    ReadWrite,  // bind-mounted rw (build mode, today)
}

public sealed record SandboxProfile(
    bool AllowNetwork,
    MountPolicy RepoMount,
    bool AllowSubprocess);

public sealed record ModeDefinition(
    string Name,
    SandboxProfile Sandbox,
    IReadOnlySet<ToolReach> AllowedReach,
    IReadOnlyList<string> ToolNames,
    string SystemPromptFileName,
    Func<ResearchState, AIFunction> FinishToolFactory,
    // Code-side default for which ChatProviders[] entry this mode should run
    // against. Null = use ActiveProvider. Overridable per-run via the config
    // key Modes:<name>:Provider, and (on the CLI) via `--provider <name>`.
    // Resolution priority: CLI flag > Modes:<name>:Provider > PreferredProvider > ActiveProvider.
    string? PreferredProvider = null);

public static class Modes
{
    static readonly Dictionary<string, ModeDefinition> _byName = new(StringComparer.OrdinalIgnoreCase);
    static readonly object _lock = new();
    static bool _initialized;

    static void EnsureInitialized()
    {
        if (_initialized) return;
        lock (_lock)
        {
            if (_initialized) return;
            Register(BuildFsMode());
            Register(BuildWikiMode());
            Register(BuildReviewMode("review-bug", "review-bug.md"));
            Register(BuildReviewMode("review-simplify-comments", "review-simplify-comments.md"));
            Register(BuildReviewMode("review-untested", "review-untested.md"));
            _initialized = true;
        }
    }

    public static void Register(ModeDefinition mode)
    {
        if (_byName.ContainsKey(mode.Name))
            throw new InvalidOperationException($"Mode '{mode.Name}' is already registered.");
        _byName[mode.Name] = mode;
    }

    public static ModeDefinition Get(string name)
    {
        EnsureInitialized();
        if (!_byName.TryGetValue(name, out var mode))
            throw new InvalidOperationException(
                $"No mode registered with name '{name}'. Known modes: {string.Join(", ", _byName.Keys)}.");
        return mode;
    }

    public static IReadOnlyCollection<string> KnownNames()
    {
        EnsureInitialized();
        return _byName.Keys.ToList();
    }

    static ModeDefinition BuildFsMode() => new(
        Name: "fs",
        Sandbox: new SandboxProfile(
            AllowNetwork: false,
            RepoMount: MountPolicy.ReadOnly,
            AllowSubprocess: false),
        AllowedReach: new HashSet<ToolReach> { ToolReach.None, ToolReach.LocalFsRead },
        ToolNames: new[] { "read_file", "grep", "list_dir" },
        SystemPromptFileName: "research-fs.md",
        FinishToolFactory: ResearchTools.BuildFinishResearchTool);

    // Survey-tuned variant of fs. Same toolset, sandbox, and finish tool —
    // the only axis of difference is the system prompt, which frames the run
    // as "describe this directory for a wiki page" instead of "answer a
    // question." Consumed by `imp wiki` (the orchestrator dispatches one run
    // per directory in scope).
    static ModeDefinition BuildWikiMode() => new(
        Name: "wiki",
        Sandbox: new SandboxProfile(
            AllowNetwork: false,
            RepoMount: MountPolicy.ReadOnly,
            AllowSubprocess: false),
        AllowedReach: new HashSet<ToolReach> { ToolReach.None, ToolReach.LocalFsRead },
        ToolNames: new[] { "read_file", "grep", "list_dir" },
        SystemPromptFileName: "research-fs-wiki.md",
        FinishToolFactory: ResearchTools.BuildFinishResearchTool);

    // Review-axis variants of fs. Identical sandbox + tools + finish-tool;
    // differ only in system prompt. The orchestrator (Health/HealthOrchestrator.cs)
    // dispatches one ResearchRunner call per (file, axis) pair using one of these
    // modes. PreferredProvider intentionally null — review is qwen-shaped (cheap,
    // sequential, noisy-but-cheap-to-verify) but users configure via
    // Modes:<name>:Provider rather than baking it in.
    static ModeDefinition BuildReviewMode(string name, string promptFile) => new(
        Name: name,
        Sandbox: new SandboxProfile(
            AllowNetwork: false,
            RepoMount: MountPolicy.ReadOnly,
            AllowSubprocess: false),
        AllowedReach: new HashSet<ToolReach> { ToolReach.None, ToolReach.LocalFsRead },
        ToolNames: new[] { "read_file", "grep", "list_dir" },
        SystemPromptFileName: promptFile,
        FinishToolFactory: ResearchTools.BuildFinishResearchTool);
}
