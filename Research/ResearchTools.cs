using System.ComponentModel;
using Microsoft.Extensions.AI;

namespace Imp.Research;

// Tools specific to research mode. Today: just finish_research, the
// terminal-action tool every mode registers. As web mode lands, this file
// grows to hold web_search / http_get / extract_text and any other
// reach-typed surfaces that aren't already in the file-tool set.

public static class ResearchTools
{
    // Output-compactness caps. The whole point of research mode is to spare the
    // parent from spelunking the repo itself, so the report it gets back must
    // not re-bloat its context. These are soft caps applied deterministically
    // at capture (trim/truncate, not reject) so a slightly-over report still
    // lands instead of risking a retry that times out into no report at all.
    const int MaxExcerptChars = 1200;        // ~15-20 lines; longer excerpts are truncated
    const int MaxCitationsPerFinding = 6;    // extra citations beyond this are dropped

    // The finish_research factory, referenced by every ModeDefinition's
    // FinishToolFactory. Closes over the per-run ResearchState so the tool
    // can capture the validated input and signal the loop to terminate.
    //
    // Validation is enforced here, not in the model prompt: the prompt
    // describes the contract; the tool refuses bad input with an error
    // string the model can read and retry against. The validation rules
    // are the field-basis citation contract:
    //   - findings array is non-empty
    //   - every finding has at least one citation
    //   - every citation has at least one non-empty excerpt
    //   - per-kind required fields (file: path + line range; url: url)
    //   - confidence is one of the enum values (handled by the converter)
    public static AIFunction BuildFinishResearchTool(ResearchState state) =>
        AIFunctionFactory.Create(
            (
                [Description("One-paragraph direct answer. No 'I found that...' framing — state the conclusion.")] string synthesis,
                [Description("What was looked at, what wasn't, where gaps remain.")] ResearchCoverage coverage,
                [Description("Findings that back up the synthesis. At least one. Each finding: claim, citations[] (each with excerpts[]), confidence (high/medium/low), reasoning.")] List<Finding> findings,
                [Description("Optional. Conflicts between findings, with supporting/contradicting indices into the findings array, a chosen resolution, and reasoning.")] List<Conflict>? conflicts = null,
                [Description("Optional. Open questions for the parent to consider issuing as a follow-up research run.")] List<string>? follow_ups = null,
                [Description("Optional. Questions that couldn't be answered without clarification, with the assumption made instead.")] List<ResearchBlockedQuestion>? blocked_questions = null) =>
            {
                var input = new FinishResearchInput(
                    Synthesis: synthesis ?? "",
                    Coverage: coverage ?? new ResearchCoverage(
                        Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>()),
                    Findings: findings ?? [],
                    Conflicts: conflicts,
                    FollowUps: follow_ups,
                    BlockedQuestions: blocked_questions);

                input = NormalizeOutput(input);

                var error = Validate(input);
                if (error is not null)
                    return $"ERROR: finish_research input rejected — {error}. Adjust and call again.";

                state.Captured = input;
                return $"Recorded research report ({input.Findings.Count} finding(s)).";
            },
            name: "finish_research",
            description: """
                Record the final research report and terminate the run. Call exactly
                once when you have enough evidence to answer the question.

                Validation rules (failures return an error you can retry against):
                  - findings array must be non-empty.
                  - every finding must have non-empty claim and reasoning.
                  - every finding must have at least one citation.
                  - every citation must have at least one non-empty excerpt.
                  - citation kind="file" requires path + line_start + line_end.
                  - citation kind="url" requires url.
                  - confidence must be one of: high, medium, low.

                Excerpts make findings auditable without re-fetching — the parent
                agent verifies your claims from the report alone, no round-trip.
                Quote enough text that the citation stands on its own.

                Keep it compact — the parent reads the whole report. A finding
                keeps at most its first few citations and over-long excerpts are
                truncated, so lead with the load-bearing ones and quote tight
                ranges (3-10 lines), not whole files.
                """);

    // Finish-tool for the review-confidence pass. Different shape from
    // finish_research: a single 0..100 score plus a one-sentence
    // justification, captured into a per-run state record. Bypasses the
    // ResearchState/ResearchReport machinery because the output is
    // internal-only — never reaches the review report — and the schema is
    // small enough that bolting it onto finish_research would mean stuffing
    // meaningless required fields.
    //
    // Lives in ResearchTools.cs (not Review/) per plans/review-mode.md so
    // that all finish-tool factories cluster together regardless of which
    // domain consumes them.
    public sealed class ConfidenceState
    {
        public int? Score { get; set; }
        public string Justification { get; set; } = "";
    }

