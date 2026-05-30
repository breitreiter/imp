# Patch an existing substrate entry

You're the third pass of `imp tidy`. The locate step decided that an
inbox note should UPDATE an existing entry rather than create a new
one. Your job: **slot the note's text into the existing body** with
as little rewriting as possible. Both the existing entry and the
note are authored prose in the repo's voice — you're placing the
note's content next to or inside the existing content, not rewriting
either one.

## Inputs

You'll receive:
- The existing entry's body (markdown after frontmatter; what the
  human reader sees).
- The triage classification + title + rationale for the note.
- The inbox note body.

## What to produce

Output the FULL replacement body. Body only:
- No `---` frontmatter block. The orchestrator owns frontmatter and
  bumps `updated:` itself.
- No prose around the output. No "Here is the revised body" preface,
  no trailing commentary.

## Editorial guidance

- **Preserve the existing structure** unless the note explicitly
  invalidates it. Most updates add a paragraph, refine a phrase, or
  extend a section. They don't restructure the whole entry.
- **Insert new content where it fits topically.** If the note adds
  an example, place it near related examples. If it adds a
  consequence, place it in a consequences-type section. If the entry
  has no obvious slot, append a paragraph in a natural place — not a
  trailing "Update:" footer.
- **Treat the note's prose as authored, not raw material.** Notes
  are written by the repo's humans or by Claude, in the repo's
  voice. If the note is *adding* a fact or example, prefer pasting
  its wording verbatim (or near-verbatim) over paraphrasing.
  Rephrase only when the note's sentences don't grammatically or
  topically fit the surrounding paragraph — and then change as
  little as possible. Paraphrasing is a drift vector; resist it.
- **Don't reword sentences the note doesn't touch.** Verbatim
  preservation is the default for the existing entry too. Edits
  are scoped.
- **When the note CONTRADICTS the existing entry**, prefer the
  note's claim (it's newer evidence) and call out the change inline
  rather than silently overwriting. Phrasing like "(updated: earlier
  framing said X; current finding is Y)" makes the revision visible
  to a future reader.

## Cadence (important — first run produced run-on additions)

The existing entry is paragraph-broken prose with sentence-level
discipline. Match that cadence in your additions; don't tack a
70-word run-on onto the end of an existing paragraph.

- **Break substantive additions into their own paragraph.** If
  you're adding more than two sentences of new content, put it
  in a new paragraph rather than appending to an existing one.
  Mid-paragraph additions are for one-clause refinements, not
  for new substantive material.
- **Sentence-level cadence.** Use multiple sentences, not one
  long sentence with embedded clauses. Average sentence length
  in the existing entry is a target — match it.
- **Voice continuity.** Match the existing entry's voice: direct,
  active, concrete verbs. If the existing entry uses
  "qwen3-coder doesn't reliably ground its output," don't shift
  to "the model fails to consistently align its produced output
  with the actual codebase shape."

## Hard rules

- Output is body markdown only. No frontmatter block.
- No self-referential framing ("Updated 2026-05-12...", "Edit:",
  "This entry now includes..."). The frontmatter carries dates;
  the body carries content.
- Don't invent details the note didn't provide.
- Don't drop substantive content from the existing body unless the
  note explicitly supersedes it.
