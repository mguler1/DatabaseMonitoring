using DatabaseMonitoring.Web.Models;

namespace DatabaseMonitoring.Web.Interfaces;

public interface IDatabaseProvider
{
    string ProviderType { get; }
    string DisplayName { get; }

    Task<IEnumerable<DatabaseInfo>> GetDatabasesAsync();
    Task<DatabaseInfo?> GetDatabaseInfoAsync(string databaseName);
    Task<IEnumerable<TableInfo>> GetTablesAsync(string databaseName);
    Task<IEnumerable<QueryInfo>> GetRecentQueriesAsync(string databaseName, int top = 25);
    Task<IEnumerable<QueryInfo>> GetTableQueriesAsync(string databaseName, string tableName, int top = 10);
}
