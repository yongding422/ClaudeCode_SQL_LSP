using Microsoft.Data.SqlClient;
using SqlLsp.Core;

namespace SqlLsp.SqlServer.IntegrationTests;

// Small executable counterexamples, independent of the sample corpus and model answers.
// Only the test-owned disposable SQL Server executes these scripts; the plugin never does.
[Collection("Disposable SQL Server")]
public class ClaimVerificationTests(SqlServerFixture fixture)
{
    [Theory]
    [InlineData("update-scope")]
    [InlineData("select-initialization")]
    [InlineData("nested-transaction")]
    [InlineData("json-null")]
    public async Task Verification_examples_parse_as_supported_sql(string name)
    {
        var parsed = await new SqlParser(new(), new()).ParseAsync(new SourceText(await Source(name)), CancellationToken.None);
        Assert.Empty(parsed.Diagnostics);
    }

    [SqlServerFact]
    public Task Update_without_order_filter_changes_an_unrequested_distinct_item() => Verify("update-scope",
        ("BroadOtherOrder", 120), ("ScopedRequestedOrder", 110), ("ScopedOtherOrder", 100));

    [SqlServerFact]
    public Task Zero_row_select_distinguishes_fresh_null_from_initialized_value() => Verify("select-initialization",
        ("FreshNoMatch", null), ("InitializedNoMatch", 99));

    [SqlServerFact]
    public Task Inner_commit_does_not_commit_caller_transaction() => Verify("nested-transaction",
        ("AfterInnerCommit", 1), ("AfterOuterRollback", 0), ("PersistedChanges", 0));

    [SqlServerFact]
    public Task Root_status_comparison_does_not_find_nested_failed_delivery() => Verify("json-null",
        ("RootStatusMissing", 1), ("RootNotDeliveredMatches", 0), ("EventNotDeliveredMatches", 1), ("DeliveryTimePresent", 1));

    private async Task Verify(string name, params (string Name, int? Value)[] expected)
    {
        await using var connection = new SqlConnection(fixture.CatalogConnection);
        await connection.OpenAsync();
        await using var command = new SqlCommand(await Source(name), connection) { CommandTimeout = 15 };
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(expected.Length, reader.FieldCount);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].Name, reader.GetName(i));
            Assert.Equal(expected[i].Value, reader.IsDBNull(i) ? (int?)null : reader.GetInt32(i));
        }
        Assert.False(await reader.ReadAsync());
        Assert.False(await reader.NextResultAsync());
    }

    private static Task<string> Source(string name) => File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", name + ".sql"));
}
