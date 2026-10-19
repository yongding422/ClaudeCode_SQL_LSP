# Follow-up evaluation on new primary procedures

The [three-task suite](sql-server-samples-new-procedures-v1.json) and [acceptance rubric](sql-server-samples-new-procedures-acceptance-v1.json) were authored before running any model sessions for this cohort. Primary procedures ReceivePurchaseOrders, PaySuppliers and RecordInvoiceDeliveries do not occur in the original Q, additional H or investigation R evidence lists. Supporting tables/helpers can be shared. This remains a selected familiar corpus, with implementing-agent source review and prior knowledge of the product's failures; it is not an independent blinded holdout.

Keep the historical questions, grades and reports unchanged. Freeze the new source snapshot and acceptance rubric before either arm runs. The three tasks cover an unfiltered write join, a local table work list with transitive lookups, and a branch-to-JSON/computed-column dependency. They deliberately require evidence the analyzer may not fully model. Read/Grep fallback and an honest limitation can be useful; tool status cannot substitute for a correct answer.

For a future matched comparison, predeclare 3 questions × 2 repeats × 2 arms (12 fresh sessions), `deepseek-v4-pro`, low effort, two workers within each phase, baseline first, seed 42, 180 seconds and $0.50 reported cap per session ($6 maximum reported cap). Use `prescribed` workflow and the same 450-word answer limit for both arms. Package/fingerprint and test the exact native candidate first. Record cold, persisted-restart or same-process warm conditions separately; the standard controller is cold only. A new model invocation is separate from this preparation.

```powershell
python scripts/rerun-sample-study.py prepare --source D:/Project/sql-server-samples --plugin D:/path/to/verified/native-plugin --model deepseek-v4-pro --suite docs/evaluation/sql-server-samples-new-procedures-v1.json --output artifacts/UNIQUE-new-procedures
python scripts/review-answer-acceptance.py freeze --study artifacts/UNIQUE-new-procedures --rubric docs/evaluation/sql-server-samples-new-procedures-acceptance-v1.json --output artifacts/UNIQUE-new-procedures-review
python scripts/rerun-sample-study.py run --study artifacts/UNIQUE-new-procedures --dry-run
```

After an authorized run, generate the anonymous review packet and review every new answer from its blank template. Apply essential facts, evidence, uncertainty, relevance and all consequential extra claims. Preserve failed/missing sessions. Report acceptance and critical errors before per-task time/tokens and complete payload/brief delivery bytes. Delivery counts do not prove evidence consumption. Changing the runner's instrumentation is an explicit historical mismatch; retain its hash. Comparisons to earlier cohorts are descriptive, not matched effects.

An independent developer can review the packet without the private arm mapping. Record their real identity/type and actual prior exposure. Direct user acceptance and correction minutes stay unknown until supplied by a developer; do not generate those observations. This preparation does not demonstrate a performance gain or provide a codebase-memory comparison.
