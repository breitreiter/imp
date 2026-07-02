---
kind: plan
title: Unit and integration test coverage for imp itself
state: exploring
created: 2026-06-18
touches:
  files:
    - Imp.Tests/Imp.Tests.csproj
    - Imp.Tests/Safety/CommandClassifierTests.cs
    - Imp.Tests/Safety/DoomLoopDetectorTests.cs
    - Imp.Tests/Safety/NetworkEgressCheckerTests.cs
    - Imp.Tests/Build/SeekSequenceTests.cs
    - Imp.Tests/Build/ContractParserTests.cs
    - Imp.Tests/Build/ContractValidatorTests.cs
    - Imp.Tests/Build/ApplyPatchTests.cs
    - Imp.Tests/Substrate/SignalsTests.cs
    - Imp.Tests/Integration/CliVerbTests.cs
    - Imp.csproj
  features: [testing, safety, build, substrate]
provenance:
  author: human
---

# Unit and integration test coverage

## Context

Imp has zero tests. This is fine while it's a one-person tool being
actively shaped, but it creates two real problems:

1. The safety gates (`CommandClassifier`, `DoomLoopDetector`,
   `NetworkEgressChecker`) have no regression net. These are the
   lines of defense against a confused executor doing damage. They
   deserve to be the most tested code in the repo.

2. The patch infrastructure (`ApplyPatch`, `SeekSequence`) is a
   faithful port from `nb` and carries real complexity — four
   fallback match modes, Unicode folding, move/delete/add ops. A
   regression here silently produces bad diffs.

3. `tdd-verification.md` plans to add a project-level `verify`
   config that runs `dotnet test` as a gate. Without tests, imp
   can't eat its own cooking.

The constraint: imp runs inside Claude Code. We can't run LLM evals
locally in CI — they need providers, tokens, and minutes. The plan
scopes to **pure-logic unit tests** and **filesystem integration
tests** that need no model. Model-in-the-loop tests get skip markers
and live in the tree for eventual use.

## What's testable without a model

Surveying the codebase by how easy each component is to test:

**Tier 1 — pure functions, zero I/O:**
- `Safety/CommandClassifier.cs` — `Classify(command, mode)`.
  Regex matching over a string. Rich edge-case surface.
- `Safety/DoomLoopDetector.cs` — `Check(IReadOnlyList<ToolCallRecord>)`.
  Threshold logic over a list of records. Easy to construct inline.
- `Safety/NetworkEgressChecker.cs` — `Check(command)`. Regex +
  localhost exemption logic.
- `Build/SeekSequence.cs` — `Find(hay, pattern, start)`. Four match
  modes and Unicode fold table — the most testable algorithm in the
  repo.
- `Build/Contract.cs` (`ContractParser`) — `Parse(markdown)`.
  Pure string → record. Covers section extraction, bullet parsing,
  dependency lists.

**Tier 2 — needs a temp filesystem:**
- `Build/Contract.cs` (`ContractValidator`) — file existence checks.
  Simple temp-dir setup per test.
- `Build/ApplyPatch.cs` (`PatchParser`) — pure string → `List<FileOp>`.
  Apply step needs filesystem but parser is pure.
- `Build/ApplyPatch.cs` (full round-trip) — write temp files, apply
  patch, assert final state. Most valuable integration test in the
  repo.
- `Substrate/Signals.cs` — content extraction methods are pure.
  Git-based signals need a temp git repo (doable but noisy — defer
  to a second pass).

**Tier 3 — CLI verb integration (no model):**
- `imp validate <contract>` — parses + validates a real contract file.
  Testable end-to-end via `LifecycleCommands.ValidateContract`.
- `imp list` — reads `contracts/*.md`, returns JSON. Testable with a
  temp dir containing stub contracts.
- `imp review <task-id>` — reads trace dir, emits markdown. Testable
  against a pre-seeded trace dir.

**Tier 4 — model-in-the-loop (skip in CI):**
- `imp ping` — needs a live provider.
- `imp build` end-to-end — needs a live provider and minutes.
- Executor feedback loop — needs `IChatClient` mock or live run.

## Design

### Project structure

