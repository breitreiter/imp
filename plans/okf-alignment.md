---
kind: plan
title: Align imp substrate and trace artifacts to OKF (Open Knowledge Format)
state: exploring
created: 2026-06-18
touches:
  files:
    - imp/_meta/conventions.md
    - imp/learnings/*.md
    - imp/reference/*.md
    - imp/note/**/*.md
    - plans/*.md
    - rules/*.md
    - Build/Worktree.cs
    - Build/BuildResult.cs
  features: [substrate, tidy, proof-of-work]
provenance:
  author: human
---

# OKF alignment

## Background

Google Cloud published [Open Knowledge Format (OKF) v0.1](https://github.com/GoogleCloudPlatform/knowledge-catalog/blob/main/okf/SPEC.md)
on 2026-06-12. It's a vendor-neutral spec for structured knowledge as a
directory of markdown files with YAML frontmatter forming a graph via
normal markdown links. One required field per file: `type:`. Everything
else is open. No SDK, no proprietary runtime — plain files, git-friendly.

Imp's substrate under `imp/` already has the right shape:

- `imp/concepts/` → OKF narrative nodes
- `imp/learnings/` → OKF rationale nodes
- `imp/log.md` → OKF's optional `log.md` (exact analog)
- frontmatter discipline already in place

The only gap: imp uses `kind:` where OKF requires `type:`. Everything
else is already compliant or gracefully extensible.

## Why align at all

1. **Portability.** An OKF-compliant bundle is navigable by any agent cold,
   without knowing imp's conventions. Proof-of-work traces handed off to
   another engineer (or another Claude session) become self-describing.
2. **Google's tooling.** A static HTML visualizer and a BigQuery enrichment
   agent speak OKF natively — imp substrate and trace bundles become
   visualizable out of the box.
3. **No migration cost now.** Currently ~28 files with `kind:` frontmatter.
   A `sed` one-liner suffices. At ~200 files, a migration tool is required.

## What `kind:` → `type:` does and doesn't change

`kind:` and OKF's `type:` are semantically equivalent — both classify the
node type in the knowledge graph. The internal vocabulary (`learning`,
`plan`, `rule`, `reference`, `concept`) is unchanged; only the frontmatter
field name changes.

The C# codebase has one `kind` field (`ResearchReport.cs:57`) that is for
JSON citation types, not frontmatter — unaffected by this rename.

## Scope

### Phase 1 — Frontmatter rename (substrate + human dirs)

Rename `kind:` → `type:` in all markdown frontmatter:

- `imp/learnings/` (8 files)
- `imp/reference/` (2 files)
- `imp/note/processed/` (2 files)
- `imp/concepts/` (0 files with frontmatter currently — README is excluded)
- `plans/` (15 files)
- `rules/` (0 files currently — verify)

Update docs that define or reference the `kind:` vocabulary:

- `imp/_meta/conventions.md` — s/kind/type/ throughout the prose, schema
  table, and per-kind extensions table. Keep the section names readable
  (e.g. "## Types" instead of "## Kinds").
- `Prompts/research-fs.md`, `Prompts/research-fs-wiki.md` — these reference
  `kind: "file"` in citation JSON output schemas, not frontmatter. Verify
  no change needed (citations are a different `kind` namespace).
- Any tidy/gnome prompts that instruct the gnome to emit `kind:` frontmatter.

### Phase 2 — Bundle index

Add `imp/index.md` at the substrate root. OKF's optional `index.md` is the
progressive-disclosure entry point for the bundle — a reader (human or agent)
arriving cold starts here. Content: one-paragraph description of the
substrate, links to `learnings/`, `reference/`, `concepts/`, `log.md`.

Frontmatter: `type: index`, `title:`, `created:`.

### Phase 3 — Trace bundles

Make `.trace/` directories OKF-compliant so proof-of-work artifacts are
self-describing when shared.

Each `<repo>.worktrees/<T-NNN>.trace/` currently contains only `trace.jsonl`.
Add:

- `index.md` — frontmatter (`type: build-trace`, `task:`, `created:`,
  `repo:`, `status:`) plus a short prose summary of the build outcome.
  Links to `trace.jsonl` (if the reader can open it) and the proof-of-work
  summary inline.
- Wire into `Build/Worktree.cs` or `Build/BuildResult.cs` — wherever the
  trace dir is finalized after a build run.

The `proof-of-work.json` stays JSON (structured data); OKF doesn't require
everything to be markdown. The `index.md` is the navigable entry point.

## Approach

Phases 1 and 2 are pure text/docs work — no code changes. Phase 1 can be
done in one commit (sed pass + conventions update). Phase 2 is one new file.
Phase 3 requires a small C# change to write `index.md` when closing a trace.

Suggested order: 1 → 2 → 3. Phases 1 and 2 have zero risk; phase 3 is the
only one that touches running code.

## Not in scope

- Adding an OKF SDK or parser
- Changing the directory structure
- Modifying `imp tidy` graph-construction logic
- Adding cross-file link validation (OKF tooling handles this)
