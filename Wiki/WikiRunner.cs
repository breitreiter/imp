using System.Diagnostics;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Imp.Research;
using Imp.Infrastructure;

namespace Imp.Wiki;

// DEPRECATED 2026-05-10 — `imp wiki` is on the way out. Substrate
// (`imp/`, maintained by `imp tidy`) supersedes it. Kept as reference
// only; do not extend. See plans/wiki-deprecation.md for the why.
//
// Orchestrator for `imp wiki`. Step 4 of project/wiki-plan.md.
//
// Consumes a WikiManifest (constructed by the CLI from a fresh plan or
// loaded from disk for --resume), iterates targets sequentially, and:
//   - Run    — synthesises a per-target brief, dispatches ResearchRunner.RunAsync
//              against the wiki mode, captures research_id and outcome.
//   - Skip   — marks the entry skipped (page already up-to-date).
//   - Stub   — marks the entry done (the renderer in step 5 will write the
//              actual stub markdown; the orchestrator just records intent).
//
// Manifest is rewritten after every per-target update so a kill leaves a
// recoverable state. On resume, entries with status in {Done, Skipped} are
// passed over; Pending and Failed are retried (Failed implies the prior
// dispatch returned a terminal-error envelope; a fresh dispatch is fine).
//
// Markdown page rendering is NOT done here — that's step 5. After this
// step runs, the user has report.json files in <repo>.researches/ and a
// manifest in <repo>.wikis/, but no wiki/*.md yet.

public sealed record WikiResult(
    [property: JsonPropertyName("wiki_id")] string WikiId,
    [property: JsonPropertyName("repo_root")] string RepoRoot,
    [property: JsonPropertyName("wiki_dir")] string WikiDir,
    [property: JsonPropertyName("archive_dir")] string ArchiveDir,
    [property: JsonPropertyName("pages_written")] int PagesWritten,
    [property: JsonPropertyName("pages_skipped_unchanged")] int PagesSkippedUnchanged,
    [property: JsonPropertyName("pages_oversized_stub")] int PagesOversizedStub,
    [property: JsonPropertyName("pages_failed")] int PagesFailed,
    [property: JsonPropertyName("wall_seconds")] double WallSeconds,
    [property: JsonPropertyName("estimated_cost_usd")] decimal EstimatedCostUsd);

public static class WikiRunner
{
    public const int DefaultToolBudget = 10;

    // Build a manifest from a fresh plan. The manifest is the canonical
    // working state from here on; the plan is its construction input only.
    public static WikiManifest ManifestFromPlan(
        WikiPlan plan,
        string targetSubpath,
        int toolBudget,
        string slug)
    {
        var entries = plan.Targets.Select(t => new WikiManifestEntry(
            SourcePath: t.RelativePath,
            PagePath: t.PagePath,
            Decision: t.Decision,
            SourceTreeSha: t.SourceTreeSha,
            SourceBytes: t.SourceBytes,
            FileCount: t.FileCount,
            Status: WikiEntryStatus.Pending,
            ResearchId: null,
            ResearchArchive: null,
            StartedAt: null,
            CompletedAt: null,
            Error: null)).ToList();

        return new WikiManifest(
            WikiId: WikiArchive.AllocateNextId(plan.RepoRoot),
            Slug: slug,
            RepoRoot: plan.RepoRoot,
            WikiDir: plan.WikiDir,
            MaxDirBytes: plan.MaxDirBytes,
            ToolBudget: toolBudget,
            TargetSubpath: targetSubpath,
            CreatedAt: DateTimeOffset.UtcNow,
            Targets: entries);
    }