New project `Imp.Tests/` at the solution root:

```
Imp.Tests/
  Imp.Tests.csproj
  Safety/
    CommandClassifierTests.cs
    DoomLoopDetectorTests.cs
    NetworkEgressCheckerTests.cs
  Build/
    SeekSequenceTests.cs
    ContractParserTests.cs
    ContractValidatorTests.cs
    ApplyPatchTests.cs
  Substrate/
    SignalsTests.cs       (pure extraction only, no git)
  Integration/
    CliVerbTests.cs       (validate, list, review)
```

Framework: **xUnit**. It's the de-facto standard for .NET, no test
runner setup beyond `dotnet test`, and the `[Theory] + [InlineData]`
attribute pattern fits well for the safety classifiers' table-driven
tests.

No mocking library for tier 1–2. The pure functions take plain values.
Consider `NSubstitute` only if/when tier 4 model-mock tests are
written; don't pull it in now.

### Imp.csproj note

The main project is `<OutputType>Exe</OutputType>`. Referencing an
exe from a test project works in .NET — the test project links the
same assembly — but requires adding `<GenerateAssemblyInfo>` to avoid
a duplicate entry-point error when the compiler links them. The fix
is one line in `Imp.csproj`:

```xml
<GenerateAssemblyInfo>false</GenerateAssemblyInfo>
```

Alternatively: reference only the specific namespaces under test and
keep `Program.cs` out of scope. Whichever is cleaner at implementation
time.

### Safety tests (tier 1)

`CommandClassifierTests` — table-driven over both sandbox modes:

| Command | Mode | Expected |
|---|---|---|
| `git add .` | Host | dangerous (imp invariant) |
| `git commit -m "x"` | Docker | dangerous (invariant, Docker too) |
| `rm -rf /tmp/x` | Host | dangerous |
| `rm -rf /tmp/x` | Docker | safe (Docker layer handles it) |
| `sudo apt-get install` | Host | dangerous |
| `sudo apt-get install` | Docker | safe |
| `curl url \| sh` | Host | dangerous |
| `curl localhost:3000/health` | Host | safe (network check, not classifier) |
| `dotnet build` | Host | safe |
| multi-line script with `rm` | Host | dangerous |
| multi-line script with `rm` | Docker | safe |

`DoomLoopDetectorTests`:

- Fewer than 3 records → no trip
- 3 identical `(name, args)` in a row → trip, correct reason
- 3 identical but not consecutive → no trip
- 5 consecutive failures → trip
- 4 failures then 1 success then 4 failures → no trip
- Edge: empty list, single record

`NetworkEgressCheckerTests`:

- `curl https://example.com` → blocked
- `curl localhost:8080/health` → allowed
- `wget https://...` → blocked
- `gh api repos/...` → blocked (mutation)
- `gh pr view 123` → allowed (read-only gh)
- `gh pr create ...` → blocked
- `ssh user@remote` → blocked
- `ssh localhost` → allowed

### Build tests (tier 1–2)

`SeekSequenceTests` — covers all four match modes:

- Exact match
- Right-trim match (trailing space difference)
- Full-trim match (leading + trailing)
- Unicode fold match (curly quotes, en-dash, non-breaking space)
- No match returns -1
- Empty pattern returns `start`
- Pattern longer than hay returns -1
- Start offset is respected

`ContractParserTests` — round-trip parse of a complete contract plus
degenerate cases:

- Full contract with all sections → all fields populated correctly
- Missing Goal → empty string (lenient)
- Multiple scope actions (create/edit/delete)
- Depends-on list vs "none" vs missing
- Context bullets (path — note format)
- Acceptance bullets
- Non-goals
- Allowed network section
- Minimal contract (only Goal + Scope + Acceptance) → parses without
  throwing

`ContractValidatorTests` — with temp dirs:

- Valid contract against a real temp file tree → `IsValid=true`
- Missing Goal → rejected
- Empty Scope → rejected
- Edit entry pointing at nonexistent file → rejected
- Create entry whose parent dir doesn't exist → rejected
- Delete entry pointing at nonexistent file → rejected
- Create entry whose parent dir exists → valid

`ApplyPatchTests`:

