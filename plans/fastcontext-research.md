---
kind: plan
title: Adopt fastcontext retrieval discipline in imp research
state: active
created: 2026-06-22
updated: 2026-06-22
provenance:
  author: human
touches:
  files:
    - Prompts/research-fs.md
    - Research/ResearchExecutor.cs
    - Research/ResearchTools.cs
    - Research/ResearchReport.cs
  features:
    - imp research (retrieval phase)
---

# Adopt fastcontext retrieval discipline in imp research

## Background

Microsoft published **FastContext** (paper + 4B/30B model weights) on
2026-06-15: a bounded, read-only retrieval subagent that coding agents
delegate to so broad searching never touches the solver's context
window. Reported wins: up to +5.5% end-to-end resolution and up to 60%
fewer main-agent tokens on SWE-bench Multilingual / Pro / SWE-QA.

Sources:
- https://github.com/microsoft/fastcontext
- https://arxiv.org/abs/2606.14066
- https://huggingface.co/microsoft/FastContext-1.0-4B-RL

**Key finding from reviewing both:** imp research is *already*
fastcontext-shaped. We converged on the same architecture independently.

| fastcontext | imp research (today) |
|---|---|
| Read-only triad `Read`/`Grep`/`Glob` | `read_file`/`grep`/`list_dir` |
| Bounded agentic loop, turn cap ~6–8 | `ResearchExecutor` loop, 60 tool-call budget |
| Returns compact `<final_answer>` citations | Returns `report.json`/`findings.jsonl`, file:line citations |
| **Explorer trajectory → separate log; only citations reach main agent** | **trace.jsonl/transcript.md stay in archive; parent sees only the report** |

The paper's headline 60% token reduction comes *entirely* from
trajectory isolation — the explorer's greps/reads never enter the
solver's history. imp gets that for free by running research as a
separate process that returns only the structured report. **The single
most valuable idea in the paper is already shipped.**

That reframes the work. This is not "port fastcontext into research."
It's "borrow fastcontext's retrieval *discipline* to sharpen research's
gathering phase." Research is a superset — fastcontext returns code
regions; research returns regions *plus* synthesized findings with
reasoning/confidence. We keep the synthesis; we tighten the retrieval.

The portable deltas are mostly prompt-level. Implement in parts; each
phase ships and is observed independently. None depends on the next.

## Phase 1 — First-turn broad parallel fan-out

The highest-leverage, lowest-cost idea. fastcontext's first turn issues
several *non-redundant parallel* tool calls covering complementary
signals (path patterns, symbols, entry points), then narrows. imp's
prompt today reads as sequential ("orient with `list_dir` then `grep`").

The harness already supports this: `ResearchExecutor.RunAsync`
(Research/ResearchExecutor.cs:90–212) harvests *all* `FunctionCallContent`
from a single model response and invokes each. Parallel tool calls in
one turn already work — only the prompt discourages them.

**Change:** edit `Prompts/research-fs.md`. Instruct: on turn one, issue
multiple parallel searches covering complementary angles
(path globs, symbol greps, likely entry points) *before* reading any
file; start broad, narrow on evidence. Add a short worked example of a
good first-turn fan-out.

**Files:** `Prompts/research-fs.md`.

**Acceptance:** run 3–5 representative research questions against this
repo; inspect `trace.jsonl`. First turn should contain ≥2 parallel tool
calls. Compare tool-call count and wall-time to baseline traces. Expect
fewer turns to a working answer.

**Cost/risk:** ~free. Pure prompt change, no infra. Risk is a weak
executor over-fanning on turn one and wasting budget — mitigated by the
existing 60-call cap and Phase 2's stop directive.

## Phase 2 — Budget-proximity stop directive

fastcontext appends "stop exploring and return the best-supported
answer" to the system prompt on the *final* turn. imp's equivalent is a
one-shot nudge ("Call finish_research now") that fires only when the
model *stops calling tools on its own* (Research/ResearchExecutor.cs:164–166)
— the wrong trigger. A model that keeps greping toward the budget wall
never sees it and gets cut off mid-stride with `BlockedCategory.Abandon`.

**Change:** in the executor loop, when remaining budget drops below a
threshold (e.g. ≤2 effective turns, or ~85% of `toolBudget` consumed),
inject a system/user directive: "You are near your budget. Stop
searching and call `finish_research` with your best-supported findings
now." Keep the existing no-tool-call nudge as well.

**Files:** `Research/ResearchExecutor.cs` (loop body near the budget
check and the existing nudge).

