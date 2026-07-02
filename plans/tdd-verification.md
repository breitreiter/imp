---
kind: plan
title: TDD-style verification — executable tests as the build acceptance gate
state: exploring
created: 2026-06-18
touches:
  files:
    - Build/Contract.cs
    - Build/Executor.cs
    - Build/LifecycleCommands.cs
    - Build/BuildResult.cs
    - Infrastructure/SandboxConfig.cs
    - Prompts/default.md
    - Templates/contract.md
    - imp/_meta/config.yaml
  features: [build, verification, tdd, acceptance, skill]
provenance:
  author: human
---

# TDD-style verification

## Problem

Imp currently has no objective build or test signal. The executor
self-reports whether tests passed — in the `tests.existing_passed`
field and acceptance bullet verdicts — but it can hallucinate or
misread output. The closeout reviewer is intentionally read-only: it
checks acceptance bullets by code inspection, not by running anything.
The result is that a `terminal_state: success` build may silently
break the project.

The bare minimum bar is: **build passes, unit tests pass** after every
build, verified by imp itself, not by the executor's self-report.

The higher bar — and the goal of this plan — is TDD: the contract
author hands imp pre-written failing tests, and imp's job is to make
them pass. The executor gets real, objective feedback on each iteration
instead of relying on self-judgment.

## Current flow (what happens today)

```
executor runs → self-reports → closeout reviewer reads code → proof-of-work
```

No shell execution in the closeout path. `TestsReport.ExistingPassed`
is filled by the executor saying "I ran tests and they passed," not by
imp running them.

## Target flow

```
executor runs → [verification step] → closeout reviewer reads code → proof-of-work
                       ↓
               runs verify commands
               captures exit codes + output
               demotes to Failure on non-zero
               populates TestsReport objectively
```

The executor also runs the verification commands *during* its loop,
using them as a steering signal — this is the TDD feedback loop. When
a verification command fails midway, the executor knows to keep
iterating. When all pass, it's confident the work is done.

## Design

### 1. Project-level verify config

`imp/_meta/config.yaml` gains a `verify` block:

```yaml
verify:
  commands:
    - dotnet build
    - dotnet test
  timeout_seconds: 120   # optional, default 120
```

These are project defaults — run after every build regardless of the
contract. The minimum useful config is a build command + a test
runner invocation. Projects that have no tests can omit `verify`
entirely (no gate is applied).

The config is loaded in `Build/LifecycleCommands.cs` (or a new thin
`ProjectConfig` helper under `Infrastructure/`). The load path
searches `<cwd>/imp/_meta/config.yaml`, the same location imp already
reads for substrate config.

Sandbox note: verification commands run in the worktree, under the
same sandbox constraints as the executor. The executor's `bash` tool
already allows this scope. No sandbox policy changes needed.

### 2. Contract `**Verification:**` section

The contract template gains an optional `**Verification:**` section
listing per-contract shell commands:

```markdown
**Verification:**
- dotnet test --filter "FullyQualifiedName~T042"
- dotnet build --project MyLib/MyLib.csproj
```

These express the specific acceptance criteria as runnable commands.
The intent is red-green TDD: the author writes a failing test (often
as a `create:` scope entry), lists it here, and imp's job is to make
it pass.

**Contract-level commands supplement, not replace, the project-level
config.** Both run at closeout; all must exit 0 for the build to
succeed.

`ContractParser` extracts these into `IReadOnlyList<string>
VerifyCommands`. `ContractValidator` does a weak sanity check
(non-empty strings, no path traversal). The validator does *not*
pre-run them — that happens at closeout.

### 3. Verification step in the build loop

After the executor closes out, before the closeout reviewer runs, a
new deterministic (non-LLM) verification step executes:

1. Collect commands: project-level verify config ++ contract
   `Verification:` commands.
2. Run each sequentially in the worktree. Capture stdout+stderr
   (truncated to ~4KB per command for the proof-of-work).
3. If any command exits non-zero: set `TerminalState` to `Failure`,
   write the first failed command + its output into `Notes` so the
   parent can diagnose without diving into the trace.
4. Populate `TestsReport` from the actual run results, not the
   executor's self-report:
   - `ExistingPassed`: true only if all project-level verify
     commands exited 0.
   - New field `VerifyResults`: list of `{ command, exit_code,
     output_snippet }` for the parent to read.

The closeout reviewer still runs (read-only). The terminal state
downgrade on failed verification happens before the reviewer, so the
reviewer's verdict is framed against a known-failing build.

### 4. Executor steering

The executor's system prompt (`Prompts/default.md`) gains an explicit
section injected when the contract has `VerifyCommands`:

```
**Verification commands** (these must all exit 0 when you're done):
{{VERIFY_COMMANDS}}

Run these periodically as you work. A failing command tells you the
work isn't done yet. A passing command after changes tells you that
sub-goal is met. Don't stop until all pass (or you genuinely can't
proceed and must block).
```

This is the TDD feedback loop: the executor doesn't have to guess
whether its changes are correct. It runs the commands, reads the
output, and steers.

The `Prompts.LoadSystemPrompt` path already receives the `Contract`
— add a `VerifyCommands` block when the list is non-empty.

### 5. BuildResult changes

`TestsReport` grows a `VerifyResults` field:

```csharp
public record VerifyCommandResult(
    string Command,
    int ExitCode,
    string OutputSnippet);  // first ~2KB of stdout+stderr

public record TestsReport(
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Modified,
    bool? ExistingPassed,
    IReadOnlyList<VerifyCommandResult> VerifyResults);  // new
```

