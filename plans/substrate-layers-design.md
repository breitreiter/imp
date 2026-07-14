---
kind: plan
title: Substrate layers — design snapshot
state: shipped
created: 2026-05-09
updated: 2026-05-10
shipped: 2026-05-10
provenance:
  source: project-migrate-skill:M-2026-05-10-1855
  migrated_at: 2026-05-10
---

# Substrate layers — design snapshot

Outcome: model has been substantially instantiated as of 2026-05-10
— `imp init` seeds the layout, `imp tidy` v0b ships, `imp note`
exists, gnome/proposal trust boundary is live. Open questions
section below remains active design surface.

State of the design as of 2026-05-10. Captures decisions from the
wiki/substrate architecture conversation. Open questions flagged
explicitly.

## Frame

The substrate is not one thing. It is a four-layer stack with
different freshness, authorship, and storage semantics per layer.
The wiki (`imp wiki`) produces layer 2; what was the existing
`project/` directory maps to layer 1 (and is renamed `imp/` —
see Layout below — to make tool ownership explicit); layers 0
(structural) and 3 (query) are new.

The unique gap imp fills, given prior art (SCIP/Kythe/Glean for
structure, Cursor/Cody for embeddings, DeepWiki for synthesis,
Doxygen for human rationale): **persisted, human-/agent-curated
rationale, anchored to stable symbol IDs, surviving refactor.**
Layers 0/2/3 exist so that layer 1 is reachable and useful.

## Layer 0 — structural

What: tree-sitter symbol map + Aider-style repo-map.

- Per-file content-hashed cache: `.imp/cache/<hash>.json` — top-level
  defs, refs, imports, signatures.
- Rendered views: `.imp/repo-map.txt` (paste-ready, PageRank-ranked,
  token-budgeted), `.imp/symbols.jsonl` (queryable).
- Manifest: `.imp/manifest.json` mapping file path → current hash.

Properties:

- Auto-generated, fully regenerable from code. Lossless w.r.t. code.
- Gitignored; build artefact, not substrate.
- File-incremental — per-file partial graphs (stack-graphs pattern),
  no global re-resolution required on most edits.
- SCIP-formatted symbol IDs where language indexers exist; tree-sitter
  fallback otherwise.

Why: solves cold-start orientation (Aider repo-map is the proven
shape) and exact-symbol queries (SCIP/stack-graphs idioms).

> **Before building Layer 0, read `plans/graphify-borrow.md`.**
> Graphify (Graphify-Labs) is close to a productized version of this
> layer — deterministic tree-sitter/AST symbol graph with cross-file
> `calls`/`imports`/`inherits` edges and `EXTRACTED`/`INFERRED`
> confidence tags. The borrow plan proposes a Roslyn-first, C#-only
> first cut of exactly this cache and how research mode would query
> it. Don't build Layer 0 from this snapshot alone without folding in
> that design work.

## Layer 1 — rationale

What: small markdown files, one claim each, anchored to code via
structured frontmatter. The "why" layer.

Shape:

- Genre folders kept as content-kind taxonomy:
  - Inside `imp/` (gnome-maintained): `learnings/`, `reference/`.
  - At repo root (human-maintained): `rules/`. Lifted out of
    `imp/` because rules are human-authored invariants, not
    gnome-derived; living next to `plans/`/`bugs/`/`TODO.md`
    matches their ownership.
  - Dropped: `aspirations/` (folds into `CLAUDE.md`), `tasks/`
    (TODO.md covers it). Reshuffle to query verbs was considered
    and rejected as hostile to authoring.
- Frontmatter: `id`, `kind`, `title`, `touches: {files, symbols,
  features}`, `links: {blocks, cites, supersedes, contradicts}`,
  `provenance: {author, origin}`, `verified-against: [{file, hash,
  lines}]`, `created`, `verified`, `status`.
- Body: one paragraph + Why/How-to-apply (existing convention).

Properties:

- NOT regenerable from code. If deleted and reconstructable from
  HEAD, it didn't belong here.
