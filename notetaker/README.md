# notetaker lab

Synthetic visits and the eval harness for the ambient notetaker (design:
`docs/NOTETAKER.md`). No PHI: every visit here is written by hand.

```
visits/      one JSON per visit: diarized transcript + answer key
results/     eval output, gitignored
```

Each answer key lists what a correct note contains. Teeth and surfaces must
match exactly; descriptions match on any listed keyword. An item with no
`tooth` may be drafted with none or with a span ("18-20", "lower arch"), but a
single specific tooth there is a tooth error; `accept_tooth` names the one tooth
that is also fine (sutures at the extraction site). `forbidden` is small
talk that must not reach the note. `review_flag_required` marks a visit that is
unclear on purpose, where the right draft asks instead of guessing.

```bash
just notetaker-eval gemma3:4b --pause 10  # local model, idling 10 s between visits
just notetaker-eval gemma3:4b --only v016  # one visit
just notetaker-eval-claude                 # Claude Opus 5.5 via the API; --model / --effort to change
just notetaker-rescore notetaker/results/<run>.json   # re-score saved drafts; no model
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

## Baselines (local models, 2026-10-09)

| | gemma3:4b | llama3.1:8b |
|---|---|---|
| recall | 60% | 72% |
| tooth errors | 26 | 10 |
| ungrounded | 2 | 0 |
| noise | 11 | 2 |
| extras | 50 | 26 |
| s/visit (M1 Pro) | 18 | 13 |

Llama 3.1 8B is the stronger offline drafter and never invented a quote, but
still puts the wrong tooth or surface on about one visit in eight -- including
guessing the inaudible tooth in v016. Offline drafts are usable only as labelled
drafts behind the acknowledgement gate and the service's unclear-speech flags,
which is how the notes service treats them. Weak spots: a dentist overruling a
hygienist (v017), and charting arch- or quadrant-level work on a single tooth.

