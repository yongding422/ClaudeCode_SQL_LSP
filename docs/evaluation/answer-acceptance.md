# Evaluate whether a developer can use the answer

Acceptance is the primary outcome for new SQL investigations. Token usage and speed remain secondary. The existing 12-question rubric, grades, reports and model transcripts are unchanged. Required-fact coverage is not a percentage of entirely correct answers, and reviewer acceptance is not actual user satisfaction.

## Cohorts and frozen expectations

- Historical cohort: [original 12 questions](sql-server-samples-suite.json), with a new [acceptance overlay](sql-server-samples-acceptance-v1.json). The overlay adds developer goals, essential facts, critical-error examples, required evidence, expected uncertainty and optional detail. It does not retroactively replace historical scores.
- Investigation extension: [five realistic tasks](sql-server-samples-investigations-v1.json) and their [acceptance overlay](sql-server-samples-investigations-acceptance-v1.json). They cover onboarding, change-impact review, failed password changes, fulfillment loop hazards and reseeding uncertainty. These are new prompts over previously studied procedures, **not an independent holdout**. Report the cohorts separately.
- New primary procedures: [three tasks](sql-server-samples-new-procedures-v1.json), [acceptance overlay](sql-server-samples-new-procedures-acceptance-v1.json) and [pre-run protocol](new-procedures-protocol.md). The three primary procedures were absent from earlier cohort evidence lists; supporting sources can overlap. No new model results or independent human acceptance are implied by preparing this cohort.
- Cache freshness: use deterministic [materialized-index tests](../../tests/SqlLsp.Mcp.IntegrationTests/MaterializedCacheTests.cs) and [report-cache tests](../../tests/SqlLsp.Mcp.IntegrationTests/PersistentCacheTests.cs). They check edits, unchanged timestamps, added ambiguity, deletion/renaming and restart reuse in disposable workspaces. Do not claim a model correctly answered an edited source just because these tests pass. A future edited-source model cohort needs its own paired snapshots and gold; the static runner does not implement that interactive experiment.

Freeze acceptance expectations before a new model run. The helper binds the overlay to the exact canonical suite JSON, source revision, manifest and corpus. Canonical suite SHA-256 uses `json.dumps(suite, sort_keys=True).encode()` after UTF-8 JSON decoding; it identifies suite content rather than pretty-printing. `freeze` before `runs/` exists records `prospective`; after it exists records `retrospective`. Files/hashes and timestamps are audit records, not signed proof that reviewers never saw previous material. Prior exposure and implementing-agent involvement must be disclosed.

Never change expectations after seeing results to improve one arm. Corrections to faulty gold require a new version and transparent re-review of both arms. Present retrospective acceptance reviews separately from predeclared experiments.

## Acceptance decisions

Judge the original answer as received, before editing. Review every consequential assertion, including additional claims outside the checklist. SQL source evidence and valid SQL semantics determine correctness; a tool's success, a keyword match or the answer's confident tone do not.

| Decision | Meaning |
|---|---|
| `accept_as_written` | All essential facts correct; complete consequential evidence; uncertainty handled; relevant and sufficient; no critical errors or material unsupported claims. |
| `minor_edits` | All essential facts and uncertainty correct; needs presentation/clarity or citation edits without changing the conclusion; no critical or material unsupported claims. |
| `major_correction` | Missing/incorrect essential facts, substantive evidence gaps, or requires meaningful further investigation/correction. |
| `reject` | Wrong or misleading conclusion, unusable answer, failed session or missing answer. |

Score each essential fact and evidence support, uncertainty handling and relevance as 0, 0.5 or 1. Missing optional detail never blocks acceptance. The helper rejects accepted decisions with partial essential facts, uncertainty below 1, failed/missing answers or consequential findings. `accept_as_written` also requires evidence and relevance scores of 1. `minor_edits` requires at least 0.5 for evidence and relevance. Other decisions still require explicit review; they are not assigned by the helper.

Each critical error or material unsupported claim needs `claim`, `explanation` and `evidence`. Record a consequential invented assertion even if all required facts are present. Explicit empty lists mean the reviewer checked and found none; null means unreviewed. Example: claiming that `RETURN -1` after an unconditional `THROW` is the procedure's normal error result blocks acceptance. A truthful statement that an exact sequence value needs runtime data can satisfy an uncertainty task.

The essential-fact score is a diagnostic coverage measure. It does not establish precision of all extra assertions; the findings and acceptance decision address those. Do not label this suite as automatically computing claim-level precision/recall.

## Anonymous review and developer feedback

The helper randomizes answer order and assigns opaque IDs. Reviewer packets contain final answers, questions, gold evidence locations and acceptance requirements. They exclude tool logs, arm labels, timings, usage and prior grades. The arm mapping is separate under `private/`. Give the reviewer only `reviewer/` and access to the frozen SQL snapshot. Do not give them `private/`, full study logs or historical results until submission.

This is label withholding, not guaranteed blinding: an answer may mention its tools. Reviewers declare identity, whether they are an implementing/independent agent/human, and whether arm labels were seen. An implementing agent is not independent because its packet is anonymous. For meaningful human validation, recruit a SQL developer other than the implementer. Additional reviewers should submit independent review files and separate reports; preserve and discuss disagreements rather than averaging away a critical error.

