using DatabaseMonitoring.Web.Models;
using DatabaseMonitoring.Web.Providers;

namespace DatabaseMonitoring.Tests;

public class SqlTextHelperTests
{
    [Fact]
    public void TrimQuery_ShortText_ReturnsUnchanged()
    {
        Assert.Equal("SELECT 1", SqlTextHelper.TrimQuery("SELECT 1", 120));
    }

    [Fact]
    public void TrimQuery_LongText_TruncatesWithEllipsis()
    {
        var result = SqlTextHelper.TrimQuery("SELECT * FROM VeryLongTableNameHere", 10);
        Assert.Equal("SELECT * F…", result);
    }

    [Theory]
    [InlineData("SELECT * FROM [dbo].[Users] WHERE Id = 1", "dbo.Users")]
    [InlineData("SELECT * FROM [Users]", "dbo.Users")]
    [InlineData("SELECT u.Id FROM Users u JOIN [sales].[Orders] o ON u.Id = o.UserId", "sales.Orders")]
    public void ExtractPrimaryTable_MsSqlBracketSyntax_ResolvesSchemaAndTable(string sql, string expected)
    {
        Assert.Equal(expected, SqlTextHelper.ExtractPrimaryTable(sql, "dbo"));
    }

    [Theory]
    [InlineData("SELECT * FROM \"public\".\"users\" WHERE id = 1", "public.users")]
    [InlineData("SELECT * FROM users", "public.users")]
    public void ExtractPrimaryTable_PostgresQuoteSyntax_ResolvesSchemaAndTable(string sql, string expected)
    {
        Assert.Equal(expected, SqlTextHelper.ExtractPrimaryTable(sql, "public"));
    }

    [Fact]
    public void ExtractPrimaryTable_NoFromOrJoin_ReturnsNull()
    {
        Assert.Null(SqlTextHelper.ExtractPrimaryTable("SELECT 1", "dbo"));
    }

    [Fact]
    public void BuildLastQueryPerTable_KeepsFirstSeenPerTable_IgnoresBlankQueryText()
    {
        var rows = new List<QueryInfo>
        {
            new() { QueryText = "SELECT * FROM [dbo].[Users]", LastExecutionTime = DateTime.Now },
            new() { QueryText = "UPDATE [dbo].[Users] SET Name = 'x'", LastExecutionTime = DateTime.Now.AddMinutes(-5) },
            new() { QueryText = "", LastExecutionTime = DateTime.Now },
            new() { QueryText = "SELECT * FROM [dbo].[Orders]", LastExecutionTime = DateTime.Now }
        };

        var map = SqlTextHelper.BuildLastQueryPerTable(rows, "dbo");

        Assert.Equal(2, map.Count);
        Assert.Equal("SELECT * FROM [dbo].[Users]", map["dbo.Users"].QueryText);
        Assert.Equal("SELECT * FROM [dbo].[Orders]", map["dbo.Orders"].QueryText);
    }
}
