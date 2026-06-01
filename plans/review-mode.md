---
kind: plan
title: Nightly review mode (imp health)
state: ready
created: 2026-05-28
updated: 2026-05-30
touches:
  features:
    - imp health (new subcommand)
    - Research/Modes.cs (new review-* modes)
    - imp/health/ (new report destination)
---

# Nightly review mode

A nightly sniff-test over a day's worth of commits on `main`,
producing a single BLUF-style report at `imp/health/<date>.md`.
Loosely modeled on Cursor's thermo-nuclear code-quality review skill,
but adapted for our shape: no PR boundary, sequential qwen executor,
strict per-run context budget, and many small checks instead of one
mega-check.

Source inspiration: `cursor/plugins` → `cursor-team-kit/skills/
thermo-nuclear-code-quality-review`. The structure here borrows the
confidence-scoring pass and the bias toward structural-over-stylistic
findings; the cleaving strategy and orchestration are ours.

## Goals

- Run nightly, unattended.
- Operate against the last 24h of `main` (or `--since-last` via a
  persisted watermark), not per-PR.
- Stay well within qwen's effective context per invocation; trade
  token count and wall-clock for safety.
- Produce one report Claude can skim in the morning: BLUF on top,
  must-fix → consider → fyi, with file:line citations.
- Catch what Roslyn / ReSharper can't.

Non-goals: replacing linters; gating commits; auto-applying fixes.

## Cadence and trigger

- New subcommand: `imp health [--since 24h | --since-last]`.
- Persists a watermark at `imp/_meta/review-watermark` (last reviewed
  commit SHA) so reruns are idempotent.
- Nightly trigger via systemd timer or the `/schedule` skill running
  `imp health --since-last`.
- Output: `imp/health/<YYYY-MM-DD>.md`, gnome-authored, no proposal
  flow (reports are reference, not edits to human-owned dirs).

## Unit of work

**Per-file across the window**, not per-commit. WIP commits on main
are too noisy. For each touched path:

- `git log -p --since=<watermark> -- <path>` gives one coherent diff.
- `imp/_index/by-file/<path>.md` (if present) gives the gnome's
  digest of what to know first — free context win.
- Current file content is attached only if <800 lines; otherwise
  changed regions + 50 lines of surrounding context.

## Pre-pass (no qwen)

Runs once before any LLM invocation. Findings from this pass land
directly in the report.

1. **Roslyn analyzers** — `dotnet build` already emits these; capture
   warnings/errors per file.
2. **`dotnet format --verify-no-changes`** — style/whitespace deltas.
3. **`jb inspectcode` (ReSharper CLI, SARIF)** — optional, falls back
   gracefully if the binary isn't installed.

Pre-pass output serves two purposes:
- Mechanical findings go straight into the report.
- Per-file SARIF excerpts are passed into qwen briefs as "the
  analyzer already caught these — find what it can't." Lowers
  false-positive rate and gives qwen a calibration anchor.

## Per-file axes (qwen, triggered selectively)

A cheap regex/path/size pre-filter decides which axes fire per file.
Most files will trigger 1–2 axes.

1. **Bug scan** — diff-focused, no broad codebase reads. Always fires
   on production code changes.
2. **Simplification + comment drift** (bundled) — fires on files
   >100 lines or >50-line diffs. One prompt with two questions; they
   share inputs, and qwen's context-load cost dominates output cost
   on this hardware.
3. **Rules adherence** — fires only if a `rules/*.md` file touches
   the path or feature area in question. Loads relevant rules + the
   diff.
4. **File-size discipline** — fires only if file is >500 lines or
   grew >20% in the window. Cursor's 1k-line threshold is the
   hard-flag line.

## Cross-cutting axes (one qwen run each)

5. **Dependency churn** — `git diff` over `*.csproj` and lockfiles.
   Flags only, never approves (dep decisions are Opus territory; see
   [[feedback_dep_decisions_are_opus]]).
6. **Untested additions** — list new public methods / new files
   under non-test dirs in the window. For each, does any test file
   in the window reference it by name or touch the same area? Flags
   the "you have xUnit set up and shipped a day's worth of code with
   no tests, what up?" pattern. Mostly `grep` + one qwen call to
   summarize whether the untested surface is load-bearing.
