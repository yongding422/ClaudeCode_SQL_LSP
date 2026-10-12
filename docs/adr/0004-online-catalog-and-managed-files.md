# ADR 0004: Online catalog acquisition and managed definition files

Date: 2026-10-11. Status: accepted for M3.

## Verified dependencies

Pin Microsoft.Data.SqlClient **7.1.1**, the latest stable package reported by the official NuGet flat-container feed at verification. The [driver lifecycle](https://learn.microsoft.com/en-us/sql/connect/ado-net/sqlclient-driver-support-lifecycle) identifies 7.1 as a supported STS line with .NET 8+ support, including the existing net10.0 application. The lifecycle page still lists 7.1.0 as its patch; the [NuGet package](https://www.nuget.org/packages/Microsoft.Data.SqlClient/7.1.1) and actual restore establish the selected patch. Keep the STS servicing window under review. All dependency assets and hashes are locked. Existing .NET SDK, ScriptDOM, OmniSharp, schema and test package pins remain unchanged.

Compiled code verifies SqlConnectionStringBuilder.Encrypt/ApplicationIntent/ConnectTimeout/ConnectRetryCount, SqlConnection.OpenAsync, SqlCommand.ExecuteReaderAsync, typed SqlParameter values, async reader cancellation and SqlString identifier comparison. This is backed by real SQL Server 2022 catalog and wire tests, not just an API sketch. SQL authentication is verified; other authentication modes are not claimed. Entra extension packages are not part of this slice.

## Source and safety decisions

Use a separate provider project with plain immutable catalog contracts. Only online mode creates the selected enabled provider. Configuration contains environment variable names, approved server/database, environment label and readOnly=true; inline credentials and unknown fields are rejected. A multi-profile configuration requires explicit selection, and unused profiles are never contacted. Hybrid mapping remains M4.

Use fixed SELECT statements with bounded TOP parameters and an OPENJSON schema-filter parameter. Aggregate text/member/dependency budgets, sequential acquisition, explicit timeouts, total-load cancellation and one transient-read retry bound work. Errors become fixed status codes; exception text and credentials are not logged. TLS is required, with explicit certificate trust settings delegated to the approved connection secret. ApplicationIntent is not treated as an authorization mechanism. A least-privilege account and database-side permissions provide authorization.

Discover objects, columns/types, parameters, available module text and dependencies, recording timestamp/version/collation/default schema. [sys.sql_modules](https://learn.microsoft.com/en-us/sql/relational-databases/system-catalog-views/sys-sql-modules-transact-sql) documents nullable definitions and unchanged module text after rename. The catalog's current identity overrides the parsed declaration identity without rewriting stored source. [Metadata visibility](https://learn.microsoft.com/en-us/sql/relational-databases/security/metadata-visibility-configuration) can hide both rows and text. Null definition does not justify inventing a CREATE statement or claiming encryption as the only possible cause. [sys.sql_expression_dependencies](https://learn.microsoft.com/en-us/sql/relational-databases/system-catalog-views/sys-sql-expression-dependencies-transact-sql) has extra permission requirements, runtime/ambiguity flags and incomplete coverage. Preserve that data separately from AST reference ranges.

## File URIs and cache policy

Use managed ordinary file URIs because no client-specific virtual URI capability has been verified. Create a private current-user directory, random generation directories and numeric object-id filenames; retrieved names are not paths. Files contain provenance/time plus permitted source, or clearly labeled metadata comments when original source is unavailable. Restrict writes with readonly attributes/Unix modes, ignore text synchronization mutations to managed sources, bound retained generations and delete owned files on normal shutdown. The generic process client verifies existence, source content, range navigation, module references, immutable outlines and shutdown removal. Claude behavior is still a separate M5 gate.

Cache only complete acquisition results (which may explicitly report partial coverage) within TTL. Expired refresh failure removes catalog results from navigation; no stale fallback is silently enabled. Query cancellation reaches the provider and canceled acquisitions are never cached. Metadata reads are not a transactional DDL snapshot, and permission changes may remain cached within TTL. Read-only flags are advisory for the owner, and force termination can leave private files. Document these limits instead of claiming complete isolation or perfect freshness.

## Integration environment

No approved existing server was available. Docker Linux was available, so the harness creates a dedicated SQL Server 2022 Developer container with an exact image digest from the pull, generated credentials, loopback-only random port and limited memory. Only the harness seeds DDL, always inside its own container, and cleans up on setup failure/normal completion. The [official Docker quickstart](https://learn.microsoft.com/en-us/sql/linux/install-upgrade/quickstart-install-docker?view=sql-server-ver16) supplies the supported container setup. Default tests skip live cases; an opt-in local/CI run exercises the full M3 gate.
