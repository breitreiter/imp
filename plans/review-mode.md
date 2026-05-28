---
kind: plan
title: Nightly review mode (imp review)
state: exploring
created: 2026-05-28
updated: 2026-05-28
touches:
  features:
    - imp review (new subcommand)
    - Research/Modes.cs (new review-* modes)
    - imp/reviews/ (new report destination)
---

# Nightly review mode

A nightly sniff-test over a day's worth of commits on `main`,
producing a single BLUF-style report at `imp/reviews/<date>.md`.
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
  persisted cursor), not per-PR.
- Stay well within qwen's effective context per invocation; trade
  token count and wall-clock for safety.
- Produce one report Claude can skim in the morning: BLUF on top,
  must-fix → consider → fyi, with file:line citations.
- Catch what Roslyn / ReSharper can't.

Non-goals: replacing linters; gating commits; auto-applying fixes.

## Cadence and trigger

- New subcommand: `imp review [--since 24h | --since-last]`.
- Persists a cursor at `imp/_meta/review-cursor` (last reviewed
  commit SHA) so reruns are idempotent.
- Nightly trigger via systemd timer or the `/schedule` skill running
  `imp review --since-last`.
- Output: `imp/reviews/<YYYY-MM-DD>.md`, gnome-authored, no proposal
  flow (reports are reference, not edits to human-owned dirs).

## Unit of work

**Per-file across the window**, not per-commit. WIP commits on main
are too noisy. For each touched path:

- `git log -p --since=<cursor> -- <path>` gives one coherent diff.
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

## Confidence-scoring pass

Borrowed from the Cursor skill. After each axis produces findings,
a separate qwen call scores each finding 0–100 against this rubric:

- 0: false positive on light scrutiny, or pre-existing.
- 25: maybe-real, couldn't verify.
- 50: real but nitpicky.
- 75: real, likely hit in practice, or explicitly called out by
  rules/CLAUDE.md.
- 100: definitely real, evidence directly confirms.

Filter <80 before report assembly. Cheap insurance against qwen's
known noisiness on this codebase (see [[project_qwen_executor]]).

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

`imp/reviews/<YYYY-MM-DD>.md`:

```
# Review — 2026-05-28
Window: 2026-05-27 09:00 → 2026-05-28 09:00 UTC
14 commits, 23 files, 41 qwen runs, 0 skipped.

## BLUF
- 2 must-fix
- 5 consider
- 11 fyi
- Untested additions: 3 new public methods, no tests touched
- Trend: Foo.cs flagged for size growth 3 nights in a row

## Must-fix
1. <description> — <file>:<line> (axis: bug-scan, confidence 95)
   <citation>

## Consider
...

## FYI (analyzer pre-pass)
- Roslyn: 4 warnings (full list collapsed)
- dotnet format: clean
- ReSharper: 12 suggestions (top 3 listed)

## Untested additions
...

## Trend
(deferred)
```

## Implementation slots

- New mode entries in `Research/Modes.cs`: `review-bug`,
  `review-simplify-comments`, `review-rules`. Each reuses the
  existing fs research mode (read-only, network=none, fs tools)
  with a different system prompt.
- New per-axis prompts in `Prompts/`: `review-bug.md`,
  `review-simplify-comments.md`, `review-rules.md`,
  `review-confidence.md`.
- New top-level `imp review` CLI handler. Once it has >1 file,
  carve into `Review/` (`Imp.Review`) per
  [[feedback_layout]].
- Orchestrator responsibilities:
  - Resolve window (`--since`/`--since-last`).
  - Enumerate touched files via `git log --name-only`.
  - Run pre-pass (Roslyn / dotnet format / jb inspectcode).
  - For each file, decide which axes fire.
  - Dispatch qwen runs sequentially.
  - Run confidence-scoring pass over collected findings.
  - Assemble report; update cursor on success.

## Report framing — open problem

The report has two readers (Sonnet and Opus, in the morning) and we
need it to land in a narrow band:

- **Take it seriously.** Findings should be weighted enough that the
  parent model actually engages — not skimmed past as "the nightly
  bot complaining again."
- **Don't accept uncritically.** Qwen is the noisiest model in our
  stack on this codebase. The parent must treat findings as leads to
  verify, not verdicts to act on. A 95-confidence score from qwen is
  not the same as a 95-confidence score from Sonnet.

Tuning levers we have, none of them obviously right:

- Confidence scores: include them, but parent models may anchor on
  the number. Possibly express as bands ("likely real" / "worth a
  look") instead of integers.
- Source attribution: every finding tagged with which axis + which
  model produced it, so parent can discount qwen-only findings.
- Framing language: report writes findings as "qwen flagged X, here's
  the diff — verify before acting" rather than "X is broken."
- BLUF discipline: top of report is counts and *categories*, not
  specific claims, so the parent has to read into the body to act on
  anything.
- Pre-pass findings (Roslyn, ReSharper) can be stated more flatly —
  those *are* verdicts. Separating analyzer findings from qwen
  findings visually may help.

This will need iteration after v1 ships and we see how Sonnet/Opus
actually react to a few real reports. Worth a calibration note in
the first few weeks' reports themselves.

## Open questions

- ReSharper CLI on the strix box — install or skip? Falls back
  cleanly either way; affects pre-pass coverage.
- Empirical bundle threshold: at what point does combining axes
  hurt qwen's focus more than it saves on context-load? Worth one
  calibration pass after v1 lands.
- Do we want a `--dry-run` that prints the axis plan (which files,
  which axes, estimated run count) without invoking qwen? Useful
  for tuning the pre-filter thresholds.
- Where do report-derived TODOs land? Manual: parent Claude reads
  the morning report and decides. Automated would risk Opus-only
  decisions (deps, rules changes) sneaking through. Stay manual
  for v1.

## Phasing

1. **v1**: subcommand, cursor file, pre-pass, axes 1+2+6, confidence
   pass, report writer. No ReSharper, no trend tracking.
2. **v2**: rules-adherence axis, file-size axis, dependency-churn
   axis, ReSharper integration if installed.
3. **v3**: trend tracking once history exists.
