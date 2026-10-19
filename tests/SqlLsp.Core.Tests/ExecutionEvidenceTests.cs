using SqlLsp.Core;

namespace SqlLsp.Core.Tests;

public class ExecutionEvidenceTests
{
    private static async Task<(DocumentSnapshot Document, SqlModuleAnalysis Module)> Analyze(string sql, bool parseErrors = false)
    {
        var document = new DocumentSnapshot(new Uri("file:///execution/test.sql"), null, SqlSourceKind.Workspace,
            await new SqlParser(new()).ParseAsync(new SourceText(sql), CancellationToken.None));
        if (!parseErrors) Assert.Empty(document.Parse.Diagnostics);
        else Assert.NotEmpty(document.Parse.Diagnostics);
        var index = new SqlNavigationIndex([document], _ => "test", new());
        var selected = index.Symbols.Single(s => s.Identity.Name == "P");
        return (document, index.AnalyzeProcedure(document.Uri, selected.Target.Range.Start, CancellationToken.None).Modules[0]);
    }

    [Fact]
    public async Task No_explicit_boundary_is_caller_dependent_and_parse_recovery_never_proves_absence()
    {
        var (_, module) = await Analyze("CREATE PROC dbo.P AS UPDATE dbo.T SET id=1;");
        var contract = Assert.Single(module.Observations, o => o.Kind == "TransactionContract");
        Assert.Equal("CallerDependent", contract.Name);
        Assert.True(contract.Transactions!.SyntaxComplete);
        Assert.Equal(0, contract.Transactions.ExplicitBegins);
        Assert.Contains("require autocommit with no enclosing", contract.Detail);
        var (_, partial) = await Analyze("CREATE PROC dbo.P AS SELECT 1;\nGO\nSELECT FROM;", true);
        var unknown = Assert.Single(partial.Observations, o => o.Kind == "TransactionContract");
        Assert.Equal("Unknown", unknown.Name);
        Assert.False(unknown.Transactions!.SyntaxComplete);
    }

    [Fact]
    public async Task Native_options_and_isolation_keep_exact_original_source()
    {
        var (document, module) = await Analyze("CREATE PROC dbo.P WITH NATIVE_COMPILATION, SCHEMABINDING, EXECUTE AS OWNER AS BEGIN ATOMIC WITH(TRANSACTION ISOLATION LEVEL=SNAPSHOT,LANGUAGE=N'us_english') SELECT 1; END;");
        var contract = Assert.Single(module.Observations, o => o.Kind == "TransactionContract");
        Assert.Equal("AtomicBlock", contract.Name);
        Assert.Equal(1, contract.Transactions!.AtomicBlocks);
        Assert.Contains("savepoint", contract.Detail);
        Assert.Contains("sees its own writes", contract.Detail);
        var options = Assert.Single(module.Observations, o => o.Kind == "DeclaredExecutionOptions").Options;
        Assert.Equal(3, options.Length);
        Assert.Contains(options, o => o.Source.Preview == "NATIVE_COMPILATION");
        Assert.Contains(options, o => o.Source.Preview == "SCHEMABINDING");
        Assert.Contains(options, o => o.Source.Preview == "EXECUTE AS OWNER");
        Assert.Contains(module.Observations, o => o.Kind == "AtomicOption" && o.Expression!.Preview.Contains("SNAPSHOT"));
        foreach (var option in options)
        {
            var range = option.Source.Location.Range;
            Assert.Equal(option.Source.Preview, document.Parse.Source.Text[document.Parse.Source.OffsetAt(range.Start)..document.Parse.Source.OffsetAt(range.End)]);
            Assert.False(option.Source.Truncated);
        }
    }

    [Fact]
    public async Task Explicit_statement_counts_do_not_assert_balance_or_runtime_paths()
    {
        var (_, module) = await Analyze("CREATE PROC dbo.P @n int AS BEGIN IF @n=1 BEGIN TRAN; IF @n=2 COMMIT; IF XACT_STATE()<>0 ROLLBACK; END;");
        var contract = Assert.Single(module.Observations, o => o.Kind == "TransactionContract");
        Assert.Equal("ExplicitStatements", contract.Name);
        Assert.Equal(new SqlTransactionSyntax(1, 1, 1, 0, true), contract.Transactions);
        Assert.Contains("Counts/source order do not establish", contract.Detail);
    }

    [Theory]
    [InlineData("EXECUTE /* principal */ AS OWNER")]
    [InlineData("EXECUTE AS 'a]quoted user'")]
    public async Task Execute_as_token_extent_preserves_comments_and_quoted_principals(string clause)
    {
        var (_, module) = await Analyze("CREATE PROC dbo.P WITH " + clause + " AS SELECT 1;");
        var options = Assert.Single(module.Observations, o => o.Kind == "DeclaredExecutionOptions").Options;
        Assert.Equal(clause, Assert.Single(options).Source.Preview);
        Assert.False(options[0].Source.Truncated);
    }