    public static AIFunction BuildFinishConfidenceTool(ConfidenceState state) =>
        AIFunctionFactory.Create(
            (
                [Description("Confidence score from 0 to 100 per the rubric in the system prompt.")] int score,
                [Description("One sentence justifying the score. Mechanical, no hedging.")] string justification) =>
            {
                if (score < 0 || score > 100)
                    return $"ERROR: score {score} out of range [0,100]; call again with a valid score.";
                state.Score = score;
                state.Justification = justification ?? "";
                return $"Recorded confidence {score}.";
            },
            name: "finish_confidence",
            description: """
                Record the confidence score for this finding and terminate. Call exactly once.

                The score is an integer in [0, 100] per the rubric in the system prompt:
                  0 = false positive or pre-existing; 25 = couldn't verify;
                  50 = real but nitpicky; 75 = real and likely hit in practice;
                  100 = definitely real, citation directly confirms.

                The justification is one sentence — kept for trace.jsonl, not shown to the parent.
                """);

    // Deterministically trims the report to the compactness caps before it is
    // captured: drops citations past the per-finding limit (keeping the first,
    // which the model lists strongest-first) and truncates over-long excerpts.
    // Runs before Validate, so the trimmed set is what gets contract-checked —
    // truncation only shortens non-empty excerpts, never empties them.
    static FinishResearchInput NormalizeOutput(FinishResearchInput input)
    {
        if (input.Findings is null || input.Findings.Count == 0) return input;

        var findings = input.Findings.Select(f =>
        {
            var citations = f.Citations is { } cs ? cs : (IReadOnlyList<Citation>)Array.Empty<Citation>();
            var trimmed = citations
                .Take(MaxCitationsPerFinding)
                .Select(c =>
                {
                    var excerpts = c.Excerpts is { } xs ? xs : (IReadOnlyList<string>)Array.Empty<string>();
                    return c with { Excerpts = excerpts.Select(TruncateExcerpt).ToList() };
                })
                .ToList();
            return f with { Citations = trimmed };
        }).ToList();

        return input with { Findings = findings };
    }

    static string TruncateExcerpt(string excerpt)
    {
        if (string.IsNullOrEmpty(excerpt) || excerpt.Length <= MaxExcerptChars)
            return excerpt;
        return string.Concat(
            excerpt.AsSpan(0, MaxExcerptChars),
            "\n… [truncated by imp — cite a narrower line range]");
    }

    static string? Validate(FinishResearchInput input)
    {
        if (input.Findings is null || input.Findings.Count == 0)
            return "findings[] is empty; at least one finding is required";

        for (int i = 0; i < input.Findings.Count; i++)
        {
            var f = input.Findings[i];
            if (string.IsNullOrWhiteSpace(f.Claim))
                return $"finding[{i}].claim is empty";
            if (string.IsNullOrWhiteSpace(f.Reasoning))
                return $"finding[{i}].reasoning is empty; every finding needs one sentence on why the citations support the claim";
            if (f.Citations is null || f.Citations.Count == 0)
                return $"finding[{i}] has no citations; every finding needs at least one";

            for (int j = 0; j < f.Citations.Count; j++)
            {
                var c = f.Citations[j];
                if (c.Excerpts is null || c.Excerpts.Count == 0
                    || c.Excerpts.All(string.IsNullOrWhiteSpace))
                    return $"finding[{i}].citations[{j}] has no non-empty excerpts";

                switch (c.Kind)
                {
                    case CitationKind.File:
                        if (string.IsNullOrWhiteSpace(c.Path))
                            return $"finding[{i}].citations[{j}] kind=file requires path";
                        if (c.LineStart is null || c.LineEnd is null)
                            return $"finding[{i}].citations[{j}] kind=file requires line_start and line_end";
                        if (c.LineStart > c.LineEnd)
                            return $"finding[{i}].citations[{j}] line_start ({c.LineStart}) > line_end ({c.LineEnd})";
                        break;
                    case CitationKind.Url:
                        if (string.IsNullOrWhiteSpace(c.Url))
                            return $"finding[{i}].citations[{j}] kind=url requires url";
                        break;
                }
            }
        }
        return null;
    }
}