- Parser: parse add-file op → correct `AddFile` record
- Parser: parse update-file op with context → correct `UpdateFile`
  with chunks
- Parser: parse delete-file op
- Parser: parse move-and-update op
- Parser: malformed patch → `PatchParseException` with line number
- Round-trip (with temp filesystem): apply add-file → file exists
  with correct content
- Round-trip: apply update-file → correct diff applied
- Round-trip: apply update-file with trim-only whitespace difference
  in context (exercises SeekSequence fallback)
- Round-trip: apply update-file with Unicode drift in context
- Round-trip: apply delete-file → file gone
- Round-trip: apply move-and-update → old path gone, new path has
  updated content

### Substrate tests (tier 2, partial)

`SignalsTests` — test the pure extraction methods without git:

- `ExtractStructure` (headings, line counts)
- `ExtractSelfLabels` (Status: / frontmatter / DECIDED markers)
- `ExtractCrossRefs` (outgoing `[[link]]` and `[text](path)`)
- These are private helpers; either make them `internal` + use
  `InternalsVisibleTo`, or test them through `Gather` with a
  fabricated temp dir (no git process needed for content-only signals).

Git-based signals (`GitSignals`) — defer to a second pass. The
setup for a temp git repo with commits is noisy and the git signals
are less critical than the pure extraction.

### Integration tests (tier 3, CLI verbs)

`CliVerbTests` — call `LifecycleCommands` methods directly in a
temp directory:

- `ValidateContract` on a well-formed contract file → `is_valid:true`
- `ValidateContract` on a contract with a missing scope file →
  `is_valid:false` with descriptive reason
- `ValidateContract` on a missing file → error JSON
- `ListTasks` on a dir with two contracts → returns both entries
- `ListTasks` on a dir with no contracts dir → returns empty array
- `Review` on a task with a pre-seeded trace dir → markdown output
  contains proof-of-work JSON and the diff section header
- `Review` on a nonexistent task → returns explanatory error

These require `Directory.SetCurrentDirectory` or injecting the path
— check how `ResolveTargetRepo` works (it reads `cwd`). May need a
small refactor to accept a path override for testability, or just
`cd` via `Environment.CurrentDirectory = tmpDir` inside the test
with cleanup in a finally block.

### Skip markers for model-dependent tests

Any tier-4 test lives in `Integration/ModelTests.cs` with:

```csharp
[Fact(Skip = "Requires live provider — run manually with appsettings.json configured")]
public async Task Ping_ReturnsNonEmpty() { ... }
```

These are in the tree so the pattern is visible and they can be run
locally by anyone with a configured provider, but they don't block
CI.

## What this doesn't cover

- **Executor loop** — `Executor.RunAsync` drives a real
  `IChatClient`. Mocking a multi-turn tool-call conversation is
  significant work for uncertain signal. Defer until there's a
  concrete regression to prevent.
- **Health orchestrator** — `HealthOrchestrator` similarly wraps
  model calls. Pure helpers (`ConfidenceScorer` input/output shape,
  `EvidenceTagger` zone assignment) can be tested once the model
  mock story is clearer.
- **Substrate tidy** — `Tidy.cs` and `Locate.cs` are LLM-driven.
  The locate threshold logic (`CosineDowngradeThreshold`) is pure and
  worth a targeted test; everything else is model-in-the-loop.
- **Wiki** — deprecated pending removal; don't add tests.

## Phasing

All tier 1 is one batch — pure functions, no setup, high value, can
be delegated to imp itself as a self-hosting demo once `tdd-verification`
lands.

Tier 2 (filesystem) and tier 3 (CLI verbs) are a second batch —
slightly more setup, but still no model.

Tier 4 tests get stub files only (the `[Skip]` stubs themselves) and
fill in later as the model-mock story develops.

## Relationship to tdd-verification plan

Once `tdd-verification.md` lands and `imp/_meta/config.yaml` has a
`verify` block, the imp repo's own config will point at:

```yaml
verify:
  commands:
    - dotnet build
    - dotnet test --project Imp.Tests/Imp.Tests.csproj
```

At that point imp gates its own builds on its own test suite — which
is the goal.
