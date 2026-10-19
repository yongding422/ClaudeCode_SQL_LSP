# Prewarmed index benchmark protocol

Frozen before the user-requested warm-index model run. Preserve all prior cold studies and their grades. This is a separate server-warm ablation, not a multi-turn conversation or persisted-cache restart experiment.

Use the original 12 questions/source-backed rubric, two repeats, two arms (48 fresh model sessions), `deepseek-v4-pro` at the existing provider, low effort, baseline first, seed 42, two workers per phase, 180 seconds and a $0.50 client-reported session cap ($24 total maximum reported cap). Keep the unchanged native Windows x64 0.16.0 candidate (`dc70df8d5b16a06c94ddaa1405d548b876e9e9f9a3bd2ff91a65873f611a9285`). Do not change global plugins, source SQL, credentials or gold. No SQL execution or database connection.

Each question gets a fresh SQL-only copy and model context. Both arms perform an untimed read of all SQL bytes, warming file pages without supplying SQL/model answers. Treatment then launches one owned native MCP process at that exact workspace with explicit offline settings and saved caches disabled. Initialize once and issue two neutral no-match discovery queries: the first must be cold and the second warm, with exactly 1,099 indexed documents. Neither response enters the model context. No question-specific module analysis is precomputed.

An authenticated loopback/stdio relay connects the measured Claude client to that exact owned process. It returns the backend's supported initialize version and capabilities, forwards measured tools unchanged, and records requested/negotiated protocol versions and process identity/cold-state evidence. A client may request a newer version and accept the backend's supported version through normal initialization negotiation. It does not cache tool results or modify the packaged native plugin. The original tool names, instructions, definitions, source visibility, model prompt, answer constraints and candidate plugin directory remain unchanged. This transport change and warm-cache condition are explicit historical mismatches. LSP, if actually used, is separately started by the client and must not be described as prewarmed.

Primary timing starts after verified warmup and includes model/client startup, relay overhead and measured tools. Retain warmup time separately and also report warmup plus measured-question time; snapshot copying is excluded from both arms as in the original study. Model input/output/cache categories and cost remain client-reported; no token savings are imputed from backend timings. Fresh model sessions isolate index reuse from prior answers/model context, so results do not establish multi-turn-session behavior.

Retain failures and timeouts without retries. A failed warmup or broken warm-state verification cannot be labeled a successful warm sample. Preserve invalid setup evidence; repair setup before starting a new labeled study. All new answers need explicit fresh review from a blank template, using the same criterion/citation/unsupported-claim rules. Disclose implementing-agent review, selected related tasks, two repeats, baseline-first order and uncontrolled OS/provider caches/load. Zero errors and a warm backend are not correctness grades or evidence of an LSP-specific advantage.

The controller freezes the original runner/controller, warm controller/relay/report helper, candidate fingerprint and SQL/rubric hashes. The original cold helper remains unchanged. Use new output paths:

```powershell
python scripts/warm-sample-study.py pilot --source D:/Project/sql-server-samples --plugin artifacts/release-candidate-2b37f30/win-x64 --output artifacts/UNIQUE-native-pilot.json
python -m unittest discover -s scripts -p "test_*.py" -q
python scripts/warm-sample-study.py prepare --source D:/Project/sql-server-samples --plugin artifacts/release-candidate-2b37f30/win-x64 --model deepseek-v4-pro --output artifacts/UNIQUE-warm-study
python scripts/warm-sample-study.py run --study artifacts/UNIQUE-warm-study --dry-run
python -u scripts/warm-sample-study.py run --study artifacts/UNIQUE-warm-study
python scripts/warm-sample-study.py grade-template --study artifacts/UNIQUE-warm-study
# Read every answer/source and save newly completed grades.json.
python scripts/warm-sample-study.py report --study artifacts/UNIQUE-warm-study --grades artifacts/UNIQUE-warm-study/grades.json
```
