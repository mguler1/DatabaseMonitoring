using Dapper;
using DatabaseMonitoring.Web.Interfaces;
using DatabaseMonitoring.Web.Models;
using Microsoft.Data.SqlClient;

namespace DatabaseMonitoring.Web.Providers;

public class MsSqlProvider : IDatabaseProvider
{
    private readonly string _masterConnectionString;
    private readonly ILogger<MsSqlProvider> _logger;

    public string ProviderType => "mssql";
    public string DisplayName => "Microsoft SQL Server";

    public MsSqlProvider(string connectionString, ILogger<MsSqlProvider> logger)
    {
        _masterConnectionString = connectionString;
        _logger = logger;
    }

    public async Task<IEnumerable<DatabaseInfo>> GetDatabasesAsync()
    {
        const string sql = """
            SELECT
                d.name                                                         AS Name,
                d.state_desc                                                   AS State,
                d.create_date                                                  AS CreateDate,
                ISNULL(SUM(mf.size) * 8.0 / 1024, 0)                         AS SizeMB
            FROM sys.databases d
            LEFT JOIN sys.master_files mf ON mf.database_id = d.database_id
            WHERE d.name NOT IN ('master','tempdb','model','msdb')
              AND d.state_desc = 'ONLINE'
            GROUP BY d.name, d.state_desc, d.create_date
            ORDER BY d.name
            """;

        using var conn = new SqlConnection(_masterConnectionString);
        var dbs = (await conn.QueryAsync<DatabaseInfo>(sql)).ToList();

        foreach (var db in dbs)
        {
            db.ProviderType = ProviderType;
            try
            {
                var stats = await GetDatabaseStatsAsync(db.Name);
                db.TableCount = stats.tableCount;
                db.TotalRows = stats.totalRows;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not read stats for database {Db}", db.Name);
            }
        }

        return dbs;
    }

    public async Task<DatabaseInfo?> GetDatabaseInfoAsync(string databaseName)
    {
        const string sql = """
            SELECT
                d.name           AS Name,
                d.state_desc     AS State,
                d.create_date    AS CreateDate,
                ISNULL(SUM(mf.size) * 8.0 / 1024, 0) AS SizeMB
            FROM sys.databases d
            LEFT JOIN sys.master_files mf ON mf.database_id = d.database_id
            WHERE d.name = @DatabaseName
            GROUP BY d.name, d.state_desc, d.create_date
            """;

        using var conn = new SqlConnection(_masterConnectionString);
        var db = await conn.QueryFirstOrDefaultAsync<DatabaseInfo>(sql, new { DatabaseName = databaseName });
        if (db == null) return null;

        db.ProviderType = ProviderType;
        var stats = await GetDatabaseStatsAsync(databaseName);
        db.TableCount = stats.tableCount;
        db.TotalRows = stats.totalRows;

        return db;
    }

