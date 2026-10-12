# Measured performance

M5 baseline, 2026-10-11 local date (2026-10-12 UTC): Windows build 26200, Intel Core i7-7700K at 4.20 GHz, 4 cores/8 logical processors, 63.9 GiB RAM, x64, .NET 10.0.12, Release build. These are single-run synthetic workload measurements on the development host, not production SLAs. [Raw standalone results](evidence/performance-windows.json) contain exact values and sample counts.

| Real stdio workload | Standalone result | Plan target |
| --- | --- | --- |
| Process launch and initialize, 27 public SQL files | 1,133 ms | <2,000 ms |
| 200,000 UTF-8 byte full-buffer replacements to syntax diagnostics, 30 edits | p50 126 ms; p95 292 ms | p95 <300 ms |
| Index 1,001 files | 2,119 ms; 472 files/s | Measure |
| Warm definition in 1,001 files, 50 requests | p50 12 ms; p95 18 ms | p95 <100 ms |
| Server working set after 1,001-file queries | 88.5 MiB | Measure |
| Index 10,001 files | 9,471 ms; 1,056 files/s | Measure |
| Warm definition in 10,001 files, 50 requests | p50 59 ms; p95 123 ms | Measure |
| Server working set after 10,001-file queries | 201.6 MiB | Measure |

The 200KB procedure contains 3,500 real SELECT statements with parameter/table references, plus padding. Each measured edit replaces the full buffer; parsing is warmed by its first open. Large-index fixtures contain one table per file and one referencing query. Warm navigation excludes the first binding/JIT request. Index time includes process startup, filesystem reads and ScriptDOM parsing. The benchmark asserts correct diagnostics/definition locations and clean shutdown; it reports timing targets without flaky timing failures.

An initial run alongside the full integration suite and disposable SQL Server measured startup 1,971 ms, 200KB diagnostic p95 **487 ms**, 1,001-file definition p95 41 ms and 10,001-file definition p95 139 ms. That contended diagnostic measurement missed the 300 ms target. The standalone run meets it narrowly; do not promise it under load. Memory is sampled working set after queries, not a peak, managed allocation total or enforced ceiling. Hybrid/catalog comparison, complex modules, references and slow/network filesystems need their own larger-workload profiling before performance guarantees.

Reproduce the opt-in measurement after building:

```powershell
$env:SQLLSP_RUN_BENCHMARKS = '1'
dotnet test tests/SqlLsp.Server.IntegrationTests --no-build --no-restore -c Release --filter FullyQualifiedName~PerformanceTests
Remove-Item Env:SQLLSP_RUN_BENCHMARKS
```

Results go to `artifacts/performance.json`. Avoid running other tests/builds simultaneously for the standalone comparison. The benchmark creates/deletes only its own synthetic temp workspaces and launches the actual server over framed stdio. It can also target an extracted apphost with `SQLLSP_TEST_SERVER_EXE`. It requires no database.

The default suite separately exercises 200 file create/delete cycles, rapid edits across 16 documents/two roots, 32 concurrent wire requests, cancellation/refresh races and shutdown. Completed disk jobs are removed; small disk reads use pooled buffers; at most four ScriptDOM parses run simultaneously. The index remains in memory and offline navigation currently rebuilds its symbol view per request. Ten-thousand-file initialization exceeds the small-workspace startup target, so client startup timeouts must account for workspace size.
