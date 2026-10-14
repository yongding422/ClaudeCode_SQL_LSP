# Measured performance

## M6 hover / patched package measurement (2026-10-13)

The same Windows host exercised the extracted 0.6.0 self-contained **.NET 10.0.13** apphost with PKCS 9.0.21. [Raw results](evidence/performance-m6-packaged-windows.json) report the **test harness** runtime (10.0.12), not the apphost runtime, whose runtimeconfig/deps were checked separately. Small-workspace startup was **1,312 ms**, 200KB diagnostics p95 **345 ms** (above the 300 ms target), and 1,001-file warm definition p95 **13 ms**. Indexing 1,001 files took 6.79 s; 10,001 files took **58.41 s**, sampled working set 210.8 MiB, warm definition p95 181 ms.

A preceding packaged run exceeded its 60-second large-workspace initialization deadline; the traced repeat completed near that deadline. Large-index startup is not consistently within that timeout on this host. The earlier source-build M6 run measured 10,001-file indexing at 10.75 s and diagnostic p95 449 ms; [its report](evidence/performance-m6-before-security-patch.json) is retained. Startup timing variability remains unresolved and needs filesystem/runtime/CPU profiling before larger-workspace guarantees. The recorded retry is not evidence that the timeout problem is fixed. Initialization scans are currently sequential; clients should use a suitable large-workspace deadline or narrower roots. No wall-clock threshold is hidden by changing assertions or raising the measured target.

Functional/performance fixtures verify result correctness independently of timing. Real Claude hover returned the selected payroll signature/source in 146 ms in the final patched probe; that small public workspace does not establish large-workspace hover latency.

## Historical M5 baseline

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