    public async Task<IEnumerable<TableInfo>> GetTablesAsync(string databaseName)
    {
        var quotedDb = QuoteIdentifier(databaseName);
        var sql = $"""
            USE {quotedDb};
            SELECT
                t.name                                    AS TableName,
                s.name                                    AS SchemaName,
                ISNULL(SUM(p.rows), 0)                      AS [RowCount],
                ISNULL(SUM(a.total_pages) * 8.0 / 1024, 0) AS [TotalSpaceMB],
                ISNULL(SUM(a.used_pages)  * 8.0 / 1024, 0) AS [UsedSpaceMB]
            FROM sys.tables t
            INNER JOIN sys.schemas s     ON t.schema_id    = s.schema_id
            INNER JOIN sys.indexes i     ON t.object_id    = i.object_id
            INNER JOIN sys.partitions p  ON i.object_id    = p.object_id
                                        AND i.index_id     = p.index_id
            INNER JOIN sys.allocation_units a ON p.partition_id = a.container_id
            WHERE t.is_ms_shipped = 0
            GROUP BY t.name, s.name
            ORDER BY [TotalSpaceMB] DESC, [RowCount] DESC
            """;

        using var conn = new SqlConnection(BuildDbConnectionString(databaseName));
        var tables = (await conn.QueryAsync<TableInfo>(sql)).ToList();

        // Fetch last query per table from DMV (best-effort)
        try
        {
            var lastQueries = await GetLastQueriesPerTableAsync(databaseName);
            foreach (var table in tables)
            {
                var key = $"{table.SchemaName}.{table.TableName}".ToUpperInvariant();
                if (lastQueries.TryGetValue(key, out var q))
                {
                    table.LastQueryTime = q.LastExecutionTime;
                    table.LastQueryText = TrimQuery(q.QueryText, 120);
                }
                table.DatabaseName = databaseName;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DMV query_stats not accessible for {Db} — VIEW SERVER STATE permission required", databaseName);
            foreach (var t in tables) t.DatabaseName = databaseName;
        }

        return tables;
    }

    public async Task<IEnumerable<QueryInfo>> GetRecentQueriesAsync(string databaseName, int top = 25)
    {
        // sys.dm_exec_query_stats is server-scoped; filter by dbid
        var sql = $"""
            SELECT TOP (@Top)
                qs.last_execution_time                                             AS LastExecutionTime,
                qs.execution_count                                                 AS ExecutionCount,
                qs.total_elapsed_time / 1000                                       AS TotalElapsedMs,
                CASE WHEN qs.execution_count = 0 THEN 0
                     ELSE qs.total_elapsed_time / qs.execution_count / 1000 END    AS AvgElapsedMs,
                SUBSTRING(
                    st.text,
                    (qs.statement_start_offset / 2) + 1,
                    (CASE qs.statement_end_offset
                         WHEN -1 THEN DATALENGTH(st.text)
                         ELSE qs.statement_end_offset
                     END - qs.statement_start_offset) / 2 + 1
                )                                                                  AS QueryText,
                DB_NAME(st.dbid)                                                   AS DatabaseName
            FROM sys.dm_exec_query_stats qs
            CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) st
            WHERE st.dbid = DB_ID(@DatabaseName)
              AND st.text NOT LIKE '%dm_exec_query_stats%'
            ORDER BY qs.last_execution_time DESC
            """;

        using var conn = new SqlConnection(_masterConnectionString);
        return await conn.QueryAsync<QueryInfo>(sql, new { Top = top, DatabaseName = databaseName });
    }

    public async Task<IEnumerable<QueryInfo>> GetTableQueriesAsync(string databaseName, string tableName, int top = 10)
    {
        var sql = $"""
            SELECT TOP (@Top)
                qs.last_execution_time                                             AS LastExecutionTime,
                qs.execution_count                                                 AS ExecutionCount,
                qs.total_elapsed_time / 1000                                       AS TotalElapsedMs,
                CASE WHEN qs.execution_count = 0 THEN 0
                     ELSE qs.total_elapsed_time / qs.execution_count / 1000 END    AS AvgElapsedMs,
                SUBSTRING(
                    st.text,
                    (qs.statement_start_offset / 2) + 1,
                    (CASE qs.statement_end_offset
                         WHEN -1 THEN DATALENGTH(st.text)
                         ELSE qs.statement_end_offset
                     END - qs.statement_start_offset) / 2 + 1
                )                                                                  AS QueryText,
                DB_NAME(st.dbid)                                                   AS DatabaseName
            FROM sys.dm_exec_query_stats qs
            CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) st
            WHERE st.dbid = DB_ID(@DatabaseName)
              AND st.text LIKE '%' + @TableName + '%'
              AND st.text NOT LIKE '%dm_exec_query_stats%'
            ORDER BY qs.last_execution_time DESC
            """;

        using var conn = new SqlConnection(_masterConnectionString);
        return await conn.QueryAsync<QueryInfo>(sql, new { Top = top, DatabaseName = databaseName, TableName = tableName });
    }

    // ---------- private helpers ----------

    private async Task<(int tableCount, long totalRows)> GetDatabaseStatsAsync(string databaseName)
    {
        var quotedDb = QuoteIdentifier(databaseName);
        var sql = $"""
            USE {quotedDb};
            SELECT
                COUNT(*)      AS TableCount,
                ISNULL(SUM(p.rows), 0) AS TotalRows
            FROM sys.tables t
            INNER JOIN sys.indexes i    ON t.object_id   = i.object_id AND i.index_id <= 1
            INNER JOIN sys.partitions p ON i.object_id   = p.object_id AND i.index_id = p.index_id
            WHERE t.is_ms_shipped = 0
            """;

        using var conn = new SqlConnection(BuildDbConnectionString(databaseName));
        var row = await conn.QueryFirstAsync(sql);
        return ((int)row.TableCount, (long)row.TotalRows);
    }

    private async Task<Dictionary<string, QueryInfo>> GetLastQueriesPerTableAsync(string databaseName)
    {
        var sql = """
            SELECT
                qs.last_execution_time AS LastExecutionTime,
                st.text                AS QueryText
            FROM sys.dm_exec_query_stats qs
            CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) st
            WHERE st.dbid = DB_ID(@DatabaseName)
              AND st.text NOT LIKE '%dm_exec_query_stats%'
            ORDER BY qs.last_execution_time DESC
            """;

        using var conn = new SqlConnection(_masterConnectionString);
        var rows = await conn.QueryAsync<QueryInfo>(sql, new { DatabaseName = databaseName });

        var result = new Dictionary<string, QueryInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            // crude: look for table names mentioned in this query
            // we store query text against first matched table later when merging
            _ = row; // processed during table merge below — kept as raw lookup
        }

        // Return full list keyed by "schema.table" — caller matches
        var byTable = new Dictionary<string, QueryInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.QueryText)) continue;
            // Will be matched by caller checking if query text contains the table name
            var key = ExtractPrimaryTable(row.QueryText);
            if (key != null && !byTable.ContainsKey(key))
                byTable[key] = row;
        }
        return byTable;
    }

    private static string? ExtractPrimaryTable(string sql)
    {
        // Very lightweight extraction: look for FROM or JOIN followed by [schema].[table] or schema.table
        var patterns = new[]
        {
            @"(?:FROM|JOIN)\s+\[?(\w+)\]?\.\[?(\w+)\]?",
            @"(?:FROM|JOIN)\s+\[?(\w+)\]?(?!\s*\.)"
        };

        foreach (var pattern in patterns)
        {
            var m = System.Text.RegularExpressions.Regex.Match(
                sql, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success)
            {
                return m.Groups.Count >= 3 && m.Groups[2].Success
                    ? $"{m.Groups[1].Value}.{m.Groups[2].Value}"
                    : $"dbo.{m.Groups[1].Value}";
            }
        }
        return null;
    }

    private string BuildDbConnectionString(string databaseName)
    {
        var builder = new SqlConnectionStringBuilder(_masterConnectionString)
        {
            InitialCatalog = databaseName
        };
        return builder.ConnectionString;
    }

    private static string QuoteIdentifier(string name) =>
        "[" + name.Replace("]", "]]") + "]";

    private static string TrimQuery(string text, int maxLen) =>
        text.Length <= maxLen ? text : text[..maxLen] + "…";
}