    public static async Task<string> RunAsync(
        IChatClient chat,
        IConfiguration config,
        WikiManifest manifest,
        string archiveDir)
    {
        ImpLog.Info($"wiki: start wikiId={manifest.WikiId} archive={archiveDir} targets={manifest.Targets.Count}");
        var sw = Stopwatch.StartNew();
        decimal totalCost = 0m;
        var providerName = config["ActiveProvider"];
        var providerSection = ResolveProviderSection(config, providerName);
        var modelName = providerSection?["Model"];

        Directory.CreateDirectory(archiveDir);
        WikiArchive.WriteManifest(archiveDir, manifest);

        for (int i = 0; i < manifest.Targets.Count; i++)
        {
            var entry = manifest.Targets[i];
            if (entry.Status == WikiEntryStatus.Done || entry.Status == WikiEntryStatus.Skipped)
            {
                ImpLog.Info($"wiki: target {i + 1}/{manifest.Targets.Count} {entry.SourcePath} status={entry.Status} (resume skip)");
                continue;
            }

            // Adaptive splitting: a fresh STUB without a cluster_slug is
            // a candidate for the orchestrator-driven splitter. If the
            // splitter succeeds, the entry is replaced in-place by N
            // cluster entries; manifest is rewritten so a crash mid-run
            // resumes against the cluster set, not the original stub.
            if (entry.Decision == WikiDecision.Stub && entry.ClusterSlug is null)
            {
                var clusterEntries = await TrySplitStubAsync(config, manifest, entry);
                if (clusterEntries is not null)
                {
                    ImpLog.Info($"wiki: target {i + 1}/{manifest.Targets.Count} {entry.SourcePath} split into {clusterEntries.Count} clusters");
                    manifest.Targets.RemoveAt(i);
                    manifest.Targets.InsertRange(i, clusterEntries);
                    WikiArchive.WriteManifest(archiveDir, manifest);
                    // Delete any legacy parent stub at <wikiDir>/<sourcePath>.md
                    // — its slot is now covered by the cluster pages under
                    // <wikiDir>/<sourcePath>/.
                    DeleteLegacyParentStub(manifest, entry.SourcePath);
                    i--; // re-enter the loop at the first cluster entry
                    continue;
                }
                ImpLog.Info($"wiki: target {i + 1}/{manifest.Targets.Count} {entry.SourcePath} splitter unavailable or failed; emitting v0 stub");
            }

            ImpLog.Info($"wiki: target {i + 1}/{manifest.Targets.Count} {entry.SourcePath} decision={entry.Decision}{(entry.ClusterSlug is null ? "" : $" cluster={entry.ClusterSlug}")}");
            var (updated, cost) = await ProcessTargetAsync(chat, config, manifest, entry, modelName);
            manifest.Targets[i] = updated;
            totalCost += cost;
            WikiArchive.WriteManifest(archiveDir, manifest);
        }

        // Regenerate the index after all per-target work. The body is
        // model-rendered via the orchestrator role (Wiki:Provider) when
        // pages exist; on any failure we fall back to the deterministic
        // v0 blockquote so the README always builds.
        try
        {
            var entries = WikiIndexRenderer.LoadEntries(manifest.RepoRoot, manifest.WikiDir);
            var indexSummary = WikiIndexRenderer.SummariseEntries(entries);

            string? modelBody = null;
            if (entries.Count > 0)
            {
                modelBody = await TrySynthesizeIndexBodyAsync(config, manifest, entries);
            }

            var indexMd = WikiIndexRenderer.Render(entries, indexSummary, DateTimeOffset.UtcNow, modelBody);
            var indexPath = Path.Combine(manifest.RepoRoot, manifest.WikiDir, "README.md");
            Directory.CreateDirectory(Path.GetDirectoryName(indexPath)!);
            File.WriteAllText(indexPath, indexMd);
            ImpLog.Info($"wiki: index regenerated at {indexPath} (model_body={(modelBody is null ? "no" : "yes")})");
        }
        catch (Exception ex)
        {
            ImpLog.Error($"wiki: index regen failed: {ex.Message}");
        }

        sw.Stop();

        var result = new WikiResult(
            WikiId: manifest.WikiId,
            RepoRoot: manifest.RepoRoot,
            WikiDir: manifest.WikiDir,
            ArchiveDir: archiveDir,
            PagesWritten: manifest.Targets.Count(t => t.Decision == WikiDecision.Run && t.Status == WikiEntryStatus.Done),
            PagesSkippedUnchanged: manifest.Targets.Count(t => t.Status == WikiEntryStatus.Skipped),
            PagesOversizedStub: manifest.Targets.Count(t => t.Decision == WikiDecision.Stub && t.Status == WikiEntryStatus.Done),
            PagesFailed: manifest.Targets.Count(t => t.Status == WikiEntryStatus.Failed),
            WallSeconds: Math.Round(sw.Elapsed.TotalSeconds, 2),
            EstimatedCostUsd: Math.Round(totalCost, 4));

        ImpLog.Info($"wiki: complete wikiId={manifest.WikiId} written={result.PagesWritten} skipped={result.PagesSkippedUnchanged} stub={result.PagesOversizedStub} failed={result.PagesFailed} cost={result.EstimatedCostUsd} wall={result.WallSeconds}s");

        return JsonSerializer.Serialize(result, ResultJsonOpts);
    }