7. **Trend tracking** — compare to last N reports. "File X flagged
   for size growth 3 nights running" composes weak signals into a
   stronger one ([[feedback_weak_signals_compose]]). **Deferred**
   until we have ≥7 reports to compare against.

## Confidence-scoring pass (internal filter)

Borrowed from the Cursor skill, but used as an internal noise filter
only — **the score never appears in the report**. After each axis
produces findings, a separate qwen call scores each finding 0–100:

- 0: false positive on light scrutiny, or pre-existing.
- 25: maybe-real, couldn't verify.
- 50: real but nitpicky.
- 75: real, likely hit in practice, or explicitly called out by
  rules/CLAUDE.md.
- 100: definitely real, evidence directly confirms.

Drop <80 before report assembly. Score lives in `trace.jsonl` for
debugging, not in the report — surfacing an integer invites the
parent model to anchor on it ([[feedback_semantic_thresholds_opaque]]),
and a qwen 95 is not a Sonnet 95. Composability of evidence tags
(below) does the credibility work in the report itself.

## Context budget per qwen run

Hard cap input around 40–60K tokens, ordered:

1. Diff hunk for the file (always).
2. `imp/_index/by-file/<path>.md` if present.
3. SARIF excerpt for this file from the pre-pass.
4. Relevant `rules/*.md` (axis 3 only).
5. Current file content if <800 lines; else changed regions + 50
   lines context.

Nothing else. Resist giving qwen the whole repo — it'll hallucinate
([[project_qwen_executor]]).

## Sequential execution

Strix Halo runs one qwen invocation at a time. Budget thinking:

- ~90s per run, 30–50 runs on a busy day → ~1h wall-clock.
- `--max-runs N` knob; cheap axes prioritized so we always finish
  *something* useful by morning.
- Bundle axes that share inputs (simplification + comment drift).
- Don't bundle axes that need different reasoning shapes (bug scan
  stays separate).

## Report shape

`imp/health/<YYYY-MM-DD>.md`. Two physical zones — *verdicts* and
*leads* — so the parent model can engage with each at the correct
register without having to infer it from per-finding language.

```
# Review — 2026-05-28
Window: 2026-05-27 09:00 → 2026-05-28 09:00 UTC
14 commits, 23 files, 41 qwen runs, 0 skipped.

## BLUF
- 2 verdicts to act on (analyzer + multi-corroborated)
- 5 corroborated leads to verify (qwen + at least one other signal)
- 11 single-source leads to skim (qwen-only, may be noise)
- Untested additions: 3 new public methods, no tests touched
- Trend: Foo.cs flagged for size growth 3 nights in a row

## Verdicts
Stated flatly. Analyzer output and qwen findings that pre-pass also
flagged on the same line/region. Treat as actionable.

1. <description> — <file>:<line>
   Source: Roslyn CA1822 + bug-scan (corroborated)
   <citation>

## Corroborated leads
Qwen findings backed by at least one composing signal: pre-pass hit
nearby, multi-axis fire, rules cited, recurring across reports.
Treat as worth verifying.

1. <description> — <file>:<line>
   Tags: [multi-axis: bug-scan, simplify] [rules-cited: rules/foo.md]
   Verify: <one-line on what to check>
   <citation>

## Single-source leads
Qwen-only, one axis, no corroboration. Treat as triage — skim, drop
the obvious noise, spot-check the rest.

1. <description> — <file>:<line>
   Tags: [bug-scan]
   Verify: <one-line on what to check>
   <citation>

## Analyzer pre-pass (full)
- Roslyn: 4 warnings (collapsed; promoted ones appear in Verdicts)
- dotnet format: clean
- ReSharper: 12 suggestions (collapsed)

## Untested additions
...

## Trend
(deferred)
```

### Evidence tags

Each lead carries 0+ composing tags. Tag count is the credibility
signal ([[feedback_weak_signals_compose]]); no integer scores.

- `analyzer-corroborated` — pre-pass flagged the same file:line
  region. Promotes the finding from a lead to a verdict.
- `multi-axis: <a>, <b>` — fired by more than one qwen axis.
- `rules-cited: <path>` — finding references a specific
  `rules/*.md` invariant.
- `recurring: <N> nights` — flagged in N prior reports for the
  same file/symbol (v3, gated on trend tracking).
