---
kind: plan
title: Builder agent autonomy (looser contracts + research sub-agent)
state: exploring
created: 2026-05-29
updated: 2026-05-29
touches:
  features:
    - Templates/contract.md (template loosening)
    - skills/imp.md (authoring guidance update)
    - Tools/Tools.cs (new substrate_read + research_subagent tools)
    - Build/Executor.cs (sub-agent dispatch)
    - Prompts/ (new sub-agent system prompt)
---

# Builder agent autonomy

Shift contract authoring effort from Opus to the builder. Today Opus
burns large token budgets researching and writing exhaustive
contracts, which both undermines the cost case for delegation and
reduces the builder (codex-mini) to a pseudocode-to-code transpiler.
This plan loosens the contract surface and gives the builder its own
research capability, while keeping imp's enforcement layer (Scope
existence-check, Acceptance + closeout reviewer) intact.

Background research summarized from a 2026-05 field survey
(Aider architect/editor, Claude Code subagents, Cline plan/act,
Codex CLI subagents, Devin 2.0, Kiro spec-driven, SWE-agent,
OpenHands, Anthropic multi-agent). imp sits at the strict end of
the spectrum, most comparable to Kiro. Aider and Codex CLI both
trust the executor to derive signatures and file lists from prose
context. The strictness in imp is partly load-bearing (the executor
is deliberately cheap and weak) and partly waste (the Contract +
Context sections encode work the executor could do itself if it
had the tools to do it).

## Goals

- Cut Opus tokens spent authoring contracts by ~30–50% on
  refactor-shaped tasks (no hard target; measure on a few real
  contracts before/after).
- Let the builder do its own orientation against the substrate and
  worktree instead of relying on hand-curated Context bullets.
- Preserve the imp review cardinal rule: parent reviews the
  closeout bundle, not the worktree. Acceptance bullets and the
  closeout reviewer stay strict.
- Keep the pre-flight Scope existence-check.

## Non-goals

- Giving the builder network access. The research sub-agent is the
  only widening of the builder's effective reach, and it remains
  read-only and substrate-scoped (no web).
- Changing the executor model or sandbox model.
- Rewriting Build/Contract.cs parsing. The validator only enforces
  Goal/Scope/Acceptance today; loosening template sections costs
  nothing at the parse layer.

## Two-track approach

### Track A — template + prompt loosening (cheap, immediate)

1. **Rename `Contract:` to `Constraints:` and make it optional.**
   Drop the "exported signatures / behaviors / purity" prescription.
   Reframe as banned operations, invariants, sandbox or performance
   bounds — information the executor cannot infer. Aider's
   architect/editor pair demonstrates that signatures don't need
   to be spec'd if the executor model is competent at derivation.
   codex-mini is competent enough for that on most in-repo work
   (the known weak spot is unfamiliar API shape, which is what
   Track B addresses).

2. **Soften `Context:` to "starting points."** Currently
   `path — why it matters` bullets. Reframe as 1–3 entry pointers
   plus an explicit instruction to grep/list_dir/substrate_read
   from there. The validator doesn't enforce Context, so this is
   pure template/prompt work.

3. **Allow glob entries in `Scope:`.** Add `edit-within:
   src/Build/*.cs` form. Validator existence-checks the parent
   directory; closeout reviewer verifies actual edits stayed
   inside the glob. Removes the modal annoyance of enumerating
   files for coherent-subdir refactors.

4. **Update `skills/imp.md`** to reflect the looser template and
   the new sub-agent. The skill doc already preaches "trust the
   executor" — the template currently contradicts that, and after
   Track A it won't.

Cost: small. Files: `Templates/contract.md`, `skills/imp.md`. No
code changes.

### Track B — read-only research sub-agent

Give the builder two new tools so it can orient itself without
exploding its own context.

1. **`substrate_read`** — a read-only tool that exposes
   `imp/_index/by-file/<path>.md`, `imp/concepts/*.md`,
   `imp/learnings/*.md`, and `imp/reference/*.md`. This is the
   gnome-maintained orientation surface that Claude Code consumes
   from CLAUDE.md but the builder currently has no access to.
   Cheap, in-process, no sub-agent needed — just a file read with
   a path allowlist.