**Acceptance:** a question engineered to exhaust budget should end via
`finish_research` (`state.Captured` set) rather than
`TerminalState.Blocked` / `Abandon`. Check that the directive fires once
and is logged to the trace.

**Cost/risk:** small, localized. Risk: firing too early truncates
genuinely-needed exploration — tune the threshold from observed traces,
start conservative (~85%).

## Phase 3 — Output-compactness caps

fastcontext's validation actively *penalizes* bloat: rejects >20
citations, full-code dumps, malformed paths. imp's finish tool
(Research/ResearchTools.cs:112–152) validates citation *presence*
(≥1 finding, ≥1 citation each, ≥1 excerpt each) but not *count* or
excerpt *length* — nothing stops the model returning a sprawling report
that re-bloats the parent's context, defeating the whole point.

**Change:** add soft caps to finish-tool validation and/or the prompt:
- cap total citations (e.g. ≤20–30) — reject or trim with a note
- cap per-excerpt length (line-range excerpts, not whole-file dumps)
- prefer rejection-with-feedback so the model retries compactly, since
  the loop already supports nudges

**Files:** `Research/ResearchTools.cs` (validation), possibly
`Research/ResearchReport.cs` (if caps belong on the record), and a line
in `Prompts/research-fs.md` setting expectations up front.

**Acceptance:** a report exceeding the caps is rejected with actionable
feedback and the model produces a compact retry. Typical `report.json`
size trends down without losing load-bearing findings.

**Cost/risk:** small. Risk: over-aggressive caps drop real findings —
make caps soft (warn/trim) before hard (reject), tune from real reports.

## Phase 4 (deferred / largely declined) — Custom retrieval model

fastcontext's actual novelty is a *trained* 4B–30B explorer, fast and
cheap at retrieve-and-cite. Weights are public
(`microsoft/FastContext-1.0-4B-SFT`, `-4B-RL`). The home Strix Halo box
could serve one as a research-only profile, and unlike qwen-as-builder
(which hallucinated imp's API shape — see
`memory/project_qwen_executor.md`) the task here is retrieval, not
editing, which is exactly what the model is RL-trained for.

**Decision: deferred, low priority.** The marginal value is modest and
the economics don't favor it:

- **At home:** Claude Code is quasi-free, and there's active ambivalence
  about Anthropic burning tokens on low-value research runs. A local 4B
  explorer would *reduce* that token spend — but the spend is already
  cheap-to-free, so the saving is small and the integration cost
  (serving profile, contract mapping, eval) is real.
- **At work:** custom models can't run at all; research there must stay
  backed by Azure OpenAI regardless. So a local model can never be the
  primary path — at best a home-only optimization behind a provider
  switch, doubling the surface to maintain.
- **Contract mismatch:** the model is trained for fastcontext's exact
  citation format, not imp's richer `Finding` shape (claim + reasoning +
  confidence). It would slot into the *gathering* sub-phase only, with a
  stronger model doing synthesis — more moving parts for a small win.

**What would change the calculus:** research volume growing enough that
token cost (even quasi-free) becomes a real annoyance; or a desire to
run research fully offline/air-gapped; or the gathering phase proving to
be the dominant cost in traces. Revisit then. For now Phases 1–3 capture
nearly all the value at nearly none of the cost.

## As built (2026-06-22)

Phases 1–3 implemented in one sitting; build clean, no tests yet (the
test project is still `plans/unit-tests.md`). End-to-end validation
against Azure is pending — needs real research runs to tune thresholds.

- **Phase 1** — `Prompts/research-fs.md`: added a "you may call multiple
  tools per turn, batch them" note to the Tools section, and rewrote
  step 1 of "How to research" into "open with a broad parallel sweep"
  (list_dir root + parallel greps on complementary signals before
  reading). Step 2 is now the narrowing step.
