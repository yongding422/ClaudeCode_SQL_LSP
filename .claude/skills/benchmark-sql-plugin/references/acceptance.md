# Correctness and acceptance review

Use `<repo>/docs/evaluation/answer-acceptance.md` for decision definitions, schemas, limitations and commands. The helper is `scripts/review-answer-acceptance.py`; it never calls a model or automatically grades correctness.

Choose the cohort explicitly. The original 12 tasks use `sql-server-samples-acceptance-v1.json`. The separate five-task `sql-server-samples-investigations-v1.json` cohort uses `sql-server-samples-investigations-acceptance-v1.json`; the cold controller accepts it with `prepare --suite`. It is new prompts on previously studied procedures, not a holdout. Keep the cohorts and historical grades separate. The current warm controller uses only the original cohort.

After study preparation, before `run`, execute `freeze --study <study> --rubric <overlay> --output <separate-review-directory>`. Both preparation and dry-run are free of model calls. A freeze on an existing run is retrospective and must be labeled. Never silently adjust the gold after answers are known.

After runs, execute `packet --review <review-directory>`. Provide only its `reviewer/` directory and frozen SQL access to the reviewer; keep `private/` and raw tool/usage logs withheld until submission. Answer wording may reveal tools. Disclose reviewer identity/type and whether labels were seen; implementing agents are not independent reviewers.

Fill every answer in `reviews.template.json` and save `reviews.json`: essential facts, evidence, uncertainty, relevance, explicit critical/unsupported finding lists, one of four acceptance decisions and notes. Empty findings are `[]`, unreviewed values remain null. Do not copy historical grades or infer acceptance from keyword/tool success. Critical errors, unsupported consequential extra claims and missing essential facts prevent acceptance even if other facts are correct. Failures stay assigned and rejected.

Use `report --review <review-directory> --reviews <reviews.json> --output <new-report-directory>`. User feedback is a separate optional `--user-feedback` input supplied directly by a human developer; an agent review cannot be relabeled user acceptance. Unmeasured correction minutes/follow-ups and absent user feedback remain unknown. Review disagreement requires preserving separate submissions, not averaging away serious mistakes.

Report acceptance as written, critical errors, fact/evidence/uncertainty coverage, correction effort, per-question regressions and paired outcomes before resources. Resource savings cannot compensate for incorrect answers. Source-only uncertainty and cache freshness limits remain explicit. Deterministic cache invalidation tests are separate from a developer accepting a model answer after an edit; the existing static runner does not implement that multi-turn experiment.