- Authored by humans/agents indirectly (see authoring inversion).
- Drift detection via `verified-against` content hashes. Nightly
  sweep flips `status: current` → `status: stale`; never auto-edits
  the body.
- One claim per entry; entries arguing two things are two entries
  with `links: extends:` between them.

`touches:` is the join key for layer 3 queries.

## Layer 2 — synthesis (on demand)

What: generated views composing layers 0 + 1.

- Per-file digests (one per source file): manifest of purpose,
  gotchas, related substrate, recent log entries. Cheap default.
- Concept pages: narrative views over a topic, generated from
  rationale entries with shared tags. Body pulled from layer 1
  entries; the page is a view, not a hand-authored essay.

Properties:

- Cached by `imp wiki` runs. Regeneratable.
- DeepWiki is the cautionary tale: LLM-fabricated rationale goes
  stale and lacks human voice. Layer 2 must be grounded in layer 1
  curated rationale, not LLM guessing.
- Concept pages produced selectively, not exhaustively. Per-file
  digest is the cheap default.

## Layer 3 — query (directory layout, not a CLI)

What: a directory layout the gnome maintains so the parent agent
queries the substrate using the tools it already has — `Read`,
`Glob`, `Grep`. No new verbs.

Full layout (at repo root):

```
<repo>/
  plans/                        # human — design intent / specs
  bugs/                         # human — bug reports
  TODO.md                       # human — running list
  rules/                        # human — hard invariants (substrate-shaped entries)
  CLAUDE.md                     # human — project orientation
  README.md                     # human — user-facing

  imp/                          # gnome territory
    _meta/                      # human (rare) — substrate conventions
    log.md                      # gnome — append-only history
    note/                       # write target (humans/agents append; gnome processes)
      inbox/, processed/, discarded/
    learnings/                  # gnome from notes — rationale entries
    reference/                  # gnome from note URLs — archived sources
    concepts/                   # gnome (layer 2) — narrative pages
    _index/                     # gnome (layer 3) — query layout
      by-file/<path>.md
      by-symbol/<sym>.md
      by-feature/<feat>.md

  .imp/                         # gitignored build cache (layer 0)
    cache/, repo-map.txt, symbols.jsonl, manifest.json
```

Why a layout instead of verbs: the agent's existing toolkit composes
to do almost everything a CLI would (Cline's bet, applied to
substrate). Adding verbs costs training, maintenance, and
discoverability without a corresponding payoff at this scale.
Filesystem paths are inherently more discoverable than CLI flags;
static files are diffable, cacheable, version-controllable; the
agent's `Read`/`Glob`/`Grep` already work correctly.

Verbs that were sketched and rejected:

- `imp digest <file>` — was always a static file pretending to be a
  verb. `Read imp/_index/by-file/<path>.md` is the same op.
- `imp page <topic>` — already a static file.
- `imp where <symbol>` — symbol disambiguation belongs in the
  pre-rendered `_index/by-symbol/<sym>.md`. Glob handles fuzzy
  lookup (`*convmgr*`). Grep handles the rest.
- `imp ref <thing> --kind/--status/--since` — flags collapse into
  frontmatter + ripgrep on layer 1 entries. Frontmatter must be in
  a canonical line format (`kind: learning` on its own line, not
  nested) so ripgrep can match cleanly.

What's lost: true ad-hoc joins across multiple dynamic axes,
output-format control (`--json`), sub-second response on enormous
substrates. None matters at our scale.

Conventions the layout depends on:

- Frontmatter on every layer 1 entry uses canonical line format
  (one field per line, not nested) so ripgrep matches work.
- `_index/by-symbol/<name>.md` files include both canonical symbol
  IDs and common aliases (e.g., `convmgr.md` → ConversationManager)
  so the agent's first guess at a glob pattern hits.
- `_index/` is regenerated wholesale by `imp tidy`; entries here
  are derived, not authoritative.

## Surviving CLI surface

Reads collapse into the filesystem. The CLI is for writes and
orchestration only:

- `imp note <text>` — capture (the dominant usage).
- `imp tidy` — gnome action; regenerates `_index/`, processes
  notes, runs drift checks.