    [Fact]
    public async Task Local_inserts_and_reads_link_source_syntax_without_table_content_lineage()
    {
        var (_, module) = await Analyze("CREATE PROC dbo.P AS BEGIN CREATE TABLE #stage(id int); DECLARE @rows TABLE(id int); DECLARE @n int; " +
            "INSERT #stage SELECT id FROM dbo.Source; INSERT @rows SELECT id FROM #stage; SET @n=(SELECT MAX(id) FROM @rows); " +
            "SELECT TOP 1 @n=id FROM #stage; END;");
        var transfers = module.Observations.Where(o => o.Kind == "LocalTableTransfer").ToArray();
        Assert.Equal(2, transfers.Length);
        Assert.Contains(transfers, o => o.Name == "#stage" && o.Inputs.Any(i => i.Name == "[dbo].[Source]" && !i.VerifiedLocal));
        Assert.Contains(transfers, o => o.Name == "@rows" && o.Inputs.Any(i => i.Name == "#stage" && i.VerifiedLocal));
        var reads = module.Observations.Where(o => o.Kind == "LocalTableRead").ToArray();
        Assert.Equal(2, reads.Length);
        Assert.All(reads, o => Assert.Equal("@n", o.Name));
        Assert.Contains(reads, o => o.Inputs.Any(i => i.Kind == "TableVariable" && i.VerifiedLocal));
        Assert.Contains(reads, o => o.Inputs.Any(i => i.Kind == "TempTable" && i.VerifiedLocal));
        Assert.All(reads, o => Assert.Contains("not table contents", o.Detail));
    }

    [Fact]
    public async Task Dynamic_string_construction_and_local_table_read_do_not_invent_a_dynamic_write()
    {
        var (_, module) = await Analyze("CREATE PROC dbo.P @schema sysname AS BEGIN DECLARE @sql nvarchar(max),@n int; CREATE TABLE #result(v int); " +
            "SET @sql=N'INSERT #result SELECT MAX(v) FROM '+QUOTENAME(@schema)+N'.T;'; EXEC(@sql); SET @n=(SELECT v FROM #result); DROP TABLE #result; END;");
        var construction = Assert.Single(module.Observations, o => o.Kind == "DynamicSqlConstruction");
        Assert.Equal("@sql", construction.Name);
        Assert.Contains("INSERT #result", construction.Expression!.Preview);
        Assert.Contains("not parsed dynamic SQL", construction.Detail);
        Assert.Single(module.Observations, o => o.Kind == "DynamicExecution");
        Assert.DoesNotContain(module.Observations, o => o.Kind == "LocalTableTransfer");
        var read = Assert.Single(module.Observations, o => o.Kind == "LocalTableRead");
        Assert.Equal("@n", read.Name);
        Assert.Equal("#result", Assert.Single(read.Inputs).Name);
    }

    [Fact]
    public async Task Non_executed_string_and_dropped_or_persistent_relations_do_not_gain_local_binding()
    {
        var (_, module) = await Analyze("CREATE PROC dbo.P AS BEGIN DECLARE @msg nvarchar(max),@n int; SET @msg=N'INSERT #result SELECT 1;'; " +
            "CREATE TABLE #result(v int); DROP TABLE #result; SET @n=(SELECT v FROM #result); SET @n=(SELECT v FROM dbo.Persistent); PRINT @msg; END;");
        Assert.DoesNotContain(module.Observations, o => o.Kind is "DynamicSqlConstruction" or "DynamicExecution" or "LocalTableRead");
    }

    [Fact]
    public async Task Literal_EXEC_text_is_not_echoed_as_an_execution_preview()
    {
        var (_, module) = await Analyze("CREATE PROC dbo.P AS EXEC(N'PRIVATE_SQL_CANARY');");
        var execution = Assert.Single(module.Observations, o => o.Kind == "DynamicExecution");
        Assert.Null(execution.Expression);
        Assert.Equal("TextOmitted", execution.Operation);
        Assert.DoesNotContain("PRIVATE_SQL_CANARY", System.Text.Json.JsonSerializer.Serialize(module.Observations));
    }

    [Fact]
    public async Task Relation_input_limit_keeps_explicit_incompleteness()
    {
        var (_, module) = await Analyze("CREATE PROC dbo.P AS BEGIN CREATE TABLE #t(id int); DECLARE @n int; SET @n=(SELECT COUNT(*) FROM #t t " +
            string.Concat(Enumerable.Range(0, 18).Select(i => "CROSS JOIN dbo.T p" + i + " ")) + "); END;");
        var read = Assert.Single(module.Observations, o => o.Kind == "LocalTableRead");
        Assert.Equal(16, read.Inputs.Length);
        Assert.False(read.InputsComplete);
        Assert.True(module.Truncated);
    }

    [Fact]
    public async Task Many_local_transfers_are_bounded_with_visible_truncation()
    {
        var (_, module) = await Analyze("CREATE PROC dbo.P AS BEGIN DECLARE @rows TABLE(id int); " +
            string.Concat(Enumerable.Repeat("INSERT @rows VALUES(1);", 80)) + " END;");
        Assert.True(module.Truncated);
        Assert.InRange(module.Observations.Length, 1, 64);
        Assert.Contains(module.Issues, i => i.Code == "observation-limit");
        Assert.Contains(module.Observations, o => o.Kind == "TransactionContract");
    }
}
