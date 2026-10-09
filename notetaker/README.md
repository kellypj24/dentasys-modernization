# notetaker lab

Synthetic visits and the eval harness for the ambient notetaker (design:
`docs/NOTETAKER.md`). No PHI: every visit here is written by hand.

```
visits/      one JSON per visit: diarized transcript + answer key
results/     eval output, gitignored
```

Each answer key lists what a correct note contains. Teeth and surfaces must
match exactly; descriptions match on any listed keyword. `forbidden` is small
talk that must not reach the note. `review_flag_required` marks a visit that is
unclear on purpose, where the right draft asks instead of guessing.

```bash
just notetaker-eval gemma3:4b             # all visits
just notetaker-eval gemma3:4b --only v016 # one visit
just notetaker-test                       # scorer + fixture tests, no model
```

Scores, per run:

| Metric | Meaning |
|---|---|
| recall | key items the draft got right |
| tooth errors | right finding, wrong tooth or surfaces -- a different patient record |
| ungrounded | items whose evidence quote is not in the transcript |
| noise | small talk that leaked into the note |
| extras | tooth items matching nothing in the key; read them, the key may be incomplete |