- `imp init` — bootstrap a new substrate.

## Authoring inversion

Humans/agents do not author layer 1 directly. They write **notes**
into a note inbox; the gnome (nightly `imp tidy`) generates layer 1
entries from note items.

Note CLI:

- `imp note <text>` — one-shot capture (the 90% case)
- `imp note` (opens `$EDITOR`), `imp note -` (stdin)
- Auto-captured metadata: timestamp, repo, `IMP_SOURCE` env,
  `git rev-parse HEAD`
- Storage: `imp/note/inbox/<timestamp>-<slug>.md`, append-only;
  gnome moves to `processed/` or `discarded/<reason>/`
- URLs/refs inline in body; gnome detects and archives at tidy time
- Mgmt subcommands (rare): `imp note {ls, show, edit, drop, flush}`
- Note CLI stays dumb; smarts live in `imp tidy`

Output format (tuned for Claude relaying back):

```
$ imp note "<text>"
noted 2026-05-10-141233: <text echo>
```

Gnome triggers (auto-proposes layer 1 entries from):

- Note items (primary)
- Commits that revert other commits in the same week
- Code comments matching `hack:|workaround:|because |XXX`
- Repeated orientation questions in agent sessions (once logged)
- Long "why" paragraphs in PR descriptions

Anti-noise defenses:

- Convention: "if it would survive as a code comment, do that"
- Convention: "if reconstructable from HEAD, don't write it"
- Utility tracking: entries never cited in N months → flagged for
  archival
- Hard length cap: one paragraph body. Longer = split.

Research-source durability:

- Note with URL → gnome fetches, archives locally, submits to
  Wayback Machine, generates `reference/` entry + cross-links to
  whatever code/decision the source influenced.

## Human-maintained, code-adjacent (root level)

The substrate is one documentation layer, not the only one.
Human-owned conventions live at the repo root, not under `imp/`:

- `plans/` — design intent / specs / things to build.
- `bugs/` — bug reports.
- `TODO.md` — running todo list.
- `rules/` — hard project invariants. Substrate-shaped (frontmatter
  entries with `touches:`) so the gnome can flag drift, but
  authored directly by humans, not via note. The only layer 1
  folder lifted out of `imp/`.

`imp init` creates these as scaffolding. Imp does not own
`plans/`, `bugs/`, or `TODO.md` — they predate substrate adoption
and have decades of muscle memory. `imp tidy` may *scan* them to
propose note items (the migrate spec already does this for legacy
ingest), but the dirs themselves stay informal.

`rules/` is the exception: humans author directly, gnome reads
during tidy to track drift against cited code, but never edits the
body.

## Trust model (partitioned by directory ownership)

Imp's writes are gated by which directory they touch.