- **Phase 2** — `Research/ResearchExecutor.cs`: `BudgetWarnFraction =
  0.85` const + `budgetWarned` flag. Once tool calls cross 85% of
  budget, injects a one-time "stop and call finish_research" directive.
  Complements (doesn't replace) the existing no-tool-call nudge.
- **Phase 3** — `Research/ResearchTools.cs`: `NormalizeOutput` runs
  before `Validate` at capture. **Deviation from plan:** the citation
  cap is a *soft per-finding trim* (`MaxCitationsPerFinding = 6`, keep
  first), not a hard total-count reject. Rejecting a valid-but-bloated
  report near budget risked a retry that times out into *no* report —
  strictly worse than a slightly-long one. Excerpts over
  `MaxExcerptChars = 1200` (~15-20 lines) are truncated with a marker.
  Both caps are deterministic and never empty a required field, so the
  report always lands. The total-count cap from the plan was dropped:
  per-finding trim + findings being self-limiting bounds size without
  the fiddly "keep ≥1 per finding" logic a total cap needs.

**Open tuning questions for first real runs:** is 85% the right warn
point (vs leaving too little room to actually write the report)? Is 6
citations/finding ever too tight? Does the parallel-sweep instruction
actually change first-turn behavior on codex-mini, or does it ignore it?
Read `trace.jsonl` from a few runs before adjusting constants.

## First traces (2026-07-01)

Ran five representative fs-mode questions against **codex-mini**
(`--provider AzureFoundry --archive`), archived at
`imp.researches/R-017`–`R-021`. All five succeeded; ~$0.23, ~7 min
total. Consumer eval by Opus (the real client) plus a repo-less Sonnet
sub-agent fed only the reports.

**Phase 1 — validated, working.** The plan's headline open question
("does codex-mini honor the parallel-sweep instruction, or ignore
it?") is answered **yes, every time**. Turn one of all five runs was a
broad parallel sweep (`list_dir` + N greps): fan-out of 4 / 4 / 8 / 5 /
5 calls. One run also batched an 8-way parallel `read_file`. Highest-
leverage change, confirmed cheap and effective.

**Phase 2 — shipped but not exercised.** The 85% directive fires at 51
of 60 calls. The busiest run used **23**; even the deliberately-broad
mode-inventory question hit only 17. codex-mini terminates far more
efficiently than the budget assumes, so `BudgetWarnFraction = 0.85`
remains **unvalidated** — no run approached it. To test it, need a
genuinely sprawling question or a temporarily lowered fraction.

**Phase 3 — shipped but not exercised.** Reports came in naturally
compact (max 3 citations/finding, longest excerpt 279 chars; caps are 6
and ~1200). No trim or truncation marker fired anywhere. The caps are
correct-by-construction but **unproven** on real output for the same
reason as Phase 2.

**Correctness — 4/5, one instructive miss.** Q1 (provider precedence),
Q2 (`--archive`), Q4 (safety gates), Q5 (MCP→CLI rewrite) were all
correct, well-cited, appropriately confident. Notably Q2 was the exact
question that made qwen hallucinate the flag out of existence
(`TODO.md` "research mode bias toward substrate over source") — on
codex-mini it read `Program.cs` and answered correctly, so that
regression did **not** reproduce here.

The miss was Q3 ("inventory every research mode"): the model read only
`plans/research-mode.md` (a design doc) and reported "imp ships **web +
fs**, two modes" at `high` confidence. Ground truth from `Modes.cs`:
**five** modes registered — `fs`, `wiki`, `review-bug`,
`review-simplify-comments`, `review-untested` — and **no web mode**
(web exists only as an enum comment). This is the same
substrate/doc-over-source foot-gun, in a new costume: not a
substrate-vs-source *scoping* error but a design-doc-as-shipped-fact
error. Phase 1's fan-out did not prevent it. The tell was in the report
itself: `coverage.not_explored` listed `Research/Modes.cs` — it knew it
hadn't read the source of truth and finished `high` anyway.

**Consumer safety (Sonnet, repo-less).** Given only the five reports,
Sonnet independently flagged Q3 as most likely to mislead, citing the
exact signal — every finding pointed at a plan doc, `Modes.cs`
unexplored, "the report contradicting its own confidence rating." So
the coverage/citation block *is* the safety rail that lets a careful
downstream reader distrust a confidently-wrong synthesis. Two caveats:
(1) the `synthesis` field itself carries no hedge, so a reader who skims
only the synthesis is still misled; (2) Sonnet under-trusted the
*correct* reports too (single-citation answers read as risky regardless
of whether the citation was code or doc) — the discriminating signal
lives in the citation *path*, not the confidence field.

**Follow-on shipped (this commit):** source-of-truth discipline added
to `Prompts/research-fs.md` — a "answer code questions from code" step
in *How to research* and a Confidence rule capping doc-only claims about
code behavior at `medium`. Directly targets the Q3 failure mode;
composes with the citation-path signal the consumer already reads.

**Q3 re-run — validated (R-023).** Same question, same setup
(codex-mini via `--provider AzureFoundry --archive`), 21 tool calls,
~$0.14. The nudge changed behavior exactly as intended:

- **Read `Research/Modes.cs` this time** — moved from `not_explored`
  (R-019) into `explored`. The "answer code questions from code" step
  drove it to the source of truth.
- **Correct synthesis:** "five built-in research modes — fs, wiki,
  review-bug, review-simplify-comments, review-untested," cited to
  `Modes.cs` at `high`. The R-019 "web + fs, two modes" error did not
  reproduce.
- **Doc claim properly demoted, not dropped:** the plan-doc "web + fs"
  statement survives as a *separate* finding at `medium` (the exact
  cap the new Confidence rule mandates), framed as "The design plan
  *specifies*" — intent, not shipped fact.
- **Bonus:** it opened a `conflicts` entry reconciling doc-vs-code and
  resolved that `Modes.cs` is authoritative for shipped modes. The
  source-of-truth framing gave it the vocabulary to name the drift
  rather than pick a side blindly.

**Still open:** Phases 2 and 3 need one budget-stressing run (or a
lowered fraction) to validate their constants — the first-trace set was
too easy to trigger either. R-023 used 21 of 60 calls, same story.

## Budget-stressing run (2026-07-01, R-024)

Model swapped to **GLM-4.5-Air** (Q6_K, llama.cpp on the Strix box) via
a new local `GLM` provider (`Infrastructure/Providers.cs` reuses the
Qwen OpenAI-compatible client shape). To make the phase-2 warn reachable
mid-exploration — the plan's documented "lowered fraction" fallback,
since real questions converge well under 60 — `Research:ToolBudget` was
temporarily set to **20** (warn at 17). Question was a deliberately
sprawling "catalog every CLI subcommand with per-command flags and
citations."

**Phase 2 — validated.** Reconstructing the trace turn-by-turn: tool
calls arrive in per-turn batches (fan-out), and the warn check runs only
at turn boundaries. At turn 8's start the cumulative count hit exactly
**17 (85% of 20)** → the directive injected → the model's next and only
action was `finish_research`, terminating `success`. Its
`blocked_questions` entry consciously records stopping short ("remaining
20 subcommands not yet analyzed") — a partial-but-cited report, exactly
the intended behavior instead of grinding to the hard cap and
`Abandon`. The mechanism works.

**Observability gap (new finding).** The warn directive is only
`history.Add`-ed (ResearchExecutor.cs:113) — it is written to **neither
`trace.jsonl` nor `transcript.md`**. Both artifacts are turn/tool-call
oriented and drop injected steering messages (the no-tool nudge has the
same problem). So from the archive alone you *cannot see* that the warn
fired — I had to infer it from the call-count crossing the threshold.
This directly undercuts the plan's own goal of "tune the threshold from
observed traces." **Fixed:** `TraceWriter.WriteDirective` emits a
`directive` event (`kind: "budget_warn"`, with `tool_calls` /
`tool_budget` / `after_turn`) to `trace.jsonl` when the warn injects.
Confirmed in R-025 (codex-mini, budget 20): the event lands at
`tool_calls: 17`. The warn is now directly observable for threshold
tuning. The no-tool `nudge` shares the gap and can reuse the same
method — left for when it next matters.

**Phase 3 — still not exercised, and likely won't be on real output.**
GLM produced 1 citation per finding with short excerpts (R-023's max was
3/finding, 279 chars). The per-finding citation cap (`MaxCitationsPerFinding
= 6`) only trips when a *single claim* needs >6 distinct-location
citations — an unusual shape; models treat a contiguous code block as
one citation (R-024 cited the entire 29-line dispatch switch as a single
excerpt). The excerpt-length cap (`MaxExcerptChars = 1200`) similarly
never approached. Three real runs now (R-023, R-024, plus the first-trace
set) show output stays comfortably under both. **The caps are insurance
against a failure mode that hasn't occurred.** The right validation for a
deterministic string trim is a unit test over `NormalizeOutput`, not
coaxing a model into bloat — folds into `plans/unit-tests.md`.

## Implementation order

1. **Phase 1** — ship first, observe traces. Highest leverage, ~free.
2. **Phase 2** — pairs naturally with Phase 1 (fan-out raises call
   throughput, so a clean stop matters more).
3. **Phase 3** — once 1–2 are stable and we can see real report sizes.
4. **Phase 4** — deferred; revisit only if the calculus above shifts.

Phases are independent — each is a standalone ship. No big-bang change.