`VerifyResults` is empty list (not null) when no verify commands are
configured, to avoid null-check noise in the parent.

## What changes

| File | Change |
|---|---|
| `Build/Contract.cs` | Add `VerifyCommands` to `Contract` record |
| `Build/Contract.cs` (parser) | Parse `**Verification:**` bullets |
| `Build/Contract.cs` (validator) | Weak validation of command strings |
| `Build/BuildResult.cs` | Add `VerifyCommandResult`, extend `TestsReport` |
| `Build/LifecycleCommands.cs` | Load project config; run verification step; demote on failure |
| `Prompts/default.md` | Inject verify commands block when present |
| `Templates/contract.md` | Add optional `**Verification:**` section with a comment |
| `imp/_meta/config.yaml` | Document `verify.commands` schema (this repo's own config) |
| `skills/imp.md` | TDD authoring pattern, updated quick-ref, `tests.verify_results` in proof-of-work section |

New file (optional): `Infrastructure/ProjectConfig.cs` — thin loader
for `imp/_meta/config.yaml`. If the struct stays simple (just a list
of commands) it can live inline in `LifecycleCommands.cs` instead.

## What this doesn't do

- **Test framework detection.** No heuristics. The author (or
  `imp init`) writes the verify commands explicitly. Heuristics are
  noisy across polyglot projects and the cost of a wrong inference
  is a phantom gate.
- **Pre-flight failure capture.** Running verify commands *before*
  the executor starts (to establish a "baseline failure" for the
  red-green pattern) is appealing but not essential for v1. The
  executor's first run of the commands will surface this naturally.
  Add if the baseline/regression distinction turns out to matter in
  practice.
- **Test writing.** Imp makes *existing* tests pass. Writing new
  tests is the contract author's job (or a future `imp scout` pass).
- **Timeout per framework.** One global `timeout_seconds` in config
  is enough for now.
- **Parallel execution.** Commands run sequentially. Simplicity >
  speed in the verification step.

## Phasing

These are three independently shippable pieces in order of value:

1. **Project verify config + verification step** (phases 1 & 3 above)
   — objective build/test gate with no contract changes required.
   Immediate value even for contracts that don't use `Verification:`.

2. **Contract `**Verification:**` section** (phase 2) — enables the
   red-green pattern. Needs phase 1's infrastructure.

3. **Executor steering prompt** (phase 4) — highest leverage but
   lowest priority; the executor can already run bash commands and
   will often run tests naturally. The explicit prompt injection just
   makes it more reliable.

Ship 1, then 2+3 together.

## Skill changes (skills/imp.md)

The skill is the orchestrator's interface to imp. It needs to teach
the TDD authoring pattern — the orchestrator is the one who writes
the tests before handing the contract to imp.

### Why the orchestrator writes the tests

There's no test-builder sub-agent and none is planned. The
orchestrator (Sonnet/Opus in Claude Code) writes the failing tests
as part of contract authoring because:

- The orchestrator already has full codebase context when authoring
  the contract — writing a test is incremental, not a separate job.
- Writing a good failing test requires understanding what the correct
  behavior *should be*. That's synthesis work, not rote coding — it
  belongs in the orchestrator, not delegated to a cheap executor.
- The test *is* the acceptance criterion. "Make this test pass" is a
  tighter contract than prose bullets.

A dedicated test-builder sub-agent might eventually make sense for
mechanical cases (e.g., "add property-based tests for all these parse
functions") but is not needed to unlock the TDD pattern.

### Changes to the skill

**"Writing a contract" section** — add the TDD authoring pattern
as a named pattern alongside the existing guidance:

> **TDD pattern (when to use `**Verification:**`):** If the feature
> or fix has a clear, testable outcome, write the failing test first
> — before writing the contract. Add it as a `create:` scope entry
> (or `edit:` if extending an existing test file), write the test
> body so it fails against current code, then add a
> `**Verification:**` section pointing at the test command. The
> executor's job becomes "make this test pass" — which is a tighter
> brief than prose acceptance bullets and gives it an objective
> steering signal throughout its loop.
>
> Pre-flight before `imp build`: run the verification command
> yourself to confirm it fails (`dotnet test --filter ...`). A test
> that already passes means the contract is wrong, not the code.

Add `**Verification:**` to the "bits that earn their keep" list, after
Acceptance:

> - **Verification** (optional) — shell commands that must all exit 0
>   at closeout. Used for the TDD pattern: list the test command(s)
>   that should start failing and finish passing. Imp runs these after
>   the executor finishes and demotes to `failure` if any exit non-zero.

**"Running a build" section** — add a TDD pre-flight step:

> For TDD contracts: before `imp build`, run the `**Verification:**`
> commands yourself to confirm the tests fail. A command that already
> passes means the contract is testing the wrong thing or the work
> is already done.

Updated build steps (insert between validate and build):

```
2b. (TDD only) Run Verification commands manually — confirm they fail.
```

**"Reading proof-of-work" section** — document `tests.verify_results`:

> - **`tests.verify_results`**: list of `{ command, exit_code,
>   output_snippet }` populated by imp running the verification
>   commands after the executor. Non-empty only when
>   `**Verification:**` or a project verify config is present. A
>   non-zero `exit_code` here is the objective reason for a
>   `terminal_state: failure` — the executor's self-report in `notes`
>   may say it passed, but this field is ground truth.

**Quick-reference table** — add a row:

| TDD pre-flight | Run `**Verification:**` commands manually; confirm they fail before `imp build` |
