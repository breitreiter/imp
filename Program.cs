using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;

using Imp.Build;
using Imp.Research;
using Imp.Review;
using Imp.Substrate;
using Imp.Wiki;
using Imp.Infrastructure;

namespace Imp;

// CLI entry point. One process per invocation, scoped to cwd.
//
// Subcommands fall into three groups:
//   build / validate / review     — the core delegation lifecycle
//   list / show / log / template  — read-only inspection of contracts and artifacts
//   ping / ping-tools / render-transcript — diagnostics
//
// Most subcommands delegate to static methods in LifecycleCommands. The `review`
// subcommand and the diagnostic helpers are handled here.

public class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            return await Dispatch(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"imp: unhandled error: {ex.GetType().Name}: {ex.Message}");
            ImpLog.Error($"unhandled: {ex.GetType().Name}: {ex.Message}");
            return 2;
        }
    }

    static async Task<int> Dispatch(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 1;
        }

        return args[0] switch
        {
            "build" => await RunBuild(args[1..]),
            "research" => await RunResearch(args[1..]),
            "init" => ProjectInit.Run(args[1..]),
            "signals" => Signals.Run(args[1..]),
            "migrate" => Migrate.Run(args[1..]),
            "note" => Note.Run(args[1..]),
            "tidy" => await RunTidy(args[1..]),
            "embed-refresh" => await RunEmbedRefresh(args[1..]),
            "locate" => await RunLocate(args[1..]),
            "wiki" => await RunWiki(args[1..]),
            "wiki-render-test" => RunWikiRenderTest(args[1..]),
            "wiki-index-test" => RunWikiIndexTest(args[1..]),
            "wiki-split-test" => await RunWikiSplitTest(args[1..]),
            "validate" => await RunValidate(args[1..]),
            "review" => await RunReviewDispatch(args[1..]),
            "list" => RunList(),
            "show" => RunShow(args[1..]),
            "log" => RunLog(args[1..]),
            "template" => RunTemplate(args[1..]),
            "ping" => await RunPing(args[1..]),
            "ping-tools" => await RunPingTools(args[1..]),
            "ping-embeddings" => await RunPingEmbeddings(args[1..]),
            "render-transcript" => RunRenderTranscript(args[1..]),
            "help" or "--help" or "-h" => PrintUsageAndExit(0),
            _ => UnknownCommand(args[0]),
        };
    }

    static int UnknownCommand(string cmd)
    {
        Console.Error.WriteLine($"imp: unknown command '{cmd}'. Run `imp help` for usage.");
        return 1;
    }

    static int PrintUsageAndExit(int code)
    {
        PrintUsage();
        return code;
    }

    static void PrintUsage()
    {
        Console.WriteLine("""
Usage: imp <command> [args]

Lifecycle:
  build <contract-path> [provider]   Run the executor against a contract.
                                     Long-running (minutes to tens of minutes).
                                     Emits proof-of-work JSON to stdout.
  research --mode=<m> "question"     Cheap-executor research run. --mode=fs reads the
    [--brief path]                   current checkout; --brief points at a structured
    [--archive]                      brief markdown file. Emits report JSON to stdout.
    [provider]                       --archive keeps the trace + transcript at
                                     <repo>.researches/R-NNN-<slug>/ for debugging;
                                     by default the archive is deleted on successful
                                     completion (kept on exception). Crashed runs
                                     always leave the archive behind for inspection.
  wiki [path] [--dry-run]            DEPRECATED, pending removal. Superseded by the
    [--full] [--resume W-NNN]        substrate (`imp/`, via `imp tidy`). Still works;
                                     prints a stderr warning on use. See
                                     plans/wiki-deprecation.md.
  validate <contract-path>           Dry-run: parse + structural check, no model call.
  review <task-id>                   Bundled post-build view: proof-of-work + git diff.
                                     The canonical "what to do after a build" command.
  review [--since|--since-last]      Nightly code-review sweep over a window of main.
    [--max-runs N] [--dry-run]       Runs Roslyn pre-pass, then per-file bug-scan +
                                     simplify-comments axes via qwen, plus a single
                                     cross-cutting untested-additions pass. Writes a
                                     two-zone report to imp/reviews/<date>.md.
                                     --dry-run prints the axis plan without dispatch.

Substrate:
  init [path] [--force]              Scaffold the substrate layout into the
                                     current git repo. Default location is
                                     `imp/` (gnome-maintained content:
                                     learnings, reference, concepts, _index,
                                     note, log). Also scaffolds root-level
                                     human-owned plans/, bugs/, rules/, and
                                     TODO.md if missing — never overwrites
                                     existing root content. --force regenerates
                                     skill-owned files on re-init.
  signals <doc> [--json]             Gather per-doc signals for migration
                                     classification: git dates, structure,
                                     self-labels, cross-refs, code-reference
                                     presence. Mechanical only; consumed by
                                     /project-migrate.
  migrate [paths...]                 Phase 1 of /project-migrate: walk legacy
    [--include glob]                 markdown sources, gather signals per doc,
    [--exclude glob]                 heuristic-classify shape, write a plan to
    [--out dir]                      <repo>.imp-proposals/_migration/M-NNN/.
                                     No model calls. Default sources: project/.
                                     Phase 2 (classification, proposals) not
                                     built yet — see plans/project-migrate-phase1.md.
  note [<text> | -]                  Append a capture to the substrate's
                                     note inbox. `imp note "<text>"` is
                                     the dominant case; no args opens
                                     $EDITOR; `-` reads stdin. The gnome
                                     processes inbox items into layer-1
                                     entries on a later `imp tidy` run.
  tidy [--dry-run]                   The gnome. Processes notes from
                                     imp/note/inbox/ into layer-1 entries
                                     (learnings, references) via triage +
                                     draft LLM phases. Cross-boundary
                                     suggestions deferred. --dry-run shows
                                     what would happen without writing.
  embed-refresh                      Scan the substrate (imp/learnings,
                                     imp/reference, plans, rules) and
                                     refresh the embeddings cache at
                                     .imp/embeddings.jsonl: embed new and
                                     content-changed entries, prune
                                     orphans, leave unchanged entries
                                     alone. Tidy will call this internally
                                     once phase 2 lands; this command is
                                     for development and inspection.
  locate <kind> [--title T] <body>   Test the locate step: embed the given
                                     text, rank top-K cached entries of
                                     the matching kind, ask the chat
                                     provider for a create/update decision.
                                     Prints candidates to stderr and the
                                     decision JSON to stdout. Useful for
                                     iterating on the locate prompt
                                     without driving through a full tidy
                                     run. Kinds: learning, reference,
                                     rule, plan (or *-suggestion variants).

Inspection:
  list                                List contracts under ./contracts/*.md (JSON).
  show <task-id>                     Print the contract markdown for a task.
  log <task-id>                      Print the rendered transcript of the most recent run.
  template <name>                    Print a template (contract | proof-of-work).

Diagnostics:
  ping [provider]                    Smoke-test a chat provider.
  ping-tools [provider]              Verify multi-turn tool-calling round-trips.
  ping-embeddings [text...]          Smoke-test the embedding provider — embed a short
                                     string (default: "hello world") and report dim,
                                     norm, and a few sample components.
  render-transcript <trace-jsonl>    Re-render transcript.md from an existing trace.jsonl.

Operates on the current working directory. cwd must be a git repo for
build / validate / list / show / log / review.
""");
    }

    // --- shared config / chat construction ---

    static IConfiguration BuildConfiguration(string? providerOverride = null)
    {
        var builder = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true)
            .AddJsonFile("appsettings.json", optional: true);

        if (!string.IsNullOrEmpty(providerOverride))
        {
            builder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ActiveProvider"] = providerOverride,
            });
        }

        return builder.Build();
    }

    // --- core lifecycle subcommands ---

    static async Task<int> RunBuild(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: imp build <contract-path> [provider]");
            return 1;
        }
        var contractPath = args[0];
        var providerOverride = args.Length > 1 ? args[1] : null;
        var config = BuildConfiguration(providerOverride);
        var chat = Providers.Create(config);

        Console.Error.WriteLine($"[imp] build start: contractPath={contractPath} provider={config["ActiveProvider"]} cwd={Directory.GetCurrentDirectory()}");
        var json = await LifecycleCommands.Build(chat, config, contractPath);
        Console.WriteLine(json);
        return 0;
    }

    static async Task<int> RunResearch(string[] args)
    {
        // Accepted forms:
        //   imp research --mode=fs "question"
        //   imp research --mode=fs --brief contracts/R-007.md
        //   imp research --mode=fs --provider Qwen "question"
        // Provider resolution (handled in ResearchRunner): --provider flag
        // beats `Modes:<name>:Provider` beats the mode's code-side
        // PreferredProvider beats ActiveProvider.
        string? mode = null;
        string? briefPath = null;
        string? question = null;
        string? providerOverride = null;
        bool keepArchive = false;

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a.StartsWith("--mode=", StringComparison.Ordinal))
                mode = a["--mode=".Length..];
            else if (a == "--mode" && i + 1 < args.Length)
                mode = args[++i];
            else if (a.StartsWith("--brief=", StringComparison.Ordinal))
                briefPath = a["--brief=".Length..];
            else if (a == "--brief" && i + 1 < args.Length)
                briefPath = args[++i];
            else if (a.StartsWith("--provider=", StringComparison.Ordinal))
                providerOverride = a["--provider=".Length..];
            else if (a == "--provider" && i + 1 < args.Length)
                providerOverride = args[++i];
            else if (a is "--archive" or "-a")
                keepArchive = true;
            else if (question is null)
                question = a;
            else
                question += " " + a;
        }

        if (string.IsNullOrEmpty(mode))
        {
            Console.Error.WriteLine("Usage: imp research --mode=<name> \"question\" [--brief path] [--provider name]");
            Console.Error.WriteLine($"       known modes: {string.Join(", ", Modes.KnownNames())}");
            return 1;
        }
        if (string.IsNullOrEmpty(briefPath) && string.IsNullOrEmpty(question))
        {
            Console.Error.WriteLine("Usage: imp research --mode=<name> \"question\" [--brief path] [--provider name]");
            Console.Error.WriteLine("       supply either a free-text question or --brief <path>");
            return 1;
        }

        var config = BuildConfiguration();

        Console.Error.WriteLine($"[imp] research start: mode={mode} brief={briefPath ?? "<free-text>"} provider={providerOverride ?? config[$"Modes:{mode}:Provider"] ?? config["ActiveProvider"]} cwd={Directory.GetCurrentDirectory()}");
        var json = await ResearchRunner.RunAsync(config, mode, question, briefPath, keepArchive, providerOverride);
        Console.WriteLine(json);
        return 0;
    }

    static async Task<int> RunTidy(string[] args)
    {
        var config = BuildConfiguration();
        var chat = Providers.Create(config);
        Console.Error.WriteLine($"[imp] tidy start: provider={config["ActiveProvider"]} cwd={Directory.GetCurrentDirectory()}");
        return await Tidy.RunAsync(chat, config, args);
    }

    static async Task<int> RunWiki(string[] args)
    {
        // DEPRECATED 2026-05-10. Superseded by the substrate (`imp/`,
        // maintained by `imp tidy`). Kept as reference; removal pending.
        // See plans/wiki-deprecation.md.
        Console.Error.WriteLine("[imp] WARNING: `imp wiki` is deprecated and pending removal. See plans/wiki-deprecation.md.");

        // Accepted forms:
        //   imp wiki                  — plan whole repo, dispatch
        //   imp wiki src/Foo          — plan subtree, dispatch
        //   imp wiki --dry-run [path] — plan only, print as JSON
        //   imp wiki --resume W-NNN   — resume an interrupted run from its manifest
        string? targetPath = null;
        bool dryRun = false;
        bool full = false;
        string? resumeId = null;
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a == "--dry-run") dryRun = true;
            else if (a == "--full") full = true;
            else if (a.StartsWith("--resume=", StringComparison.Ordinal))
                resumeId = a["--resume=".Length..];
            else if (a == "--resume" && i + 1 < args.Length)
                resumeId = args[++i];
            else if (a.StartsWith("--", StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"imp wiki: unknown flag '{a}'");
                return 1;
            }
            else if (targetPath is null) targetPath = a;
            else
            {
                Console.Error.WriteLine($"imp wiki: unexpected extra argument '{a}'");
                return 1;
            }
        }

        var cwd = Directory.GetCurrentDirectory();
        var repoRoot = Path.GetFullPath(cwd);
        if (!Directory.Exists(Path.Combine(repoRoot, ".git")) && !File.Exists(Path.Combine(repoRoot, ".git")))
        {
            Console.Error.WriteLine($"imp wiki: cwd is not a git repository: {repoRoot}");
            return 1;
        }

        var config = BuildConfiguration();

        // Resume path: load manifest from <repo>.wikis/W-NNN-<slug>/, ignore
        // any positional path argument, dispatch unfinished targets.
        if (!string.IsNullOrEmpty(resumeId))
        {
            if (!string.IsNullOrEmpty(targetPath) || dryRun || full)
            {
                Console.Error.WriteLine("imp wiki: --resume cannot be combined with a target path, --dry-run, or --full");
                return 1;
            }
            var existing = WikiArchive.FindByWikiId(repoRoot, resumeId);
            if (existing is null)
            {
                Console.Error.WriteLine($"imp wiki: no archive found for {resumeId} under {WikiArchive.RootFor(repoRoot)}");
                return 1;
            }
            var manifest = WikiArchive.ReadManifest(existing);
            if (manifest is null)
            {
                Console.Error.WriteLine($"imp wiki: manifest.json missing or unreadable in {existing}");
                return 1;
            }

            var chat = Providers.Create(config);
            Console.Error.WriteLine($"[imp] wiki resume: wikiId={manifest.WikiId} archive={existing} targets={manifest.Targets.Count}");
            var json = await WikiRunner.RunAsync(chat, config, manifest, existing);
            Console.WriteLine(json);
            return 0;
        }

        var wikiDir = config["Wiki:Dir"] ?? "imp-wiki";
        var maxDirBytes = long.TryParse(config["Wiki:MaxDirBytes"], out var mdb) && mdb > 0 ? mdb : 40960L;
        var toolBudget = int.TryParse(config["Wiki:ToolBudget"], out var tb) && tb > 0 ? tb : WikiRunner.DefaultToolBudget;

        string targetSubpath = "";
        if (!string.IsNullOrEmpty(targetPath))
        {
            var abs = Path.GetFullPath(targetPath);
            if (!abs.Equals(repoRoot, StringComparison.Ordinal)
                && !abs.StartsWith(repoRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                Console.Error.WriteLine($"imp wiki: target {abs} is outside repo {repoRoot}");
                return 1;
            }
            targetSubpath = Path.GetRelativePath(repoRoot, abs).Replace('\\', '/');
            if (targetSubpath == ".") targetSubpath = "";
        }

        var plan = WikiPlanner.Plan(repoRoot, targetSubpath, wikiDir, maxDirBytes);

        // --full: force RUN on cache hits. Stubs stay stubs (size threshold
        // is a real constraint, not a cache decision).
        if (full)
        {
            var rewritten = plan.Targets.Select(t =>
                t.Decision == WikiDecision.Skip
                    ? t with { Decision = WikiDecision.Run, Reason = "--full forced re-run" }
                    : t).ToList();
            var summary = new WikiPlanSummary(
                Run: rewritten.Count(t => t.Decision == WikiDecision.Run),
                Skip: 0,
                Stub: rewritten.Count(t => t.Decision == WikiDecision.Stub));
            plan = plan with { Targets = rewritten, Summary = summary };
        }

        if (dryRun)
        {
            Console.WriteLine(WikiPlanner.SerializePlan(plan));
            return 0;
        }

        // Construct manifest from plan, allocate archive dir, dispatch.
        var slug = SlugForRun(targetSubpath);
        var freshManifest = WikiRunner.ManifestFromPlan(plan, targetSubpath, toolBudget, slug);
        var archiveDir = WikiArchive.DirectoryFor(repoRoot, freshManifest.WikiId, slug);

        var chatClient = Providers.Create(config);
        Console.Error.WriteLine($"[imp] wiki start: wikiId={freshManifest.WikiId} archive={archiveDir} targets={freshManifest.Targets.Count} provider={config["ActiveProvider"]}");
        var resultJson = await WikiRunner.RunAsync(chatClient, config, freshManifest, archiveDir);
        Console.WriteLine(resultJson);
        return 0;
    }

    static string SlugForRun(string targetSubpath)
        => string.IsNullOrEmpty(targetSubpath) ? "root" : BriefParser.SlugFrom(targetSubpath);

    // Hidden diagnostic: render a wiki page from an existing report.json
    // without dispatching a fresh research run. Used to iterate on the
    // renderer / wiki prompt without burning model time. Not listed in help.
    //   imp wiki-render-test <research-id> <source-path>
    //   imp wiki-render-test R-001 ""
    static int RunWikiRenderTest(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: imp wiki-render-test <research-id> <source-path>");
            return 1;
        }
        var researchId = args[0];
        var sourcePath = args[1];
        var repoRoot = Path.GetFullPath(Directory.GetCurrentDirectory());

        var researchesRoot = ResearchArchive.RootFor(repoRoot);
        var match = Directory.EnumerateDirectories(researchesRoot)
            .FirstOrDefault(d => Path.GetFileName(d).StartsWith(researchId + "-", StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            Console.Error.WriteLine($"wiki-render-test: no archive found for {researchId} under {researchesRoot}");
            return 1;
        }
        var reportPath = Path.Combine(match, "report.json");
        if (!File.Exists(reportPath))
        {
            Console.Error.WriteLine($"wiki-render-test: report.json missing in {match}");
            return 1;
        }
        var report = System.Text.Json.JsonSerializer.Deserialize<ResearchReport>(
            File.ReadAllText(reportPath), ResearchReportJson.Options);
        if (report is null)
        {
            Console.Error.WriteLine($"wiki-render-test: failed to parse report.json");
            return 1;
        }

        var pagePath = WikiPlanner.PageRelativePathFor(sourcePath, "imp-wiki");
        var ctx = new WikiPageContext(
            PagePath: pagePath,
            SourcePath: sourcePath,
            SourceTreeSha: "stub-sha-for-render-test",
            SourceBytes: 0,
            FileCount: 0,
            MaxDirBytes: 40960,
            Mode: report.Mode,
            ModelName: "render-test",
            ProviderName: null,
            ResearchId: researchId,
            GeneratorVersion: WikiPageRenderer.CurrentGeneratorVersion,
            GeneratedAt: DateTimeOffset.UtcNow,
            WorktreeDirty: null);

        var entry = new WikiManifestEntry(
            SourcePath: sourcePath,
            PagePath: pagePath,
            Decision: WikiDecision.Run,
            SourceTreeSha: ctx.SourceTreeSha,
            SourceBytes: 0,
            FileCount: 0,
            Status: WikiEntryStatus.Done,
            ResearchId: researchId,
            ResearchArchive: match,
            StartedAt: null,
            CompletedAt: null,
            Error: null);

        Console.Write(WikiPageRenderer.Render(ctx, entry, report));
        return 0;
    }

    // Hidden diagnostic: call the splitter against a source-path and print
    // the cluster proposal JSON. No dispatch, no page writes — pure model
    // probe to iterate on the splitter prompt without burning a full wiki
    // run. Uses Wiki:Provider (or ActiveProvider) like the real path.
    //   imp wiki-split-test <source-path>
    static async Task<int> RunWikiSplitTest(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: imp wiki-split-test <source-path>");
            return 1;
        }
        var sourcePath = args[0];
        var repoRoot = Path.GetFullPath(Directory.GetCurrentDirectory());
        var config = BuildConfiguration();
        var provider = config["Wiki:Provider"] ?? config["ActiveProvider"];
        if (string.IsNullOrEmpty(provider))
        {
            Console.Error.WriteLine("wiki-split-test: no Wiki:Provider or ActiveProvider configured");
            return 1;
        }
        var maxDirBytes = long.TryParse(config["Wiki:MaxDirBytes"], out var mdb) && mdb > 0 ? mdb : 40960L;
        var orchestrator = Providers.CreateForProvider(config, provider);

        Console.Error.WriteLine($"[imp] wiki-split-test: source={sourcePath} threshold={maxDirBytes} provider={provider}");
        try
        {
            var proposal = await WikiSplitter.ProposeAsync(orchestrator, repoRoot, sourcePath, maxDirBytes);
            Console.Error.WriteLine($"[imp] wiki-split-test: {proposal.Clusters.Count} clusters");
            var json = System.Text.Json.JsonSerializer.Serialize(proposal, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            Console.WriteLine(json);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"wiki-split-test: failed: {ex.Message}");
            return 1;
        }
    }

    // Hidden diagnostic: render the wiki index from a given directory.
    //   imp wiki-index-test [wiki-dir]
    // Defaults to <cwd>/wiki/. Useful for iterating on the index format
    // against a synthesized fixture without dispatching a real run.
    static int RunWikiIndexTest(string[] args)
    {
        var cwd = Path.GetFullPath(Directory.GetCurrentDirectory());
        var wikiAbs = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(cwd, "imp-wiki");
        if (!Directory.Exists(wikiAbs))
        {
            Console.Error.WriteLine($"wiki-index-test: directory does not exist: {wikiAbs}");
            return 1;
        }
        var repoRoot = Path.GetDirectoryName(wikiAbs.TrimEnd(Path.DirectorySeparatorChar)) ?? cwd;
        var wikiDir = Path.GetFileName(wikiAbs.TrimEnd(Path.DirectorySeparatorChar));
        var (md, summary) = WikiIndexRenderer.RenderFromDirectory(repoRoot, wikiDir, DateTimeOffset.UtcNow);
        Console.Error.WriteLine($"[imp] wiki-index-test: {summary.TotalPages} pages ({summary.Generated} generated, {summary.Oversized} oversized, {summary.Failed} failed)");
        Console.Write(md);
        return 0;
    }

    static async Task<int> RunValidate(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: imp validate <contract-path>");
            return 1;
        }
        var json = await LifecycleCommands.ValidateContract(args[0]);
        Console.WriteLine(json);
        return 0;
    }

    static int RunReview(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: imp review <task-id>");
            return 1;
        }
        var bundle = LifecycleCommands.Review(args[0]);
        Console.Write(bundle);
        if (!bundle.EndsWith('\n')) Console.WriteLine();
        return 0;
    }

    // Dispatches between the existing build-bundle `imp review <task-id>` and
    // the new nightly `imp review [--since|--since-last|--max-runs|--dry-run]`
    // by looking for flags: a single positional argument that doesn't start
    // with `--` is the legacy task-id path; anything else is the new review.
    static async Task<int> RunReviewDispatch(string[] args)
    {
        bool hasFlags = args.Any(a => a.StartsWith("--", StringComparison.Ordinal));
        bool looksLikeTaskId = args.Length >= 1 && !args[0].StartsWith("--", StringComparison.Ordinal);
        if (!hasFlags && looksLikeTaskId)
            return RunReview(args);
        return await RunNightlyReview(args);
    }

    static async Task<int> RunNightlyReview(string[] args)
    {
        string? since = null;
        bool sinceLast = false;
        int? maxRuns = null;
        bool dryRun = false;

        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a.StartsWith("--since=", StringComparison.Ordinal)) since = a["--since=".Length..];
            else if (a == "--since" && i + 1 < args.Length) since = args[++i];
            else if (a == "--since-last") sinceLast = true;
            else if (a.StartsWith("--max-runs=", StringComparison.Ordinal)) maxRuns = int.Parse(a["--max-runs=".Length..]);
            else if (a == "--max-runs" && i + 1 < args.Length) maxRuns = int.Parse(args[++i]);
            else if (a == "--dry-run") dryRun = true;
            else
            {
                Console.Error.WriteLine($"imp review: unknown flag '{a}'");
                return 1;
            }
        }

        var cwd = Directory.GetCurrentDirectory();
        var repoRoot = FindRepoRoot(cwd);
        if (repoRoot is null)
        {
            Console.Error.WriteLine($"imp review: not in a git repo: {cwd}");
            return 1;
        }

        var config = BuildConfiguration();
        var opts = new ReviewOptions(since, sinceLast, maxRuns, dryRun);
        var outcome = await ReviewOrchestrator.RunAsync(config, repoRoot, opts);
        if (outcome is null) return 0; // dry-run printed and exited
        Console.WriteLine($"wrote {outcome.ReportPath}");
        Console.WriteLine($"qwen runs: {outcome.QwenRunCount}, skipped: {outcome.SkippedRunCount}, watermark advanced: {outcome.WatermarkAdvanced}");
        return 0;
    }

    // --- inspection subcommands ---

    static int RunList()
    {
        Console.WriteLine(LifecycleCommands.ListTasks());
        return 0;
    }

    static int RunShow(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: imp show <task-id>");
            return 1;
        }
        Console.Write(LifecycleCommands.GetContract(args[0]));
        return 0;
    }

    static int RunLog(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: imp log <task-id>");
            return 1;
        }
        Console.Write(LifecycleCommands.GetLog(args[0]));
        return 0;
    }

    static int RunTemplate(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: imp template <contract|proof-of-work>");
            return 1;
        }
        var fileName = args[0] switch
        {
            "contract" => "contract.md",
            "proof-of-work" => "proof-of-work.json",
            _ => null,
        };
        if (fileName is null)
        {
            Console.Error.WriteLine($"imp: unknown template '{args[0]}'. Available: contract, proof-of-work");
            return 1;
        }
        var path = Path.Combine(AppContext.BaseDirectory, "Templates", fileName);
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"imp: template file not found at {path}");
            return 2;
        }
        Console.Write(File.ReadAllText(path));
        return 0;
    }

    // --- diagnostics ---

    static async Task<int> RunPing(string[] args)
    {
        var providerOverride = args.Length > 0 ? args[0] : null;
        var config = BuildConfiguration(providerOverride);
        Console.Error.WriteLine($"[ping] ActiveProvider = {config["ActiveProvider"]}");

        var client = Providers.Create(config);
        var response = await client.GetResponseAsync(
            "Reply with exactly the word 'pong' and nothing else.",
            new ChatOptions { MaxOutputTokens = 2000 });
        Console.Error.WriteLine($"[ping] FinishReason = {response.FinishReason}");
        Console.Error.WriteLine($"[ping] Usage = in:{response.Usage?.InputTokenCount} out:{response.Usage?.OutputTokenCount} total:{response.Usage?.TotalTokenCount}");
        Console.Error.WriteLine($"[ping] Messages = {response.Messages.Count}, Contents across messages = {response.Messages.Sum(m => m.Contents.Count)}");
        foreach (var msg in response.Messages)
            foreach (var c in msg.Contents)
                Console.Error.WriteLine($"[ping]   {msg.Role} content: {c.GetType().Name}");
        Console.WriteLine(response.Text);
        return 0;
    }

    // Verifies multi-turn tool-calling round-trips correctly against the active
    // provider. Specifically checks that reasoning-model responses (TextReasoningContent)
    // don't break the follow-up turn after a tool call. If this works, the inner
    // executor loop is safe to build on the same pattern.
    static async Task<int> RunPingTools(string[] args)
    {
        var providerOverride = args.Length > 0 ? args[0] : null;
        var config = BuildConfiguration(providerOverride);
        Console.Error.WriteLine($"[ping-tools] ActiveProvider = {config["ActiveProvider"]}");

        var client = Providers.Create(config);

        var echoTool = AIFunctionFactory.Create(
            (string value) => $"echoed: {value}",
            name: "echo",
            description: "Echoes the given value back with an 'echoed: ' prefix.");

        var options = new ChatOptions
        {
            MaxOutputTokens = 2000,
            Tools = new List<AITool> { echoTool },
        };

        var history = new List<ChatMessage>
        {
            new(ChatRole.User, "Call the echo tool with value='hello world'. After the tool returns, reply with exactly the tool's output and nothing else."),
        };

        Console.Error.WriteLine("[ping-tools] --- Turn 1 (expect tool call) ---");
        var r1 = await client.GetResponseAsync(history, options);
        DescribeResponse("r1", r1);

        var toolCalls = r1.Messages
            .SelectMany(m => m.Contents)
            .OfType<FunctionCallContent>()
            .ToList();

        if (toolCalls.Count == 0)
        {
            Console.Error.WriteLine("[ping-tools] FAIL: no tool call on turn 1.");
            Console.WriteLine(r1.Text);
            return 1;
        }

        foreach (var msg in r1.Messages)
            history.Add(msg);

        var toolResults = new List<AIContent>();
        foreach (var call in toolCalls)
        {
            var valueArg = call.Arguments?.TryGetValue("value", out var v) == true ? v?.ToString() ?? "" : "";
            var result = $"echoed: {valueArg}";
            Console.Error.WriteLine($"[ping-tools] Tool call: {call.Name}(value={valueArg}) -> {result}");
            toolResults.Add(new FunctionResultContent(call.CallId, result));
        }

        history.Add(new ChatMessage(ChatRole.Tool, toolResults));

        Console.Error.WriteLine("[ping-tools] --- Turn 2 (expect final text) ---");
        var r2 = await client.GetResponseAsync(history, options);
        DescribeResponse("r2", r2);

        Console.WriteLine(r2.Text);
        return 0;
    }

    // Test the locate step end-to-end on the live substrate cache.
    // Embeds the supplied note text, ranks top-K cached entries of the
    // matching kind, and asks the chat provider whether to create-new
    // or update-which. Prints the ranking to stderr (for inspection)
    // and the decision JSON to stdout.
    static async Task<int> RunLocate(string[] args)
    {
        string? kind = null;
        string? title = null;
        var bodyParts = new List<string>();
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a == "--title" && i + 1 < args.Length) { title = args[++i]; continue; }
            if (a.StartsWith("--title=", StringComparison.Ordinal)) { title = a["--title=".Length..]; continue; }
            if (kind is null) { kind = a; continue; }
            bodyParts.Add(a);
        }

        if (string.IsNullOrEmpty(kind) || bodyParts.Count == 0)
        {
            Console.Error.WriteLine("Usage: imp locate <kind> [--title <title>] <body...>");
            Console.Error.WriteLine($"       known kinds: {string.Join(", ", EmbeddingIndex.KindToPathPrefix.Keys)}");
            return 1;
        }

        var body = string.Join(" ", bodyParts);
        title ??= body.Length > 60 ? body[..60] + "…" : body;

        var cwd = Directory.GetCurrentDirectory();
        var repoRoot = FindRepoRoot(cwd);
        if (repoRoot is null)
        {
            Console.Error.WriteLine($"imp locate: not in a git repo: {cwd}");
            return 1;
        }

        var config = BuildConfiguration();
        var chat = Providers.Create(config);
        var embed = Embeddings.Create(config);

        var promptPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Prompts", "tidy-locate.md");
        if (!File.Exists(promptPath))
        {
            Console.Error.WriteLine($"imp locate: prompt not found at {promptPath}");
            return 2;
        }
        var systemPrompt = await File.ReadAllTextAsync(promptPath);

        Console.Error.WriteLine($"[locate] kind={kind} title=\"{title}\" chat-provider={config["ActiveProvider"]}");

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = await Locate.LocateAsync(chat, embed, repoRoot, kind, title, body, systemPrompt);
        sw.Stop();

        Console.Error.WriteLine($"[locate] candidates considered (top-{result.Candidates.Count}):");
        foreach (var c in result.Candidates)
            Console.Error.WriteLine($"  {c.Similarity:F4}  {c.Entry.Path}");
        Console.Error.WriteLine($"[locate] elapsed={sw.ElapsedMilliseconds}ms");

        var output = new
        {
            decision = result.Decision,
            target_path = result.TargetPath,
            rationale = result.Rationale,
        };
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(output,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    // Scan the substrate and refresh the embeddings cache. Idempotent:
    // unchanged entries are kept as-is, changed entries are re-embedded,
    // new entries are added, and orphaned cache rows (file no longer
    // exists) are dropped. Fail-closed on unreachable embedding provider
    // per rules/embedding-provider.md.
    static async Task<int> RunEmbedRefresh(string[] args)
    {
        var cwd = Directory.GetCurrentDirectory();
        var repoRoot = FindRepoRoot(cwd);
        if (repoRoot is null)
        {
            Console.Error.WriteLine($"imp embed-refresh: not in a git repo: {cwd}");
            return 1;
        }

        var config = BuildConfiguration();
        var modelId = config["EmbeddingProvider:Model"] ?? "unknown";
        var endpoint = config["EmbeddingProvider:Endpoint"] ?? "<unset>";
        Console.Error.WriteLine($"[embed-refresh] repo={repoRoot} endpoint={endpoint} model={modelId}");

        var client = Embeddings.Create(config);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var stats = await EmbeddingIndex.RefreshAsync(client, repoRoot, modelId);
        sw.Stop();

        Console.WriteLine($"refresh: added={stats.Added} updated={stats.Updated} removed={stats.Removed} unchanged={stats.Unchanged} total={stats.Total} elapsed={sw.ElapsedMilliseconds}ms");
        return 0;
    }

    static string? FindRepoRoot(string startDir)
    {
        var dir = new DirectoryInfo(System.IO.Path.GetFullPath(startDir));
        while (dir is not null)
        {
            if (Directory.Exists(System.IO.Path.Combine(dir.FullName, ".git"))
                || File.Exists(System.IO.Path.Combine(dir.FullName, ".git")))
                return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    // Smoke-test the embedding provider. Embeds a short string against
    // imp:8081 (or wherever EmbeddingProvider:Endpoint points) and reports
    // dimension + norm + a few sample components, enough to confirm the
    // round-trip works and the vector looks healthy. Fail-closed per
    // rules/embedding-provider.md — no fallback on connection failure.
    static async Task<int> RunPingEmbeddings(string[] args)
    {
        var input = args.Length > 0 ? string.Join(" ", args) : "hello world";
        var config = BuildConfiguration();

        var endpoint = config["EmbeddingProvider:Endpoint"] ?? "<unset>";
        var model = config["EmbeddingProvider:Model"] ?? "<unset>";
        Console.Error.WriteLine($"[ping-embeddings] endpoint={endpoint} model={model}");
        Console.Error.WriteLine($"[ping-embeddings] input=\"{input}\"");

        var client = Embeddings.Create(config);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var vector = await Embeddings.EmbedAsync(client, input);
        sw.Stop();

        var norm = Math.Sqrt(vector.Sum(x => (double)x * x));
        Console.Error.WriteLine($"[ping-embeddings] dim={vector.Length} norm={norm:F4} elapsed={sw.ElapsedMilliseconds}ms");
        Console.Error.WriteLine($"[ping-embeddings] first 5: [{string.Join(", ", vector.Take(5).Select(x => x.ToString("F4")))}]");
        Console.Error.WriteLine($"[ping-embeddings] last 5:  [{string.Join(", ", vector.TakeLast(5).Select(x => x.ToString("F4")))}]");
        Console.WriteLine($"ok dim={vector.Length}");
        return 0;
    }

    // Regenerates transcript.md from an existing trace.jsonl. Useful for old
    // traces or for iterating on the renderer without re-running a contract.
    static int RunRenderTranscript(string[] args)
    {
        if (args.Length < 1)
        {
            Console.Error.WriteLine("Usage: imp render-transcript <trace-jsonl-path>");
            return 1;
        }
        var tracePath = args[0];
        var outputPath = Path.Combine(
            Path.GetDirectoryName(tracePath) ?? ".",
            "transcript.md");
        TranscriptRenderer.Render(tracePath, outputPath);
        Console.Error.WriteLine($"[render-transcript] wrote {outputPath}");
        return 0;
    }

    static void DescribeResponse(string label, ChatResponse response)
    {
        Console.Error.WriteLine($"[ping-tools] {label}.FinishReason = {response.FinishReason}");
        Console.Error.WriteLine($"[ping-tools] {label}.Usage = in:{response.Usage?.InputTokenCount} out:{response.Usage?.OutputTokenCount}");
        Console.Error.WriteLine($"[ping-tools] {label}.Messages = {response.Messages.Count}");
        foreach (var msg in response.Messages)
            foreach (var c in msg.Contents)
                Console.Error.WriteLine($"[ping-tools]   {msg.Role} content: {c.GetType().Name}");
    }
}