    // Adaptive splitting (item 11). Calls the orchestrator with the
    // dir's file list; on success, returns N cluster entries to replace
    // the stub. On any failure (no provider, validation error, model
    // error), returns null and the caller falls back to the v0 stub.
    static async Task<List<WikiManifestEntry>?> TrySplitStubAsync(
        IConfiguration config,
        WikiManifest manifest,
        WikiManifestEntry stubEntry)
    {
        var orchestratorProvider = config["Wiki:Provider"] ?? config["ActiveProvider"];
        if (string.IsNullOrEmpty(orchestratorProvider))
        {
            ImpLog.Info($"wiki: no orchestrator provider configured; cannot split {stubEntry.SourcePath}");
            return null;
        }

        WikiSplitProposal proposal;
        try
        {
            var orchestrator = Providers.CreateForProvider(config, orchestratorProvider);
            ImpLog.Info($"wiki: requesting cluster proposal for {stubEntry.SourcePath} via {orchestratorProvider}");
            proposal = await WikiSplitter.ProposeAsync(
                orchestrator: orchestrator,
                repoRoot: manifest.RepoRoot,
                sourcePath: stubEntry.SourcePath,
                maxDirBytes: manifest.MaxDirBytes);
        }
        catch (Exception ex)
        {
            ImpLog.Error($"wiki: splitter failed for {stubEntry.SourcePath}: {ex.Message}");
            return null;
        }

        var entries = new List<WikiManifestEntry>();
        foreach (var cluster in proposal.Clusters)
        {
            var pagePath = ClusterPagePath(manifest.WikiDir, stubEntry.SourcePath, cluster.Slug);
            entries.Add(new WikiManifestEntry(
                SourcePath: stubEntry.SourcePath,
                PagePath: pagePath,
                Decision: WikiDecision.Run,
                SourceTreeSha: ClusterSha(cluster),
                SourceBytes: cluster.TotalBytes,
                FileCount: cluster.Files.Count,
                Status: WikiEntryStatus.Pending,
                ResearchId: null,
                ResearchArchive: null,
                StartedAt: null,
                CompletedAt: null,
                Error: null,
                ClusterSlug: cluster.Slug,
                ClusterRationale: cluster.Rationale,
                ClusterFiles: cluster.Files));
        }
        return entries;
    }

    static string ClusterPagePath(string wikiDir, string sourcePath, string clusterSlug)
    {
        var combined = string.IsNullOrEmpty(sourcePath) ? clusterSlug : $"{sourcePath}/{clusterSlug}";
        return Path.Combine(wikiDir, combined + ".md").Replace('\\', '/');
    }

    static void DeleteLegacyParentStub(WikiManifest manifest, string sourcePath)
    {
        if (string.IsNullOrEmpty(sourcePath)) return;
        var stubPath = Path.Combine(manifest.RepoRoot, manifest.WikiDir, sourcePath + ".md");
        if (File.Exists(stubPath))
        {
            try
            {
                File.Delete(stubPath);
                ImpLog.Info($"wiki: removed legacy parent stub {stubPath}");
            }
            catch (Exception ex)
            {
                ImpLog.Error($"wiki: failed to remove legacy stub {stubPath}: {ex.Message}");
            }
        }
    }