Only direct human-developer feedback belongs in `user-feedback.json`. Use the same four decisions and notes, retaining null entries for answers the developer has not evaluated. No supplied feedback means unknown user acceptance. The all-assigned user acceptance rate stays null until every answer in that arm has feedback; the observed subset rate and coverage are reported separately to expose selection bias. An actual user's opinion does not overwrite the source-based correctness review.

Reviewer and developer effort are separate fields:

- `correction_minutes`: measured hands-on source verification, correction and clarification work after receiving the answer. Record actual elapsed work, not a model estimate; explain whether the correction reached an acceptable answer in notes. Null means unmeasured, not zero. This field alone is not a guaranteed time-to-acceptance measurement.
- `follow_up_questions`: count actual clarification requests required by that evaluator. Retain null when not observed; do not invent zero follow-ups for historical answers.

Keep the initial acceptance decision even after correction. Source-dependent claims and explicit unknowns may be useful, but incomplete evidence cannot be treated as complete impact coverage. Live SQL behavior is not validated by a source-only study. No database access or SQL execution is introduced by this workflow.

## Commands

The standard cold controller already accepts a separate suite. Preparation and acceptance review make no paid model calls:

```powershell
python scripts/rerun-sample-study.py prepare --source D:/Project/sql-server-samples --plugin D:/path/to/verified/native-plugin --model deepseek-v4-pro --suite docs/evaluation/sql-server-samples-investigations-v1.json --output artifacts/UNIQUE-investigations
python scripts/review-answer-acceptance.py freeze --study artifacts/UNIQUE-investigations --rubric docs/evaluation/sql-server-samples-investigations-acceptance-v1.json --output artifacts/UNIQUE-investigation-review
python scripts/rerun-sample-study.py run --study artifacts/UNIQUE-investigations --dry-run
# A real model run is a separate, authorized step; default extension is 20 sessions.
python scripts/rerun-sample-study.py run --study artifacts/UNIQUE-investigations
python scripts/review-answer-acceptance.py packet --review artifacts/UNIQUE-investigation-review
# Review every anonymous answer; save reviewer/reviews.json from the null template.
python scripts/review-answer-acceptance.py report --review artifacts/UNIQUE-investigation-review --reviews artifacts/UNIQUE-investigation-review/reviewer/reviews.json --output artifacts/UNIQUE-acceptance-report
# If a developer supplies direct feedback, generate a new report with:
# --user-feedback artifacts/UNIQUE-investigation-review/reviewer/user-feedback.json
```

For the original 12 questions, omit `--suite` and use the default acceptance overlay (omit `freeze --rubric`). Freeze after study preparation and before either the cold or warm controller runs. The existing warm controller currently fixes the original cohort; it does not run this five-task extension. The project `.sqlscriptdom.json` is not inherited by isolated runs. Cache conditions are separate experimental choices; do not conflate persisted restart reuse with live in-memory prewarming.

An existing completed study can also use `freeze` then `packet`. It is explicitly retrospective; every answer needs a fresh acceptance review. The helper never reads historical `grades.json`, never pre-fills passing grades and never overwrites existing packet/report directories. If a run produced no metrics/answer, the complete assigned schedule is retained, the missing result cannot be accepted and its quality contribution is zero. Broken or duplicate schedules fail validation rather than silently dropping work. Changing answers, metrics, SQL or review rubrics after packet generation invalidates reporting.

The ordinary factual-grade workflow is still available for historical comparability; acceptance is an additional separate report. Do not merge new acceptance percentages with historical strict-pass percentages.

## Interpret the report

`acceptance-report.md` and `acceptance-results.json` order outcomes as:

1. Reviewer acceptance as written, across all assigned answers.
2. Critical-error and material unsupported-claim rates.
3. Essential-fact coverage, evidence support and uncertainty handling.
4. Explicit correction effort and direct developer acceptance, with observed/unknown counts.
5. Per-question regressions, paired accepted/neither/one-arm-only outcomes, then time and reported tokens.

Tokens include reported uncached input plus cache-read and cache-write tokens; incomplete usage stays unknown. Failures cannot improve acceptance by disappearing from the denominator. Corrections and feedback cannot be reconstructed from existing token/call logs. Backend `get_metrics` remains an operational tool: it cannot determine answer quality or infer satisfaction. Acceptance records stay in the local benchmark artifacts; no hook or remote feedback collector is added.

A higher acceptance rate with fewer consequential errors is useful evidence even if it costs more tokens. Report that tradeoff. Do not collapse correctness and resource use into a weighted number that permits a fast wrong answer to win. With selected related tasks, two repeats, unblinded implementing-agent review or incomplete developer feedback, report descriptive results and their limitations. An acceptance advantage on this suite is not proof of general superiority or an LSP-specific benefit. This change does not implement statistical confidence intervals, a codebase-memory arm, or an independent human review service.

Validation:

```powershell
python -m unittest discover -s scripts -p "test_*.py" -v
```
