# Verifying answers without knowing the sample project

A developer does not need to certify unfamiliar sample business rules. Keep three separate results: executable SQL checks, source-grounded answer review, and practical developer acceptance. No developer acceptance or correction-time score is recorded when that feedback is unavailable.

## Executable counterexamples

The fixtures below have small, explicit inputs and asserted results. They run only in the test harness's disposable SQL Server, never through the plugin or against a user database. SQL Server CI already owns this container; no connection string is required from the developer.

| Fixture | Known input | Expected result |
| --- | --- | --- |
| [update-scope.sql](../../tests/SqlLsp.SqlServer.IntegrationTests/Fixtures/update-scope.sql) | Two different stock items start at 100; orders 1 and 2 supply 10 and 20 units; only order 1 is requested | The broad join changes order 2's item to 120. Adding the order predicate leaves that item at 100 and changes the requested item to 110. Each item has one source match; this does not rely on ambiguous multi-match behavior. |
| [select-initialization.sql](../../tests/SqlLsp.SqlServer.IntegrationTests/Fixtures/select-initialization.sql) | A fresh scalar and a scalar initialized to 99; neither SELECT finds a row | Fresh scalar remains NULL; initialized scalar remains 99. Retention alone does not establish a stale ID. |
| [nested-transaction.sql](../../tests/SqlLsp.SqlServer.IntegrationTests/Fixtures/nested-transaction.sql) | Outer transaction inserts one row; inner BEGIN/COMMIT inserts another | Transaction count after inner COMMIT is 1; outer rollback leaves count 0 and no rows. The inner COMMIT was not an independent durable commit. |
| [json-null.sql](../../tests/SqlLsp.SqlServer.IntegrationTests/Fixtures/json-null.sql) | Failure status inside `Events[0]`, root delivery timestamp present | Root status is NULL; root `<> 'Delivered'` finds no match, event-path comparison does. Timestamp presence can coexist with the failed event. |

[ClaimVerificationTests](../../tests/SqlLsp.SqlServer.IntegrationTests/ClaimVerificationTests.cs) checks column names, all expected values and absence of extra rows/results. Offline tests also require all four scripts to parse successfully with ScriptDOM. A skipped live test is not proof of execution.

Run the existing disposable-container tests on a machine with Docker:

```powershell
$env:SQLLSP_RUN_SQLSERVER_TESTS = '1'
dotnet test tests/SqlLsp.SqlServer.IntegrationTests -c Release --filter FullyQualifiedName~ClaimVerificationTests
```

The CI `sqlserver` job runs these checks automatically. The fixtures show concrete counterexamples to unsafe generalizations. They do not execute the full sample procedures or establish production data, concurrency, permissions, business intent, or every possible UPDATE join plan.

## Reviewing a benchmark answer

Preserve the frozen task and rubric. For each consequential answer claim, retain its source file/revision and exact lines, the extracted fact or source interpretation supporting it, and any unresolved assumptions. Check relevant joins/predicates, initialization, constraints/computed columns, JSON paths and transaction ownership. Mark omissions and unsupported claims even if the tool call succeeded. A runnable counterexample can refute a general SQL claim; a passing fixture alone cannot validate an entire natural-language answer.

Keep implementing-agent review explicitly labeled. A separate reviewer can assess the answer using a packet containing the question, pinned source excerpts, expected facts and these fixture results, without knowing the whole repository. That is reviewer acceptance, not direct user acceptance. Do not treat another model's agreement or matching keywords as independent proof.

For practical acceptance, use a small procedure from a project the developer knows, with an expected behavior/change question agreed before running both arms. Developer feedback on that familiar task can remain optional; unknown feedback must not reduce a score to zero or inflate it to a pass. Report verified facts, critical errors, evidence support and reviewer judgments separately from actual user acceptance, latency and tokens.