    // Per-cluster cache key. Combines the cluster slug with the sorted file
    // list so re-runs that yield the same proposal hit the SHA cache. Files
    // changing under the cluster will rotate the SHA via the file list (not
    // contents — coarse but correct for v0; cluster-content hashing is a
    // post-v0 polish item).
    static string ClusterSha(WikiClusterProposal cluster)
    {
        var sorted = cluster.Files.OrderBy(f => f, StringComparer.Ordinal);
        var payload = cluster.Slug + "\0" + string.Join("\0", sorted);
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    static async Task<string?> TrySynthesizeIndexBodyAsync(
        IConfiguration config,
        WikiManifest manifest,
        IReadOnlyList<WikiIndexEntry> entries)
    {
        var orchestratorProvider = config["Wiki:Provider"] ?? config["ActiveProvider"];
        if (string.IsNullOrEmpty(orchestratorProvider))
        {
            ImpLog.Info("wiki: no Wiki:Provider or ActiveProvider; using deterministic index body");
            return null;
        }

        try
        {
            var orchestrator = Providers.CreateForProvider(config, orchestratorProvider);
            var repoName = Path.GetFileName(manifest.RepoRoot.TrimEnd(Path.DirectorySeparatorChar));
            ImpLog.Info($"wiki: synthesizing index body via {orchestratorProvider} (entries={entries.Count})");
            var body = await WikiIndexSynthesizer.RenderBodyAsync(orchestrator, entries, repoName);
            return body;
        }
        catch (Exception ex)
        {
            ImpLog.Error($"wiki: orchestrator index synthesis failed, falling back to deterministic: {ex.Message}");
            return null;
        }
    }

    static IConfigurationSection? ResolveProviderSection(IConfiguration config, string? activeProvider)
    {
        if (string.IsNullOrEmpty(activeProvider)) return null;
        return config.GetSection("ChatProviders").GetChildren()
            .FirstOrDefault(p => string.Equals(p["Name"], activeProvider, StringComparison.OrdinalIgnoreCase));
    }

    static async Task<(WikiManifestEntry Entry, decimal Cost)> ProcessTargetAsync(
        IChatClient chat,
        IConfiguration config,
        WikiManifest manifest,
        WikiManifestEntry entry,
        string? modelName)
    {
        var startedAt = DateTimeOffset.UtcNow;

        switch (entry.Decision)
        {
            case WikiDecision.Skip:
                return (entry with
                {
                    Status = WikiEntryStatus.Skipped,
                    StartedAt = startedAt,
                    CompletedAt = DateTimeOffset.UtcNow,
                }, 0m);

            case WikiDecision.Stub:
            {
                var stubEntry = entry with
                {
                    Status = WikiEntryStatus.Done,
                    StartedAt = startedAt,
                    CompletedAt = DateTimeOffset.UtcNow,
                };
                WritePage(manifest, stubEntry, report: null, modelName);
                return (stubEntry, 0m);
            }

            case WikiDecision.Run:
                return await DispatchRunAsync(chat, config, manifest, entry, startedAt, modelName);

            default:
                return (entry with
                {
                    Status = WikiEntryStatus.Failed,
                    StartedAt = startedAt,
                    CompletedAt = DateTimeOffset.UtcNow,
                    Error = $"unknown decision: {entry.Decision}",
                }, 0m);
        }
    }

    static async Task<(WikiManifestEntry Entry, decimal Cost)> DispatchRunAsync(
        IChatClient chat,
        IConfiguration config,
        WikiManifest manifest,
        WikiManifestEntry entry,
        DateTimeOffset startedAt,
        string? modelName)
    {
        var descriptor = BuildDescriptor(manifest, entry);
        var researchArchive = ResearchArchive.DirectoryFor(manifest.RepoRoot, descriptor);

        string envelope;
        try
        {
            envelope = await ResearchRunner.RunAsync(
                config: config,
                modeName: "wiki",
                descriptor: descriptor,
                repoRoot: manifest.RepoRoot,
                toolBudgetOverride: manifest.ToolBudget);
        }
        catch (Exception ex)
        {
            ImpLog.Error($"wiki: dispatch failed sourcePath={entry.SourcePath} researchId={descriptor.ResearchId}: {ex.Message}");
            var failed = entry with
            {
                Status = WikiEntryStatus.Failed,
                ResearchId = descriptor.ResearchId,
                ResearchArchive = researchArchive,
                StartedAt = startedAt,
                CompletedAt = DateTimeOffset.UtcNow,
                Error = ex.Message,
            };
            WritePage(manifest, failed, report: null, modelName);
            return (failed, 0m);
        }

        var (terminal, error) = ParseEnvelopeOutcome(envelope);
        var status = terminal == "success" ? WikiEntryStatus.Done : WikiEntryStatus.Failed;
        var report = LoadReport(researchArchive);
        var cost = status == WikiEntryStatus.Done ? (report?.Usage?.EstimatedCostUsd ?? 0m) : 0m;

        var updated = entry with
        {
            Status = status,
            ResearchId = descriptor.ResearchId,
            ResearchArchive = researchArchive,
            StartedAt = startedAt,
            CompletedAt = DateTimeOffset.UtcNow,
            Error = status == WikiEntryStatus.Failed ? (error ?? terminal ?? "unknown") : null,
        };

        WritePage(manifest, updated, status == WikiEntryStatus.Done ? report : null, modelName);
        return (updated, cost);
    }

    // Render the per-target page and write it under wiki/<...>.md. Skips
    // empty source paths (those would collide with the index slot). Failures
    // here are non-fatal — logged, and the manifest is still updated. The
    // next run picks up the missing page via the SHA-mismatch path.
    static void WritePage(WikiManifest manifest, WikiManifestEntry entry, ResearchReport? report, string? modelName)
    {
        if (string.IsNullOrEmpty(entry.SourcePath)) return;

        var ctx = new WikiPageContext(
            PagePath: entry.PagePath,
            SourcePath: entry.SourcePath,
            SourceTreeSha: entry.SourceTreeSha,
            SourceBytes: entry.SourceBytes,
            FileCount: entry.FileCount,
            MaxDirBytes: manifest.MaxDirBytes,
            Mode: "wiki",
            ModelName: modelName,
            ProviderName: null,
            ResearchId: entry.ResearchId,
            GeneratorVersion: WikiPageRenderer.CurrentGeneratorVersion,
            GeneratedAt: entry.CompletedAt ?? DateTimeOffset.UtcNow,
            WorktreeDirty: report?.WorktreeDirty,
            ClusterSlug: entry.ClusterSlug,
            ClusterRationale: entry.ClusterRationale,
            ClusterFiles: entry.ClusterFiles);

        try
        {
            var markdown = WikiPageRenderer.Render(ctx, entry, report);
            var absPath = Path.Combine(manifest.RepoRoot, entry.PagePath);
            Directory.CreateDirectory(Path.GetDirectoryName(absPath)!);
            File.WriteAllText(absPath, markdown);
        }
        catch (Exception ex)
        {
            ImpLog.Error($"wiki: page write failed sourcePath={entry.SourcePath} pagePath={entry.PagePath}: {ex.Message}");
        }
    }

    static ResearchReport? LoadReport(string researchArchive)
    {
        var reportPath = Path.Combine(researchArchive, "report.json");
        if (!File.Exists(reportPath)) return null;
        try
        {
            return JsonSerializer.Deserialize<ResearchReport>(
                File.ReadAllText(reportPath), ResearchReportJson.Options);
        }
        catch (Exception ex)
        {
            ImpLog.Error($"wiki: report load failed at {reportPath}: {ex.Message}");
            return null;
        }
    }

    // Synthesise a TaskDescriptor for a wiki survey of the target directory.
    // The brief mirrors the YAML shape from project/wiki-plan.md but is
    // emitted as the markdown brief format the rest of the codebase already
    // round-trips through (## R-NNN: title, **Question:**, etc.) so the
    // brief.md sidecar stays human-readable and consistent with research-mode
    // archives.
    static TaskDescriptor BuildDescriptor(WikiManifest manifest, WikiManifestEntry entry)
    {
        var researchId = BriefParser.AllocateNextId(manifest.RepoRoot);
        var isCluster = entry.ClusterSlug is not null;
        var displayPath = isCluster
            ? $"{(entry.SourcePath.Length == 0 ? "<repo root>" : entry.SourcePath)} ({entry.ClusterSlug} cluster)"
            : entry.SourcePath.Length == 0 ? "<repo root>" : entry.SourcePath;

        var slugBase = entry.SourcePath.Length == 0 ? "root" : entry.SourcePath;
        if (isCluster) slugBase += "-" + entry.ClusterSlug;
        var slug = BriefParser.SlugFrom("wiki-" + slugBase);

        var sources = isCluster
            ? entry.ClusterFiles ?? Array.Empty<string>()
            : WikiPlanner.EnumerateSourceFiles(manifest.RepoRoot, entry.SourcePath);

        var question = isCluster
            ? $"Produce a wiki survey of the '{entry.ClusterSlug}' cluster of files inside {entry.SourcePath}. The cluster contains exactly the files listed under Sources — survey only those, no others."
            : $"Produce a directory survey of {displayPath} suitable for a wiki page that describes the contents of this directory to a reader who has not seen the source.";

        var subQuestions = new[]
        {
            "What does each file in this directory do?",
            "What are the entrypoints into this directory's code from elsewhere in the repo?",
            "What are the load-bearing types or functions?",
        };

        // Cluster runs override the system prompt's step 1 (list_dir).
        // The Sources list IS the inventory — listing the parent dir would
        // dump sibling-cluster files into the executor's context and almost
        // certainly cause it to survey the wrong files.
        var clusterScope = isCluster
            ? string.Join("\n",
                $"This is a cluster of {sources.Count} file(s) inside {entry.SourcePath}; the parent dir has been split because its total size exceeds the per-page threshold.",
                "**Skip step 1 of the system prompt (`list_dir`).** The file inventory is the Sources list below — read those files in order, write one finding per meaningful file, then call `finish_research`. Do not list the parent dir; do not read sibling-cluster files; do not read files at the repo root.",
                $"Cluster rationale: {entry.ClusterRationale ?? "(none provided)"}.")
            : "";

        var background = string.Join("\n",
            $"You are running in wiki mode against {displayPath}.",
            clusterScope,
            $"Stay inside this directory; only read outside to resolve a cross-reference.",
            $"Tool budget for this run is {manifest.ToolBudget} calls.");

        var expectedOutput = string.Join("\n",
            "- synthesis (<=80 words)",
            "- findings[] (one per meaningful file in the target, plus cross-cutting findings)",
            "- coverage with explored / not_explored / gaps; account for every file in the target");

        var sourceMarkdown = BuildBriefMarkdown(researchId, slug, question, background, subQuestions, sources, expectedOutput);

        return new TaskDescriptor(
            ResearchId: researchId,
            Slug: slug,
            Question: question,
            SubQuestions: subQuestions,
            SuggestedSources: sources is IReadOnlyList<string> roSources ? roSources : sources.ToList(),
            Forbidden: Array.Empty<string>(),
            Background: background,
            ExpectedOutput: expectedOutput,
            SourceMarkdown: sourceMarkdown);
    }

    static string BuildBriefMarkdown(
        string researchId,
        string slug,
        string question,
        string background,
        IReadOnlyList<string> subQuestions,
        IReadOnlyList<string> sources,
        string expectedOutput)
    {
        var sb = new StringBuilder();
        sb.Append("## ").Append(researchId).Append(": ").Append(slug).Append('\n').Append('\n');
        sb.Append("**Question:** ").Append(question).Append('\n').Append('\n');
        sb.Append("**Background:**\n").Append(background).Append('\n').Append('\n');
        sb.Append("**Sub-questions:**\n");
        foreach (var q in subQuestions) sb.Append("- ").Append(q).Append('\n');
        sb.Append('\n');
        if (sources.Count > 0)
        {
            sb.Append("**Sources:**\n");
            foreach (var s in sources) sb.Append("- ").Append(s).Append('\n');
            sb.Append('\n');
        }
        sb.Append("**Output:**\n").Append(expectedOutput).Append('\n');
        return sb.ToString();
    }

    // Pull terminal_state out of the JSON envelope returned by ResearchRunner.RunAsync.
    // Single field probe — avoids re-deserialising the whole thing or coupling
    // to ResearchResult's record type.
    static (string? Terminal, string? Error) ParseEnvelopeOutcome(string envelope)
    {
        try
        {
            using var doc = JsonDocument.Parse(envelope);
            var terminal = doc.RootElement.TryGetProperty("terminal_state", out var ts) ? ts.GetString() : null;
            string? error = null;
            if (doc.RootElement.TryGetProperty("blocked_reason", out var br) && br.ValueKind == JsonValueKind.Object)
            {
                if (br.TryGetProperty("summary", out var ex)) error = ex.GetString();
            }
            return (terminal, error);
        }
        catch
        {
            return (null, "could not parse research envelope");
        }
    }

    static readonly JsonSerializerOptions ResultJsonOpts = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
