using System.Diagnostics;
using Microsoft.Data.SqlClient;
using SqlLsp.Providers.SqlServer;

namespace SqlLsp.SqlServer.IntegrationTests;

public sealed class SqlServerFactAttribute : FactAttribute
{
    public SqlServerFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SQLLSP_RUN_SQLSERVER_TESTS") != "1")
            Skip = "Opt-in disposable Docker SQL Server tests; set SQLLSP_RUN_SQLSERVER_TESTS=1.";
    }
}

[CollectionDefinition("Disposable SQL Server")]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>;

// DDL exists exclusively in this test harness, always against its own ephemeral container.
public sealed class SqlServerFixture : IAsyncLifetime
{
    public const string Image = "mcr.microsoft.com/mssql/server@sha256:4402d880dd4c34bfa7d8705e56a86cd6c88da80a1f6bbbe741f999e76264a090";
    private readonly string container = "sql-lsp-test-" + Guid.NewGuid().ToString("N");
    private readonly string password = "Lsp!" + Guid.NewGuid().ToString("N");
    private bool created;
    public string Endpoint { get; private set; } = "";
    public string CatalogConnection { get; private set; } = "";
    public string LimitedConnection { get; private set; } = "";
    public SqlServerProfile Profile => new("docker-test", "SQLLSP_FIXTURE_CATALOG", "disposable-test", Endpoint,
        "SqlLspFixture", Enabled: true, LoadTimeoutSeconds: 30);

    public async Task InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable("SQLLSP_RUN_SQLSERVER_TESTS") != "1") return;
        try
        {
            await DockerAsync(["run", "--name", container, "--label", "sqlscriptdom-lsp.fixture=true",
                "-e", "ACCEPT_EULA=Y", "-e", "MSSQL_SA_PASSWORD", "-e", "MSSQL_PID=Developer",
                "-p", "127.0.0.1::1433", "--memory", "2g", "-d", Image], password);
            created = true;
            var binding = (await DockerAsync(["port", container, "1433/tcp"])).Trim();
            Endpoint = "127.0.0.1," + binding.Split(':')[^1];
            var admin = new SqlConnectionStringBuilder
            {
                DataSource = Endpoint,
                InitialCatalog = "master",
                UserID = "sa",
                Password = password,
                Encrypt = SqlConnectionEncryptOption.Mandatory,
                TrustServerCertificate = true,
                ConnectTimeout = 2,
                ConnectRetryCount = 0,
                Pooling = false
            };
            using var readiness = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            while (true)
            {
                readiness.Token.ThrowIfCancellationRequested();
                try
                {
                    await using var connection = new SqlConnection(admin.ConnectionString);
                    await connection.OpenAsync(readiness.Token);
                    break;
                }
                catch (SqlException) { await Task.Delay(500, readiness.Token); }
            }
            await ExecuteAsync(admin.ConnectionString, "CREATE DATABASE SqlLspFixture COLLATE Latin1_General_100_CI_AS;");
            await ExecuteAsync(admin.ConnectionString, $"CREATE LOGIN lsp_catalog WITH PASSWORD='{password}'; CREATE LOGIN lsp_limited WITH PASSWORD='{password}';");
            admin.InitialCatalog = "SqlLspFixture";
            string[] batches =
            [
                "CREATE TABLE dbo.Payroll (PayrollId int NOT NULL PRIMARY KEY, Note nvarchar(40) NULL, Amount decimal(12,2) NOT NULL);",
                "CREATE SCHEMA sales;",
                "CREATE TABLE sales.Payroll (PayrollId int NULL);",
                "CREATE PROCEDURE dbo.ProcessPayroll @PayrollId int, @Note nvarchar(40) = NULL, @Result int OUTPUT AS BEGIN SELECT * FROM dbo.Payroll WHERE PayrollId=@PayrollId; SET @Result=1; END;",
                "CREATE PROCEDURE dbo.RunPayroll AS BEGIN DECLARE @result int; EXEC dbo.ProcessPayroll @PayrollId=42,@Result=@result OUTPUT; END;",
                "CREATE VIEW dbo.PayrollView AS SELECT PayrollId, Amount FROM dbo.Payroll;",
                "CREATE FUNCTION dbo.PayrollTotal(@id int) RETURNS decimal(12,2) AS BEGIN RETURN (SELECT Amount FROM dbo.Payroll WHERE PayrollId=@id); END;",
                "CREATE PROCEDURE dbo.EncryptedPayroll WITH ENCRYPTION AS SELECT PayrollId FROM dbo.Payroll;",
                "CREATE PROCEDURE dbo.OriginalName AS SELECT * FROM dbo.Payroll;",
                "EXEC sys.sp_rename 'dbo.OriginalName', 'RenamedPayroll';",
                "CREATE USER lsp_catalog FOR LOGIN lsp_catalog; GRANT CONNECT TO lsp_catalog; GRANT VIEW DEFINITION TO lsp_catalog; GRANT SELECT ON sys.sql_expression_dependencies TO lsp_catalog;",
                "CREATE USER lsp_limited FOR LOGIN lsp_limited; GRANT CONNECT TO lsp_limited; GRANT EXECUTE ON dbo.ProcessPayroll TO lsp_limited;"
            ];
            foreach (var batch in batches) await ExecuteAsync(admin.ConnectionString, batch);
            admin.Pooling = true;
            admin.UserID = "lsp_catalog"; CatalogConnection = admin.ConnectionString;
            admin.UserID = "lsp_limited"; LimitedConnection = admin.ConnectionString;
            Environment.SetEnvironmentVariable(Profile.ConnectionStringEnv, CatalogConnection);
        }
        catch
        {
            if (created) { await DockerAsync(["rm", "-f", container]); created = false; }
            throw;
        }
    }

    private static async Task ExecuteAsync(string connectionString, string fixtureSql)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(fixtureSql, connection) { CommandTimeout = 15 };
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> DockerAsync(string[] arguments, string? password = null)
    {
        var start = new ProcessStartInfo("docker")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        if (password != null) start.Environment["MSSQL_SA_PASSWORD"] = password;
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Docker could not start.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(3));
        if (process.ExitCode != 0) throw new InvalidOperationException("Docker fixture operation failed: " + await error);
        return await output;
    }

    public async Task DisposeAsync()
    {
        Environment.SetEnvironmentVariable(Profile.ConnectionStringEnv, null);
        SqlConnection.ClearAllPools();
        if (created) await DockerAsync(["rm", "-f", container]);
    }
}