2. **`research_subagent`** — dispatches a fresh executor turn
   (same model or a different mode-configured one) with a tight
   prompt, the read-only tool subset (`read_file`, `grep`,
   `list_dir`, `substrate_read`), no `bash`/`write_file`/
   `apply_patch`, and a small token budget. Returns a structured
   summary (findings + file pointers), not a transcript. The
   builder can spawn one to answer questions like "which call
   sites of `Foo.Bar` exist and what shape do they expect" without
   loading that exploration into its own context window.

   This intentionally mirrors `Tools.CreateReadOnly` for closeout
   review — same toolset, different caller.

The split between the two: `substrate_read` is for cheap lookups
the builder does directly. `research_subagent` is for open-ended
questions where the builder wants synthesis, not raw file content.
Same distinction as Claude Code's Read vs. Explore agent.

Cost: medium. Files: `Tools/Tools.cs`, `Build/Executor.cs` (to
dispatch the sub-agent), new prompt under `Prompts/`,
`Tools/ToolRegistry.cs` to register `substrate_read` for research
mode too.

## Security note — lethal trifecta

The lethal trifecta (Willison): an agent with **(1) access to
untrusted input**, **(2) access to sensitive data or write
capability**, and **(3) ability to externally communicate** is
exploitable via prompt injection.

The builder today has (2) — it can write to the worktree and run
arbitrary bash. It does not have (1) or (3). Adding a research
sub-agent must not silently add (1).

Risks once the sub-agent exists:

- If the sub-agent ever gains web access (it must not in this
  plan), attacker-controlled web content becomes (1). The builder
  consuming the sub-agent's summary then has all three legs.
- Even web-less, the sub-agent reads `imp/reference/` which
  contains archived external sources. Anything quoted verbatim
  from `imp/reference/` is effectively untrusted text the gnome
  may have already laundered through summarization, but
  injection-bearing strings could still survive. Treat
  `imp/reference/` content surfaced through the sub-agent as
  partially untrusted.
- The sub-agent's structured output is itself a prompt-injection
  surface. A clever attacker who got text into the substrate
  could craft a "finding" that tells the builder to exfiltrate
  via bash. The builder still has bash + worktree write — the
  trifecta only needs the input leg.

Mitigations baked into this plan:

- Sub-agent has **no `bash`**, **no `write_file`**, **no network
  tool**, **no `apply_patch`**. Read-only.
- Sub-agent **output is constrained to a fixed JSON shape**
  (findings: list of {claim, evidence_paths}). No free-form
  prose passthrough to the builder. Anything outside the schema
  is dropped.
- Sub-agent prompt **explicitly frames retrieved content as
  data, not instructions**, and instructs the sub-agent to
  ignore directives found in read content. This is defense in
  depth; the schema constraint is the real moat.
- Sub-agent gets a **separate, sandbox-respecting bash-free
  toolbox** built via `Toolbox.CreateReadOnly`-style factory.
  Do not pass the main builder's toolbox through.
- **No web tool, ever, in this plan.** If a future plan wants
  web research for the builder, it must address the trifecta
  head-on (separate process, output laundering, human review
  step) — not bolt it onto this sub-agent.

The deliberate posture: the sub-agent widens the builder's
*orientation* surface, not its *capability* surface. Sub-agent
sees more files; builder sees more synthesis; nobody gains the
ability to reach external systems.

## Open questions

- Should the sub-agent share `ExecutorState` (token budget, todo
  list) with the builder, or get its own? Probably its own, with
  a hard cap on tokens consumed per dispatch so a runaway
  sub-agent can't drain the contract's budget.
- What model runs the sub-agent? Same codex-mini is the cheap
  default. A research-mode-configured model (e.g. qwen for
  read-only orientation) might be a better fit and aligns with
  the existing per-mode model config direction.
- How does the closeout reviewer see sub-agent activity? At
  minimum, sub-agent dispatches should appear in `trace.jsonl` so
  review can spot a builder leaning too hard on synthesis instead
  of doing the work.

## Sequencing

Track A is independent and ships first — it's a template edit and
a skills doc update, measurable against a couple of real
contracts. Track B follows; `substrate_read` lands before
`research_subagent` since it's the cheaper half and partially
obviates the more complex one. Re-evaluate whether the sub-agent
is still needed once `substrate_read` is in builder hands.
