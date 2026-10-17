# Discovery-only repair: controlled rerun

The payload repair makes discovery usable, but this candidate still does not beat the fresh Grep control on time or input tokens. No general performance claim is supported.

Candidate: private Windows package **0.15.0**, source `1353a5e`, runtime-file fingerprint `8ed220bcb094e4e0c88e01fa447ee97b3d3d4b697db281f1605a4821719cf39d`. It contains R0 accounting and bounded discovery only, before the CTAS/scope/reuse/materialized-index/routing/semantic changes. The package records dirty provenance because a later regression test was untracked; the measured runtime files are frozen/fingerprinted. The failed prerelease candidate/preparation was not used for any model calls.

The frozen 12 questions × two repeats × two arms ran through Claude Code 2.1.302 using DeepSeek `deepseek-v4-pro` at `api.deepseek.com`, effort low, baseline first, two concurrent clients per phase, fresh SQL-only folders, 180-second timeout, 450-word instruction and $0.50 client-reported per-session cap. All 48 sessions completed. Corpus revision/hash, tasks/rubric and recorded protocol dimensions match the historical run. No SQL was executed or global plugin installation changed.

| Metric | Fresh Read/Grep/Glob | Candidate + file tools |
|---|---:|---:|
| Mean final-answer time | 17.27 s | 38.28 s |
| Mean total reported input including cache | 19,072.5 | 44,487.0 |
| Mean factual score | 94.62% | 94.97% |
| Mean citation score | 100% | 100% |
| Strict passes | 10/24 | 10/24 |
| Client tool failures | 1 | 1 |
| Discovery overflows | 0 | 0 |
| Analysis overflows | 0 | 1 |
| Sessions obtaining supported inline module analysis | 0 | 10/24 |
| Total client-reported cost | $1.8788 | $3.5659 |

Discovery's historical 41 overflows are eliminated. The remaining treatment overflow is a full `analyze_procedure` response in run-0047, so analysis budgeting remains necessary. The Grep error is a guessed nonexistent People.sql path in run-0020. Run-0028's two revision failures were caused by the model dropping the `::Workspace` suffix from both supplied `expectedRevision` strings; source had not changed. Duplicate cross-project identities still caused fallback in this pre-scope candidate. These outcomes motivate scoped/direct selectors and smaller analysis defaults, without attributing every failure to the parser.

The reviewer read all 48 new answers against the frozen gold and source evidence and supplied new explicit per-criterion scores/notes; historical grades were not copied. Review is by the implementing agent, not an independent/blinded reviewer. Equal strict passes and a small factual difference do not demonstrate quality superiority. Provider/OS caching, baseline-first order, competing host load, related questions and two repeats limit inference. No LSP or Skill calls occurred, so this cannot establish an LSP-specific benefit.

All raw/derived local evidence is retained in `artifacts/sample-study-r1-corrected-20261017`: `rerun-plan.json`, frozen inputs, `runs/metrics.csv`, every answer/transcript, `grades.json`, `reviewed-results.csv/json`, `report.md`, `client-failures.json` and `baseline-comparison.md/json`. The original 0.14.0 artifacts/evidence remain unchanged. Use the fresh matched Grep arm as primary control; the historical comparison is descriptive.

Packaging audit: this earlier candidate included maintainer benchmark docs/rubrics. No reads of those files were observed in restricted transcripts, but their presence is a contamination risk. The clean post-repair artifact is evaluated separately; do not treat these quality scores as an independent holdout.
