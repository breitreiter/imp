---
kind: plan
title: Borrow Graphify's symbol-graph techniques for research retrieval
state: exploring
created: 2026-07-14
updated: 2026-07-14
touches:
  files:
    - Substrate/Signals.cs
    - Substrate/Locate.cs
    - Research/
  features:
    - research-mode-retrieval
    - substrate-layer-0
links:
  cites:
    - plans/substrate-layers-design.md
    - plans/lsp-integration.md
---

# Borrow Graphify's symbol-graph techniques for research retrieval

Outcome: not started. Exploring whether Graphify's deterministic
code-graph approach is worth adapting to strengthen `imp research`
retrieval — and if so, how much of it, scoped to what.

Prompted by [Graphify](https://github.com/Graphify-Labs/graphify)
getting traction. Graphify is close to a productized version of this
repo's own unbuilt **Layer 0** (see
`plans/substrate-layers-design.md`), so the question is less "adopt a
new tool" and more "does Graphify's shape tell us how to finally
build Layer 0, and does research mode want it?"

## What Graphify actually does

Deterministic, no-LLM, nothing-leaves-the-machine code processing:

- **tree-sitter AST** across ~40 languages → per-file defs/refs.
- **Symbol resolution edges** across files: `calls`, `imports`,
  `inherits`, `mixes_in`. This is the part beyond a repo-map — an
  actual cross-file graph, not just a per-file skeleton.
- **Confidence tags** on every edge: `EXTRACTED` (literally in
  source) vs `INFERRED` (derived via symbol resolution).
- **Leiden community detection** → auto-labeled subsystems (high-level
  architecture without manual annotation).
- **Graph traversal retrieval** — `query` (scoped subgraph for an NL
  question), `path` (shortest link between two entities), `explain`
  (node + its relationships). Explicitly **not** vector embeddings
  for code.
- Docs/PDFs/images get LLM semantic extraction; inline `# WHY:` /
  `# NOTE:` comments become first-class graph nodes linking rationale
  to code.

## Where it lands against imp's existing design

| Graphify technique | imp status |
|---|---|
| tree-sitter AST, deterministic | = unbuilt Layer 0 (imp specced tree-sitter as SCIP *fallback*; Graphify makes it primary) |
| cross-file symbol edges | **net-new** — Layer 0 spec stops at per-file defs/refs/imports/signatures |
| `EXTRACTED` / `INFERRED` confidence tags | **net-new**; fits imp's provenance/drift discipline |
| Leiden community detection | overlaps Layer 2 concept-page *selection* |
| graph traversal instead of embeddings | direct tension with imp's current substrate embeddings |
| inline-comment rationale as graph nodes | overlaps the gnome's existing `hack:|workaround:|XXX` trigger |

Confirmed 2026-07-14: **Layer 0 does not exist in the codebase.** No
tree-sitter/Roslyn/SCIP dependency, no repo-map, no `symbols.jsonl`,
no PageRank. Code-finding today is regex + `git grep` (`Signals.cs`)
and Qwen3-8B doc-level embeddings (`EmbeddingIndex.cs`/`Locate.cs`) —
all text/vector-shaped, no AST.

## Why research mode specifically

`imp research` fans out over the substrate + codebase and traces
dependency/impact relationships. Today that tracing is grep +
LLM inference — the same "miss a call site, hallucinate the graph"
failure mode the LSP plan (`plans/lsp-integration.md`) worries about
for build mode.

A resolved symbol graph turns "who calls X" / "what would this change
break" / "shortest path from this entrypoint to that subsystem" into
**deterministic traversal** the research executor can cite, rather
than a grep it has to second-guess. That's a direct lever on research
answer quality, targeted at a real failure mode.

## Fit with recorded preferences

- **Graph traversal over embeddings** is the interpretable-ranking
  direction — explainable edges instead of cosine-score black magic.
- **`EXTRACTED`/`INFERRED` tags** are the weak-signals-that-compose
  shape: an extracted edge and an inferred edge are two signals to
  rank differently, not one authority.

Both point the same way: the borrowable core is the *deterministic,
explainable graph*, not the whole product.

## The tension to decide

imp's substrate design ruled **"Embedding/RAG retrieval out of scope;
structural + grep wins at this scale (Cline's bet)"** — then shipped
substrate-doc embeddings anyway. Graphify agrees structural beats
vectors but goes **much heavier** than the Aider repo-map Layer 0
promised: a full resolved call graph + Leiden clustering is real
infra (per-language grammars, incremental invalidation, community
detection), well past a "token-budgeted PageRank skeleton."

So the decision isn't binary adopt/reject — it's *how far down the
Graphify stack to go.*

## Proposed scoping (exploring — not decided)

**Cheap, aligned first bet** — symbol-graph edges + confidence tags
as a research-mode retrieval primitive, **C# only, Roslyn-based**:

- Roslyn resolves call/inherit/reference edges for C# with no
  tree-sitter grammar dependency at all — sidesteps the "new native
  dep" question (see below) for the validation phase.
- Emit a per-repo edge list (`calls`/`inherits`/`references`) with
  `EXTRACTED`/`INFERRED` tags into the gitignored `.imp/` cache —
  finally the concrete first instance of Layer 0.
- Expose to the research executor as a traversal tool: "references to
  symbol S", "path from A to B". Measure whether research answers
  cite the graph and whether grep-and-guess drops.

**Defer** until the cheap bet proves out:

- **tree-sitter for the other ~40 languages.** Only worth the native
  dependency once Roslyn-on-C# shows the graph earns its keep. New
  native dep = parent-model decision regardless.
- **Leiden community detection → concept-page selection.** Needs
  Layer 0 to exist before there's anything to cluster; speculative
  until then.
- **Inline-comment-as-graph-node.** The gnome already has a
  comment-scanning trigger; fold in only if the graph makes it
  cheap.

## Open questions

- Roslyn-first vs tree-sitter-first. Roslyn gives real symbol
  resolution for the dogfood language with zero grammar deps;
  tree-sitter gives breadth but coarser (no true resolution without
  a resolver layer). Recommend Roslyn-first for validation. Note the
  overlap with `plans/lsp-integration.md`'s "cheap first step"
  (Roslyn diagnostics, host-side) — same host-Roslyn machinery could
  serve both; worth building once.
- Does the graph live in `.imp/` (build cache, regenerable) or does
  any of it get promoted into curated substrate? Edges are
  regenerable-from-code → cache. But `INFERRED`-with-rationale edges
  might be learning-shaped.
- Research-mode-only, or does build mode want the same traversal
  tools? (LSP plan already argues build mode does — converging
  need.)
- Incremental invalidation. Graphify commits graph snapshots to VCS;
  imp's Layer 0 spec says gitignored + content-hash incremental. Keep
  gitignored — a committed graph is a merge-conflict and staleness
  magnet.
- Cold-start cost per research run vs amortization (same concern as
  the LSP plan's indexing-payback question).

## Prior art / relationship to existing plans

- `plans/substrate-layers-design.md` — Layer 0 is the home for this;
  this plan is the "how" that layer's spec deferred.
- `plans/lsp-integration.md` — adjacent: LSP gives the *executor*
  live semantic tools in-worktree; this gives *research* a persisted
  queryable graph. Shared Roslyn host machinery; build once.
- Graphify (Graphify-Labs) — the concrete borrow target. Archive the
  repo as a `reference/` entry if this moves past exploring.
- Aider repo-map / SCIP / stack-graphs — already cited by the
  substrate design as Layer 0's lineage.
