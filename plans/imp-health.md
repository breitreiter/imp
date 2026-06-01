---
kind: plan
title: imp health — codebase vibe check from CI output
state: exploring
created: 2026-05-10
updated: 2026-05-10
provenance:
  source: project-migrate-skill:M-2026-05-10-1855
  migrated_at: 2026-05-10
---

# imp health — codebase vibe check from CI output

> **Name collision (2026-05-30):** the `imp health` command name is now
> taken by the shipped nightly code-review sweep (renamed from `imp review
> [--since]`; see plans/review-mode.md). This plan — a CI-output vibe check —
> is unbuilt and would need a different verb (e.g. `imp pulse`) or to be
> folded into the sweep's report. Kept as a distinct idea for now.

Outcome: not started — feature idea captured 2026-05-10, no code
yet, open questions still unresolved on scheduling, output target,
tidy-integration, and provider choice.

> Periodic "vibe check" on codebase health. Imp consumes structured
> output from existing tooling (build logs, static-analysis reports,
> nightly test runs) and produces a one-paragraph judgment on
> direction: is tech debt getting better or worse, and roughly where?

## What it does

A scheduled or on-demand command that feeds the day's accumulated
diagnostic output into a model and asks: **how much worse did we
make things today?**

Output is qualitative — short prose, not a metric. The goal is to
surface drift before it accumulates into a refactor emergency. The
model's job is synthesis across weak signals, the kind of thing a
senior engineer notices in a glance and a dashboard misses.

## Inputs

Mechanical sources, all already produced by existing CI:

- Build output (warnings, errors, deprecation notices, slow targets)
- Static-analysis reports (ReSharper, Roslyn analyzers, lint output)
- Test-run results (timing trends, intermittent failures, skipped
  tests, coverage shifts)
- Diff against the previous health check (what actually changed)

## Output

A brief substrate entry — probably a `learning`, possibly a new kind
(`health-check`?) — citing the specific signals that contributed.
For meaningful regressions, a separate drift entry that the gnome's
later sweep can act on.

## What this is NOT

- **Not a test runner.** Imp doesn't execute anything. It consumes
  output other tools produced. The whole pipeline is downstream of
  existing CI.
- **Not a metric dashboard.** Numbers are inputs; the output is a
  human-readable judgment.
- **Not a change-time reviewer.** `imp build` and `/ultrareview`
  cover that. `imp health` is a daily/weekly trend signal, not a
  per-change gate.

## Open questions

- **Scheduling.** Cron-driven nightly, or manual `imp health`? Likely
  both — manual for ad-hoc, scheduled for trend continuity.
- **Output target.** Substrate entry under `imp/learnings/`, or a
  separate stream like `imp/health/<date>.md`? Latter keeps the
  signal isolated and easy to glance at; former integrates with
  drift tracking.
- **Phase of tidy or its own command.** Could be a phase that fires
  during `imp tidy` if recent CI artifacts are present. Probably
  cleaner as its own command first; refactor later if natural.
- **Provider choice.** Synthesizing weak signals across multiple
  reports favors a more capable model (Sonnet/Opus class) over the
  cheap executor. Worth a `Health.Provider` config knob from day one.

## Why this is here

The capability is genuinely useful for any project that produces
diagnostic output it can't quite read at scale. Naming convention
inside imp is "health" — covers the use case and avoids implying
test-management functionality, which it isn't.
