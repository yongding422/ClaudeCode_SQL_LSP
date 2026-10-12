# Online SQL Server setup

Pass this nonsecret configuration in LSP `initialize.initializationOptions`:

```json
{
  "sql": { "mode": "online", "parserVersion": "170" },
  "databases": [{
    "id": "approved-dev",
    "enabled": true,
    "environment": "development",
    "server": "localhost,1433",
    "database": "PayrollDev",
    "connectionStringEnv": "SQL_LSP_DEV_CONNECTION",
    "readOnly": true,
    "schemas": ["dbo", "sales"],
    "connectTimeoutSeconds": 5,
    "commandTimeoutSeconds": 5,
    "loadTimeoutSeconds": 20,
    "cacheTtlSeconds": 300
  }],
  "resolution": { "defaultDatabaseProfile": "approved-dev" }
}
```

Set the environment variable through your local secret management workflow before launching the client/server. Its value is a SqlClient connection string with Server and Database exactly matching the approved profile (server comparison ignores case; database comparison is exact). Do not commit or print its value. Encryption defaults to mandatory; `Encrypt=False` is rejected. Production connections should validate a trusted certificate. `TrustServerCertificate=True` is intended only for explicitly approved development fixtures with self-signed certificates. Windows integrated authentication depends on the host configuration; SQL authentication is exercised by the live fixture. Entra authentication extension packages/configuration are not included or verified in M3.

Profiles default to disabled. Offline mode never reads their secrets or opens a database connection. Online mode requires an explicitly enabled profile; with several enabled profiles, defaultDatabaseProfile must select one. M3 contacts only that selected profile. No environment label implies approval automatically: enable only targets you have approved. Cross-database/server resolution, multiple mapped targets and hybrid source precedence are M4 work.

Online mode parses opened buffers for syntax/local bindings and query references, but does not scan repository files or treat their declarations as online global objects. Set `workspace.scanInOnline=true` only when local indexing is wanted. This option can introduce honest ambiguity between local and deployed declarations; it does not select an editable winner or report drift.

## Permissions and acquisition

Use a dedicated metadata account. Login/database CONNECT is needed, with metadata visibility granted only for approved objects/schemas. Database VIEW DEFINITION exposes permitted module text; catalog dependency retrieval additionally needs SELECT on sys.sql_expression_dependencies and the documented database metadata permission. Provisioning grants is an administrator action outside this LSP. For an already provisioned metadata user, an administrator may review this example:

```sql
GRANT CONNECT TO [lsp_metadata];
GRANT VIEW DEFINITION TO [lsp_metadata];
GRANT SELECT ON sys.sql_expression_dependencies TO [lsp_metadata];
```

These grants are optional scope choices, not a demand for broad access. Without broad metadata permissions, only visible objects are discovered; hidden module text remains unavailable. Denied dependency access produces partial status while retaining other metadata. ApplicationIntent=ReadOnly is a routing hint and does not enforce authorization. The provider has only fixed SELECT catalog queries, no user SQL execution entry point, and never runs analyzed scripts. The disposable fixture alone contains DDL to prepare its own test database.

Objects are bounded with parameterized TOP limits; schema filters are JSON parameter values using OPENJSON (compatibility level 130 or newer). Commands are sequential on one connection, with explicit connect/command timeouts and a total load deadline. Cancellation reaches OpenAsync, ExecuteReaderAsync and ReadAsync. One retry is allowed only for a recognized transient read failure. No expired cache fallback is enabled. Supported configured limits are in the [schema](configuration.schema.json); defaults are 1,000 objects, 1,024 members/dependencies per object, and 1,048,576 characters per module. Hard aggregate caps retain at most 8,388,608 module characters, 65,536 columns/parameters and 65,536 dependencies. Exceeding a cap reports partial coverage; truncated SQL bodies are not parsed as original source.

## Navigation and freshness

Search and navigation use the existing source-aware index. Catalog object identity includes profile/database/schema/name/kind, even when sp_rename left an older name in stored module text. Case/accent/kana/width/binary flags and LCID come from database collation. Optional profile.caseSensitive (or resolution.caseSensitive) overrides this with ordinal matching. The metadata account's default schema and dbo fallback apply to unqualified names; qualify names or set resolution.defaultSchema for a different execution context. This comparison is deliberately bounded in scope and is not complete SQL Server binding.

Visible module text is parsed using the configured parser version and the module's stored QUOTED_IDENTIFIER flag. Tables and inaccessible/encrypted/oversized modules navigate to clearly labeled metadata comment files; original CREATE text is never invented. LSP references are verified AST locations from visible modules/open buffers. Catalog dependencies are acquired separately with caller-dependent/ambiguous flags; they do not supply guaranteed source locations and are not presented as comprehensive LSP references.

Snapshots are ordinary file URIs, tested through the generic wire client. Each process creates a random private directory under the OS temp directory with a current-user-only Windows ACL or Unix mode 0700. Files are read-only (Unix 0400); catalog names never determine paths. Text synchronization cannot overwrite these sources in the index. The owner can still change filesystem permissions: this is an advisory navigation snapshot, not a security boundary against its owner. At most two generations are retained; older URIs expire after subsequent refreshes. Normal shutdown deletes the private tree. Abrupt termination can leave private files; remove only the exact process-owned directory after verifying it is no longer in use.

Refresh occurs on the first navigation/search and on the first subsequent request after TTL, not in the background. A cached snapshot is reused within TTL, so permission revocations or DDL changes may not be reflected until refresh/restart. On failed refresh, no expired catalog results are returned; previously opened private snapshot files may remain until shutdown. window/logMessage reports profile status, capture time, version, collation, object count and coverage codes without credentials or module text. Metadata collection spans multiple SELECT reads and is not a transactional schema snapshot.

## Reproducible live tests

Docker must run Linux x86-64 containers. The fixture uses SQL Server 2022 Developer image digest `sha256:4402d880dd4c34bfa7d8705e56a86cd6c88da80a1f6bbbe741f999e76264a090`, a random loopback port, a generated password and a 2 GiB memory limit. Enabling the tests accepts the image EULA for that disposable fixture. No external database connection string is accepted by the harness. It removes only the container it created.

```powershell
$env:SQLLSP_RUN_SQLSERVER_TESTS = '1'
dotnet test -c Release
Remove-Item Env:SQLLSP_RUN_SQLSERVER_TESTS
```

```sh
SQLLSP_RUN_SQLSERVER_TESTS=1 dotnet test -c Release
```

Five opt-in cases cover real catalogs/types/parameters/modules/dependencies, restricted metadata, encrypted and renamed modules, query limits, parameterized hostile schema filters, cancellation and actual stdio navigation to readable read-only files. Deterministic provider/cache/configuration tests run without Docker. CI includes a separate Linux SQL Server job; local live verification was performed on Windows against Docker's Linux engine. Real Claude Code online initialization/navigation remains an M5 gate.

Permission behavior follows [Microsoft metadata visibility](https://learn.microsoft.com/en-us/sql/relational-databases/security/metadata-visibility-configuration) and [catalog dependency permissions and limits](https://learn.microsoft.com/en-us/sql/relational-databases/system-catalog-views/sys-sql-expression-dependencies-transact-sql).