- `diff-local` — touches code inside the review window (default;
  surfaced only when *absent*, e.g. for cross-cutting findings).

A lead with two or more tags is corroborated. Zero tags is
single-source. The zone a finding lands in is mechanical — no
judgment call at report-assembly time.

### Per-finding framing

- **Verdicts** use indicative voice: "X violates Y."
- **Leads** use the verify-against frame: "Qwen flagged X — verify
  against Y." Every lead carries a one-line `Verify:` field naming
  what the parent should check. Forces the report to articulate the
  doubt rather than assert certainty, and gives the parent a cheap
  next step instead of a verdict to react to.

### BLUF discipline

The BLUF gives engagement instructions, not claims. "2 verdicts to
act on" tells the parent model *how* to read the rest; it does not
front-load any specific finding. Specific claims live in the body so
the parent has to load the citation before acting.

## v1 implementation slots

v1 scope (per phasing): subcommand, watermark file, pre-pass, axes 1
(bug scan), 2 (simplify+comment-drift bundled), 6 (untested
additions), confidence filter, two-zone report writer. No ReSharper,
no rules axis, no file-size axis, no dep-churn axis, no trend.

### CLI surface

```
imp health [--since <duration> | --since-last]
           [--max-runs N]
           [--dry-run]
```

- `--since 24h` or `--since-last` (defaults to `--since-last` if a
  watermark exists, else `--since 24h`).
- `--max-runs N` caps total qwen invocations; cheap axes run first
  so the report is useful on early exit.
- `--dry-run` prints the axis plan (files × axes, estimated run
  count, estimated wall-clock) and exits without invoking qwen.
  Resolves the calibration open-question in v1 — we'll want this
  immediately for tuning pre-filter thresholds.

### Folder layout — carve `Review/` (`Imp.Review`) from day one

v1 already has more files than a flat top-level can hold cleanly
([[feedback_layout]]):

- `Review/ReviewCommand.cs` — CLI dispatch + flag parsing.
- `Review/ReviewOrchestrator.cs` — pipeline driver (steps below).
- `Review/Window.cs` — `--since` / watermark resolution to a SHA range.
  Edge cases:
  - Watermark SHA unreachable (force-push/rebase on main): warn,
    fall back to `--since 24h` from HEAD. Don't fail.
  - Empty window or HEAD == watermark: write a minimal "0 commits"
    report, exit success, advance watermark to HEAD anyway (no-op
    review still moves the marker forward).
  - Missing watermark on first run: behave as `--since 24h`.
  - Merge commits: include touched files. Per-file diff across the
    window is what matters; commit graph shape is irrelevant.
- `Review/FileEnumerator.cs` — `git log --name-only` →
  filtered touched-path set. v1 filter:
  - Include only `*.cs`.
  - Drop deleted files (no current content to review).
  - Drop `obj/`, `bin/`, `*.g.cs`, `*.Designer.cs`, and any file
    whose first 5 lines contain `<auto-generated`.
  - Test exclusion (`*Tests.cs`, under `Tests/`) wired in as a
    no-op until tests exist — costs nothing, ready when they appear.
  - Non-`.cs` paths (Prompts, plans, csproj, json) skip per-file
    axes entirely in v1. Prompt-drift is uncaught; acceptable.
- `Review/PrePass.cs` — runs `dotnet build` + parses Roslyn
  diagnostics, runs `dotnet format --verify-no-changes`. Produces
  per-file diagnostic lists. Builds in a **fresh worktree at
  window-head SHA**, thrown away after — reuses `Build/`'s
  worktree machinery. Determinism over WIP-pickup: running `imp
  review` with uncommitted changes in the working tree does not
  poison the SARIF.
- `Review/AxisPlanner.cs` — pre-filter: which axes fire on each
  file. Pure function over (path, size, diff-size). Same code feeds
  `--dry-run` and the real dispatch. v1 rules:
  - *Production code* = `.cs` files under `Build/`, `Research/`,
    `Wiki/`, `Tools/`, `Safety/`, `Infrastructure/`, `Review/`,
    or the repo root.
  - Bug-scan (axis 1): fires on any ≥1-line code change to a
    production-code file. No size floor — off-by-ones live in
    1-line diffs. `--max-runs` does fanout limiting, not the
    pre-filter.
  - Simplify+comments (axis 2): fires on production-code files
    where the file is >100 lines OR the diff in the window is
    >50 lines.
  - Untested (axis 6): cross-cutting, planned once at the run
    level, not per file.