**Direct-write (imp's own territory, no approval gate):**
- `imp/learnings/`, `imp/reference/`, `imp/concepts/`, `imp/_index/`
- `imp/log.md`
- `imp/note/{processed,discarded}/` (the gnome moves files here from
  `inbox/` after processing)
- `.imp/` cache (gitignored anyway)

The gnome writes these as part of nightly `imp tidy`. Auditability
comes from git, not a proposal queue. Imp's commits use a distinct
git author set per-commit (no GH account needed — a noreply email is
fine):

```
git -c user.name="imp-gnome" \
    -c user.email="noreply@imp.local" \
    commit -m "imp tidy: <summary>"
```

This makes imp's edits trivially filterable
(`git log --author=imp-gnome` or `git log -- imp/`). Reverse-out is
plain `git revert <sha>`.

**Proposal-required (cross-boundary):**

Imp must NOT write directly to:
- `rules/` (root, human-authored hard invariants)
- `plans/`, `bugs/`, `TODO.md` (root, informal human docs)

When the gnome wants to suggest changes here, it writes proposals to
`<repo>.imp-proposals/P-NNN-<slug>.md` (sibling of the repo root,
gitignored in the main repo). The `/imp-promote` skill reviews and
applies them. This is the smaller, narrower trust gate that survives
the new model — most nights it's empty.

**Out of scope for the gnome entirely:**
- Code edits. Imp's *build mode* does code work in worktrees with its
  own proof-of-work pipeline; that's a different trust story and
  lives outside the substrate.

The principle: imp can write to its own dir without ceremony but
cannot reach into human territory unsupervised. Auditability sits on
git, not approval queues.

## Operating principles

- Eventual consistency over real-time. Daytime accumulates drift;
  nightly `imp tidy` reconciles. Don't engineer transactional
  freshness.
- Layer 3 is a directory layout, not a query CLI. The agent's
  existing `Read`/`Glob`/`Grep` toolkit is the query surface; the
  gnome's job is to lay out files so those tools compose well.
- Composition still matters — it just happens at gnome time
  (pre-rendered views) rather than query time.
- Trust model is partitioned by directory ownership (see Trust
  model section). Imp writes directly to its own dir; proposals
  exist only for cross-boundary edits to root-level human dirs.

## Relationship to existing `imp wiki`

`imp wiki` produces LLM-driven per-directory surveys at
`wiki/<path>.md`, committed with the codebase. It overlaps with what
this snapshot would call layer 2 (per-file/concept-style narrative)
but takes the DeepWiki shape — auto-fabricated descriptions of code,
no rationale grounding. Kept as a working reference and orientation
tool while `imp tidy` is built. Expected to retire once tidy
produces compelling layer-2 output (per-file digests grounded in
layer 1 rationale + layer 0 structural extracts).

## Out of scope

- Embedding/RAG retrieval. Per-project substrate is too small to
  need it; structural + grep wins at this scale (Cline's bet).
- Reorganizing layer 1 by query verb. Considered, rejected.
- A read-side query CLI (`imp where`, `imp ref`, `imp digest`,
  `imp page`). Considered, rejected — the agent's `Read`/`Glob`/
  `Grep` already compose to do this against a well-laid-out
  `_index/` directory.
- A spec-kit-style monolithic constitution. Considered, rejected —
  `rules/` + `_meta/conventions.md` + `CLAUDE.md` already cover
  the territory, decomposed into joinable entries. The gnome can
  render `imp/_index/principles.md` as a one-shot orientation
  view if needed. Amendment ceremony (formal versioning of
  invariants) is the one thing the decomposed approach loses;
  deferred until there's evidence we miss it.
- `aspirations/` as a standalone folder. Considered, dropped —
  most projects have 2–3 aspirations and they live in CLAUDE.md.
- `tasks/` as a standalone folder. Considered, dropped — TODO.md
  covers it. If TODO.md isn't structured enough later, add
  structure to it rather than introducing a parallel folder.
- Auto-fabricated rationale (DeepWiki shape). Layer 2 is a view
  over curated layer 1, not LLM hallucination.
- LLM-mediated `imp ask` NL interface. Parent agent already is an
  LLM; give it structured retrieval, let it synthesize.

## Open questions

- Trigger detection accuracy (gnome-proposed entries from
  commits/comments/sessions). The whole "review-not-write" pitch
  depends on this. Prototype before committing.
- Layer 1 granularity — schema doesn't enforce "one claim";
  convention does. Risk of essay drift.
- Layer 2 page selection — when does narrative pay off vs. digest?
- Frontmatter canonical line format — needs to be locked down so
  ripgrep filters work reliably.
- Symbol-alias generation in `_index/by-symbol/` — what aliases get
  generated, and how (heuristic vs. configured).
- Slash-command surface (`/note` etc.) — separate from CLI, not
  yet sketched.

## Prior art that shaped this

- SCIP (Sourcegraph) — stable symbol IDs as the join key.
- stack-graphs (GitHub) — file-incremental indexing.
- Aider repo-map — token-budgeted PageRank file skeleton as
  cold-start primitive.
- Kythe (Google) / Glean (Meta) — typed code-fact graph; we don't
  go this heavy but the shape informs layer 0.
- DeepWiki — cautionary; auto-fabricated rationale goes stale.
- Doxygen — model for human-authored rationale, but too tightly
  bound to declarations.
- Cline — "no index" is closer to the right default for per-repo
  substrate than Cursor's heavy infra.
