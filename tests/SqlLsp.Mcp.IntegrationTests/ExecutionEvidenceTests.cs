namespace SqlLsp.Mcp.IntegrationTests;

public class ExecutionEvidenceTests
{
    [Fact]
    public async Task Brief_relates_pretransaction_throw_to_try_region_and_exposes_transaction_contract()
    {
        await using var client = await McpClient.Start("CREATE PROC dbo.P @bad bit AS BEGIN BEGIN TRY IF @bad=1 THROW 51000,N'bad',1; BEGIN TRAN; COMMIT; END TRY BEGIN CATCH IF XACT_STATE()<>0 ROLLBACK; THROW; RETURN -1; END CATCH END;");
        var data = McpClient.Data(await client.Tool("analyze_procedure", new { documentUri = new Uri(client.SourceFile).AbsoluteUri, declaration = "P", intent = "transactions" }));
        var facts = data.GetProperty("brief").GetProperty("facts").EnumerateArray().ToArray();
        var contract = Assert.Single(facts, f => f.GetProperty("kind").GetString() == "TransactionContract");
        Assert.Equal(1, contract.GetProperty("data").GetProperty("transactions").GetProperty("explicitBegins").GetInt32());
        var before = Assert.Single(facts, f => f.GetProperty("kind").GetString() == "Throw" &&
            f.GetProperty("sourceOrder").GetProperty("beforeFirstExplicitBegin").ValueKind == System.Text.Json.JsonValueKind.True);
        Assert.Contains(before.GetProperty("sourceOrder").GetProperty("regions").EnumerateArray(), r => r.GetProperty("kind").GetString() == "Try");
        Assert.Contains(before.GetProperty("sourceOrder").GetProperty("regions").EnumerateArray(), r => r.GetProperty("kind").GetString() == "Then" && r.GetProperty("predicate").GetProperty("preview").GetString() == "@bad=1");
    }

    [Fact]
    public async Task Compact_retains_local_table_and_dynamic_construction_evidence_through_wire_pages()
    {
        await using var client = await McpClient.Start("CREATE PROC dbo.P AS BEGIN DECLARE @sql nvarchar(max),@n int; CREATE TABLE #value(v int); SET @sql=N'INSERT #value SELECT 1;'; EXEC(@sql); SET @n=(SELECT v FROM #value); END;");
        var data = McpClient.Data(await client.AnalyzeExpanded(new { documentUri = new Uri(client.SourceFile).AbsoluteUri, declaration = "P", intent = "dependencies" }));
        var report = data.GetProperty("report");
        var observations = report.GetProperty("modules")[0].GetProperty("observations").EnumerateArray().ToArray();
        var read = Assert.Single(observations, o => o.GetProperty("kind").GetString() == "LocalTableRead");
        var input = Assert.Single(read.GetProperty("inputs").EnumerateArray());
        Assert.Equal("#value", input.GetProperty("name").GetString());
        Assert.True(input.GetProperty("verifiedLocal").GetBoolean());
        var evidence = input.GetProperty("at").GetString()!;
        Assert.Contains("modules.sql:1", report.GetProperty("evidence").GetProperty(evidence).GetProperty("text").GetString());
        Assert.Contains(observations, o => o.GetProperty("kind").GetString() == "DynamicSqlConstruction");
        Assert.DoesNotContain(observations, o => o.GetProperty("kind").GetString() == "LocalTableTransfer");
    }
}