- `Review/EvidenceTagger.cs` — given a finding (file:line + source
  axis), computes tags by checking pre-pass overlap, multi-axis
  hits, etc. Pure function over collected findings.
- `Review/ReportWriter.cs` — assembles the two-zone markdown.
- `Review/Watermark.cs` — read/write `imp/_meta/review-watermark`.

### Modes to register in `Research/Modes.cs`

All three fork the existing fs mode (read-only mount, no network,
no subprocess, `{read_file, grep, list_dir}` toolset). Differ only
in system prompt.

- `review-bug` — per-file bug-scan axis.
- `review-simplify-comments` — per-file simplify+comment-drift
  bundle.
- `review-untested` — single cross-cutting run for axis 6 (grep
  output + new-symbol list as input, summarize load-bearingness).
- `review-confidence` — internal-filter scoring pass. Same fs mode
  so it can re-read citations; output never reaches the report.

PreferredProvider defaults to qwen on all four (see
[[project_qwen_executor]] — qwen is research-only, which is exactly
what review is).

### Prompts to author in `Prompts/`

Four files: `review-bug.md`, `review-simplify-comments.md`,
`review-untested.md`, `review-confidence.md`. Design spec for each
in the next section.

#### Reuse of existing finish-tool shape

All four use `finish_research` and the existing `Finding` record
(`Research/ResearchReport.cs:46`) — no schema change in v1. Field
reuse for review:

- `claim` — the lead, phrased as observation, not verdict.
  Examples: "Possible null-deref at L:42 if `Resolve()` returns
  null"; "Simplify: nested `if`s collapse to a switch
  expression"; "Comment drift: header says 'returns null on
  miss' but code now throws."
- `citations[]` — file:line + excerpt as today; one citation per
  finding minimum.
- `confidence` — categorical (`high`/`medium`/`low`), as today.
  Lives in `trace.jsonl`, not the report. The separate
  `review-confidence` pass produces a 0–100 score in parallel,
  also internal-only.
- `reasoning` — repurposed for review-mode only: the
  verify-against hint. Reports render this directly as the
  `Verify:` line. Example: "Verify by checking whether
  `Resolve()` can return null at the call site, or whether
  upstream guards already ensure non-null."

The mode's system prompt is what enforces the voice and the
`reasoning` repurposing. Shared scaffolding (citations have
excerpts, coverage is explicit, convergence cadence, etc.) is
copied from `research-fs.md` into each axis prompt for v1; if
drift becomes a problem we extract a shared base in v2.

### Per-axis prompt design

Each prompt is structured: **job** → **what counts** → **what
doesn't count** → **voice** → **shared scaffolding** (citations,
coverage, convergence, stopping — copied from `research-fs.md`).

#### `review-bug.md`

- **Job.** Read the diff hunk for one file plus its surrounding
  context. Surface plausible correctness bugs *introduced or
  exposed by the changes in the window*. One file at a time.
- **What counts.** Null-deref, off-by-one, incorrect comparison
  (`==` on reference types, wrong operator), missing `await`,
  fire-and-forget tasks, resource leak (undisposed
  `IDisposable`), swallowed exceptions, wrong format string,
  race on shared mutable state, incorrect bounds check, broken
  invariant a comment or rule asserts.
- **What doesn't count.** Style. Naming. Anything the pre-pass
  SARIF already names on the same line — those become verdicts
  via the pre-pass, not leads from qwen. Findings outside the
  diff window unless the diff *caused* them (regression).
  Theoretical bugs with no plausible trigger path in this
  codebase.
- **Voice.** `claim` phrased as observation: "Possible X at L:N
  if Y." Not "X is broken." `reasoning` names the verification
  step: "Verify by checking whether Z."
- **Inputs handed in (orchestrator-provided).** Diff hunk;
  by-file index page if present; SARIF excerpt for this file
  ("the analyzer already named these — find what it can't");
  current file content if <800 lines.

#### `review-simplify-comments.md` (bundled)

