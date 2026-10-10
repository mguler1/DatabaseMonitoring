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

    Task<IEnumerable<ActiveSession>> GetActiveSessionsAsync();
    Task<IEnumerable<LongRunningQuery>> GetLongRunningQueriesAsync(int thresholdSeconds = 30);
    Task<IEnumerable<BlockingInfo>> GetBlockingQueriesAsync();
    Task<IEnumerable<IndexInfo>> GetIndexInfoAsync(string databaseName);
    Task<IEnumerable<BackupInfo>> GetBackupStatusAsync(string databaseName);
    Task<IEnumerable<ColumnInfo>> GetColumnsAsync(string databaseName, string tableName, string schemaName = "dbo");
    Task<ServerMetrics> GetServerMetricsAsync();
}
