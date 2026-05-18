## [2026-05-10] design | four-layer substrate model

Full snapshot in `substrate-layers.md`. Headlines:

- Substrate is a four-layer stack: 0 structural (auto, gitignored
  build cache), 1 rationale (anchored entries), 2 synthesis
  (per-file digests + concept pages), 3 query (directory layout,
  not a CLI).
- Layer 3 collapsed from a CLI (`imp where/ref/digest/page`) into
  a pre-rendered `imp/_index/` directory. Reads use the agent's
  existing `Read`/`Glob`/`Grep`. Cline's "no index" stance applied
  to substrate.
- Authoring inversion: humans/agents capture into `imp note`;
  the gnome generates layer 1 entries overnight. Direct authoring
  is reserved for `rules/`.
- Surviving CLI surface: `imp note`, `imp tidy`, `imp init`.
  Reads are filesystem-only.

## [2026-05-10] rename | project/ → imp/

Substrate dir renamed to make tool ownership explicit by name.
`plans/`, `bugs/`, `TODO.md`, and `rules/` lifted to repo root as
human-owned dirs. Inside `imp/`: gnome-maintained content
(`log.md`, `note/`, `learnings/`, `reference/`, `concepts/`,
`_index/`) plus `_meta/` for substrate conventions.

Dropped: `aspirations/` (folds into CLAUDE.md), `tasks/` (TODO.md
covers it).

Open question: whether `<repo>.project-proposals/` follows to
`<repo>.imp-proposals/` — affects the `project-promote` skill and
existing docs.

## [2026-05-10] init | scaffolding rewritten for new layout

`imp init` now produces the new layered substrate by default. Default
location: `imp/` (was `project/`). Templates restructured:

- Added: `note/{inbox,processed,discarded}/`,
  `_index/{by-file,by-symbol,by-feature}/`.
- Removed: `aspirations/`, `tasks/`, `plans/active|archive/`, `rules/`
  (the last three lifted to repo root, `aspirations`/`tasks` dropped
  outright).
- Rewrote: `_meta/conventions.md` (kinds taxonomy, trust model,
  drift semantics, ~200→160 lines), top-level `README.md`, `log.md`
  init message, kind-folder READMEs (concepts/learnings/reference)
  for the new authoring story (gnome generates from notes).

`imp init` now also scaffolds root-level human-owned dirs (`plans/`,
`bugs/`, `rules/`) with `.gitkeep`s and creates `TODO.md` if missing.
Never overwrites existing root content. Adds `.imp/` (layer-0 cache)
to `.gitignore` alongside the proposals path.

Smoke test green: fresh `git init` + `imp init` produces complete
new-layout tree; re-run is a no-op without `--force`; `imp note`
lands captures correctly in `imp/note/inbox/`.

## [2026-05-10] design | trust model partitioned by directory ownership

Pre-rename: every imp write went through proposals (humans approved
each one). That made sense when imp shared `project/` with
human-authored content (rules, aspirations, learnings). Post-rename
(`project/` → `imp/`, with `rules/`/`plans/`/`bugs/`/`TODO.md` lifted
to root), imp's own dir is fully imp territory; the proposal gate on
imp's writes there is needless paperwork.

New model: imp writes directly to `imp/*` under a distinct git author
(`imp-gnome <noreply@imp.local>`, set per-commit so no GH account
needed). Auditability via `git log -- imp/`. Proposals only fire for
cross-boundary writes — `rules/`, `plans/`, `bugs/`, `TODO.md`.

Code stays out of scope; build mode has its own trust pipeline.

## [2026-05-10] rename | <repo>.project-proposals → <repo>.imp-proposals
                  | /project-promote → /imp-promote

Follows the `project/` → `imp/` rename. Proposals dir and the skill
that applies them now match the substrate dir name. Skill content
narrowed to reflect the new trust model: anything destined for
`imp/*` is out-of-scope (refused), tier table simplified, atomicity
precondition checks `rules/ plans/ bugs/ TODO.md` cleanliness only
(not `imp/`, since imp may have uncommitted gnome work in flight).

Skill filename: `skills/project-promote.md` → `skills/imp-promote.md`.
Spec doc filenames in `project/` left alone (`project-promote-spec.md`,
`project-migrate-spec.md`) — internal references updated.

## [2026-05-10] rename | stash → note (CLI verb)

`imp stash` renamed to `imp note` to avoid conversational and
muscle-memory collisions with `git stash`. The dominant capture
verb (90% case: agent calls `imp note "<text>"` mid-conversation)
needed a name unambiguous from "stash these changes." Output
phrasing followed: `noted <id>: <echo>`. Inbox path is now
`imp/note/inbox/`. nb's substrate dirs renamed to match.

## [2026-05-10] research | code intelligence prior art surveyed

Surveyed SCIP/LSIF/Kythe/Glean (symbol graphs), Aider/Continue
(repo-map), Cursor/Cody/Tabby (embeddings), DeepWiki/Greptile
(LLM synthesis), Doxygen (human rationale), Cline (no index).
Key takeaways: stable symbol IDs as join keys (SCIP), file-
incremental indexing (stack-graphs), token-budgeted PageRank
skeletons (Aider) as cold-start primitive. The gap nobody fills
well — *persisted, human-curated rationale anchored to code* —
is exactly where imp's substrate wins. DeepWiki is the cautionary
tale: auto-fabricated rationale goes stale and lacks human voice.

<!-- migrated from project/log.md by project-migrate-skill:M-2026-05-10-1855 on 2026-05-10 -->
```

## Notes for the reviewer

- The append target is `imp/log.md` and the position is
  "after the `---` separator, before the existing init entry." The
  apply step in `/imp-promote` may need to handle that ordering
  manually since `append` typically adds at end-of-file; if the
  promote tool doesn't support `position: after-header`, fall back
  to a manual edit at apply time.
- No `Preview` entries were lightly edited — the legacy content
  is preserved verbatim. The trailing HTML comment is the only
  addition, marking provenance inline (the `imp/log.md` file
  itself doesn't have frontmatter, so the provenance marker can't
  go there).
- After apply, the legacy `project/log.md` keeps its content but
  gains frontmatter pointing readers to the merged location.

## [2026-05-10] migration | M-2026-05-10-1855 complete

Migrated 19 docs from project/ via /project-migrate. Outputs:
P-2026-05-10-001 through P-2026-05-10-019.

Result: 12 plans (5 shipped, 4 exploring, 1 active, 2
special-shape: todo-merge + log-merge), 3 references, 1
learning, 2 drops. Zero polish traps flagged. Zero deferrals.

Drops: project/project-migrate-spec.md (superseded by
plans/project-migrate-phase{1,2}.md + skills/project-migrate.md);
project/wiki-plan.md (superseded by plans/wiki-deprecation.md).

Cleanup + audit: P-2026-05-10-020.

## [2026-05-13] tidy | 3 note(s): 1 new entry(s), 1 updated, 1 proposal(s), 0 discarded, 0 failed

Processed inbox: 1 learning(s), 0 reference(s), 1 proposal(s), 0 discarded, 0 failed.

## [2026-05-18] tidy | 1 note(s): 0 new entry(s), 0 updated, 1 proposal(s), 0 discarded, 0 failed

Processed inbox: 0 learning(s), 0 reference(s), 1 proposal(s), 0 discarded, 0 failed.