- **Job.** Two questions in one run, because they share inputs
  and qwen's per-run context-load cost dominates output cost on
  Strix Halo:
  1. *Simplification.* Can any region of the diff or
     surrounding file be expressed more directly without losing
     intent?
  2. *Comment drift.* Does any comment claim something the code
     no longer does?
- **What counts (simplify).** Over-abstraction for one caller.
  Defensive checks at internal boundaries (per CLAUDE.md: "Trust
  internal code"). Dead branches. Duplication that an
  abstraction would now help with (three similar lines is fine;
  five with the same shape is suspicious). Helpers/wrappers that
  add nothing.
- **What counts (comment-drift).** Header comments that describe
  behavior the code no longer has. Comments that explain *what*
  the code does instead of *why* (per CLAUDE.md). Comments
  referencing removed code, old call sites, or stale issue/ticket
  numbers.
- **What doesn't count.** Style/whitespace. Renames for taste.
  Pre-pass SARIF overlaps.
- **Voice.** Claims prefixed `Simplify: ` or `Comment drift: ` so
  the report writer can group them. `reasoning` names the verify
  step ("Verify the alternative actually compiles and preserves
  behavior; check call sites X, Y").
- **Inputs.** Same as `review-bug.md`. Fires on files >100 lines
  or diffs >50 lines (`AxisPlanner` decides).

#### `review-untested.md` (single cross-cutting run)

- **Job.** One run for the whole window. Given the list of new
  public methods and new files added in the window, plus the
  list of test files that *did* change in the window, decide
  which untested additions are load-bearing.
- **What counts.** New public API that has plausible misuse
  modes. New error-handling paths. New parsing/serialization.
  Anything where regression cost > test cost.
- **What doesn't count.** CLI dispatch glue (covered by smoke
  runs). Pure data records / DTOs. Internal helpers reached
  through tested public surface. Code marked obviously
  experimental (e.g. plan files reference it as `state:
  exploring`).
- **Voice.** Claims of the form "Untested: `SymbolName` —
  reachable via X, plausible misuse Y." `reasoning` names the
  test that would matter ("Verify a test covering the empty-list
  path exists or add one").
- **Inputs.** Pre-computed by orchestrator (grep over diff for
  new `public` / new files; list of test files touched in the
  window). Qwen does *not* re-derive these; the grep output is
  the input. One read tool budget for spot-checks of
  load-bearingness only.

#### `review-confidence.md` (internal filter)

- **Job.** Score one finding 0–100 against the rubric in the
  "Confidence-scoring pass" section. Single output: a number
  plus a one-sentence justification.
- **Voice.** Mechanical. No hedging, no narrative — this output
  is parsed and dropped after filtering.
- **Inputs.** One finding (claim + citation + reasoning) at a
  time. Allowed to re-read the cited file via `read_file` for
  verification. No other context.
- **Output shape.** A dedicated `finish_confidence(score: int,
  justification: string)` tool, factory-built alongside the
  existing `finish_research` in `Research/ResearchTools.cs`. The
  `review-confidence` mode binds this instead of
  `finish_research` (the only mode in v1 that does). Findings
  scoring <80 are dropped; survivors flow into evidence-tagging.
  Score and justification persist in `trace.jsonl` for
  debugging, never in the report.

### Orchestrator pipeline

`ReviewOrchestrator.RunAsync(opts)` executes:

1. Resolve window (`Window`): SHA range + window timestamps.
2. Enumerate touched files (`FileEnumerator`): unique paths,
   filtered (drop deleted, non-`.cs`, generated).
3. Pre-pass (`PrePass`): Roslyn + dotnet format. Per-file diagnostic
   lists in memory.
4. Plan axes (`AxisPlanner`): for each file, the set of axes that
   fire. If `--dry-run`, print the plan and stop.
5. Dispatch sequentially, cheap axes first, respecting `--max-runs`.
   Each per-file axis becomes one `ResearchRunner` invocation in
   the corresponding mode, with the context-budget ordering from
   the "Context budget per qwen run" section.
6. Run cross-cutting axis 6 (`review-untested`) once.
7. Confidence filter (`review-confidence`): score each finding,
   drop <80. Score retained in `trace.jsonl`, never in the report.
8. Tag (`EvidenceTagger`): compute evidence tags for each survivor.
   Two-zone assignment is mechanical from tag set.
9. Write report (`ReportWriter`): `imp/health/<YYYY-MM-DD>.md`.
10. Update watermark (`Watermark.Write(headSha)`) **only on full
    completion** — all planned runs executed, report assembled. If
    `--max-runs` cut the run short, leave the watermark where it
    was; next night re-reviews from the same SHA with a wider
    window. The report carries a prominent banner when this
    happens ("Budget hit at N/M runs; watermark not advanced; raise
    `--max-runs` or narrow window") so the user knows coverage is
    being deferred, not lost.

### Out of v1 (deferred to v2/v3)

- `review-rules` mode + prompt (axis 3).
- File-size axis (axis 4) — straight grep on file length, no qwen
  needed; v2 because pre-filter logic gets clearer once we've seen
  axis-1/2 false positives.
- Dependency-churn axis (axis 5).
- ReSharper integration in `PrePass`.
- Trend axis (axis 7) — needs ≥7 prior reports.
- `recurring:` evidence tag — same gate.

## Report framing — resolved approach

Two readers (Sonnet and Opus, in the morning), narrow target band:

- **Take it seriously.** Weighted enough that the parent engages.
- **Don't accept uncritically.** Qwen is the noisiest model in our
  stack on this codebase ([[project_qwen_executor]]). Findings are
  leads to verify, not verdicts.

The resolved shape — see "Report shape" above — pushes this tension
into the report structure itself rather than into per-finding
hedging language. Four mechanisms compose:

1. **Two-zone split.** Verdicts (analyzer + corroborated) live
   visually separated from leads (qwen-only). The parent knows the
   register before reading any specific finding.
2. **Composable evidence tags, not integer scores.** A lead's
   credibility is the count and kind of tags it carries
   ([[feedback_weak_signals_compose]]). No opaque single number for
   the parent to anchor on
   ([[feedback_semantic_thresholds_opaque]]). The qwen confidence
   pass survives as an internal filter; its score never reaches the
   report.
3. **Verify-against framing on every lead.** A `Verify:` line names
   what to check. The report points; it does not assert. Cheap next
   step for the parent instead of a verdict to react to.
4. **BLUF as engagement instructions.** "Act on / verify / skim" at
   the top, not specific claims. The parent loads the citation
   before acting.

Calibration debt: this is a first cut. After v1 ships and we see how
Sonnet/Opus actually react to a few real reports, expect to tune the
verb choices, the BLUF wording, and the tag set. First-few-weeks
reports should carry a short "how to read this" header that the
parent can compare against its own behavior, and we should plan a
review of the reviews around the 2-week mark.

## Open questions

- ReSharper CLI on the strix box — install or skip? Falls back
  cleanly either way; affects pre-pass coverage.
- Empirical bundle threshold: at what point does combining axes
  hurt qwen's focus more than it saves on context-load? Worth one
  calibration pass after v1 lands.
- Where do report-derived TODOs land? Manual: parent Claude reads
  the morning report and decides. Automated would risk Opus-only
  decisions (deps, rules changes) sneaking through. Stay manual
  for v1.

## Phasing

1. **v1**: subcommand, watermark file, pre-pass, axes 1+2+6, confidence
   pass, report writer. No ReSharper, no trend tracking.
2. **v2**: rules-adherence axis, file-size axis, dependency-churn
   axis, ReSharper integration if installed.
3. **v3**: trend tracking once history exists.

## Deferred: bootstrap mode for existing codebases

Everything above assumes a window — yesterday's diffs. An existing
codebase adopting imp wants a different thing: a one-shot sweep over
the *whole* repo to seed an initial state. That's significantly more
complex:

- Fanout is the entire tree, not a day's worth of files. Run-count
  budgeting becomes the dominant concern.
- No diff to anchor on — qwen has to reason about whole files cold,
  which is exactly the mode it's worst at on this codebase.
- The "untested additions" axis loses its window-based heuristic;
  needs a different shape (coverage map? per-namespace summary?).
- Output isn't a daily report — it's a backlog the user works
  through over weeks. Different report shape, different lifecycle.

Worth its own plan when we get there. Likely composes with
`project-migrate` rather than living inside `imp health`.
