---
captured: 2026-05-18T14:52:49Z
repo: imp
source: cli
git-head: dd94c982b873
---

Look into dumping 'imp note' in favor of triggering imp automatically on Claude Code exit/clear (via hooks). Imp would grind over the full conversation transcript and decide what's worth adding to the substrate vs noise — no need for me or Claude to remember to compose notes manually. Pros: zero friction, captures things we'd otherwise forget to write down, lets the synthesis pass weigh the full session arc rather than isolated captures. Cons: bigger input (full transcript vs targeted note), harder to attribute voice (notes today are authored prose; transcripts are dialog), risk of low signal-to-noise driving substrate drift. Open Qs: which hook (Stop? PreCompact?), does imp tidy then run on transcript chunks or extract candidate notes first, how to handle multi-session continuity.
