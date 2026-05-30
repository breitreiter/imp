---
kind: plan
title: Semantic clustering for crowded substrate directories
state: exploring
created: 2026-05-15
updated: 2026-05-15
touches:
  features:
    - imp tidy (new late phase)
    - imp-promote (new apply handler for cluster proposals)
---

# Semantic clustering for crowded substrate directories

When a substrate directory accumulates too many flat files, navigation
degrades. This plan describes how `imp tidy` detects overcrowding and
proposes semantic subdirectory groupings via the standard proposal flow.

## Trigger

Any substrate directory (`learnings/`, `concepts/`, `reference/`) that
reaches 12+ `.md` files (excluding `README.md`) at the end of a tidy
run. Checked per-subdir — if subdirs already exist, the threshold
applies to each leaf, not the aggregate. No clustering is proposed for
already-organized trees.

## Signal gathering

Two weak signals composed:

**Embeddings** — cosine similarity matrix over all docs in the
directory, using the existing Qwen3 Embedding 8B provider. Produces a
distance structure without naming anything.

**LLM title scan** — filenames + first H1/paragraph (200 chars each)
fed to the executor. No full file reads. Ask it to propose 3–5 cluster
labels and assign each doc to exactly one label.

The embedding structure validates the LLM grouping: if two docs placed
in the same cluster have cosine distance > 0.5, they are flagged as
split candidates. This keeps the LLM honest about cross-cutting docs
without making embeddings load-bearing. See
`imp/learnings/weak-signals-compose.md`.

## Output format

A proposal at `imp.imp-proposals/P-NNN-cluster-<dirname>.md` containing:

- Proposed subdirectory names (kebab-case slugs)
- Doc → subdirectory assignment table
- Any flagged split candidates with their distance score and the two
  candidate clusters
- A proposed `README.md` stub for each new subdir

The proposal contains no shell commands — that is the apply step,
handled by `/imp-promote`.

## Apply step

`/imp-promote` gets a new apply handler for cluster proposals. On
approval it:

1. Runs the `mv`s (atomic — all moves or none)
2. Rewrites any cross-file links that reference moved docs
3. Regenerates the parent directory `README.md` index

## Integration point

Tidy, not a new subcommand. The cluster check is a late tidy phase
after note processing and concept refresh. It only fires when the
threshold is crossed; otherwise it is a no-op. No new `imp cluster`
command.

## Edge cases

**Cross-cutting docs**: a doc that bridges two clusters goes in the
cluster with higher average cosine similarity to its neighbors, and is
listed in the other cluster's `README.md` as a "see also." Not
duplicated.

**Flat stays flat below threshold**: no proposal until 12+ docs.
Alphabetical order in a flat dir is sufficient below that.

**Already-clustered dirs**: threshold is per-subdir, not aggregate.
Existing organization is not flattened and re-clustered.

## Open questions

- Should cluster naming be a separate LLM pass with the full doc
  content, or is the title scan sufficient? Title scan is cheap but
  may produce generic labels for terse filenames.
- 12 is a guess for the threshold. May need tuning once `learnings/`
  grows past 10.
- Cluster proposals are in the Claude-approvable tier per the current
  trust gradient (plan edits, no rule changes). Confirm this is right
  — a bad clustering that moves 15 files is non-trivial to undo even
  if atomic.
