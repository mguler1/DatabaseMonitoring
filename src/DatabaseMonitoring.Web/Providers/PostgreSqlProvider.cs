using Dapper;
using DatabaseMonitoring.Web.Interfaces;
using DatabaseMonitoring.Web.Models;
using Npgsql;

namespace DatabaseMonitoring.Web.Providers;

public class PostgreSqlProvider : IDatabaseProvider
{
    private readonly string _maintenanceConnectionString;
    private readonly ILogger<PostgreSqlProvider> _logger;

    // Cached once per provider instance: does the server support last_call (PG 14+)?
    private bool? _hasLastCall;

    public string ProviderType => "postgresql";
    public string DisplayName => "PostgreSQL";

    public PostgreSqlProvider(string connectionString, ILogger<PostgreSqlProvider> logger)
    {
        _maintenanceConnectionString = connectionString;
        _logger = logger;
    }

    public async Task<IEnumerable<DatabaseInfo>> GetDatabasesAsync()
    {
        const string sql = """
            SELECT
                datname                                              AS Name,
                'ONLINE'                                             AS State,
                NULL::timestamp                                      AS CreateDate,
                pg_database_size(datname) / 1024.0 / 1024.0        AS SizeMB
            FROM pg_database
            WHERE datistemplate = false
            ORDER BY datname
            """;

        using var conn = new NpgsqlConnection(_maintenanceConnectionString);
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
                _logger.LogWarning(ex, "Could not read stats for PostgreSQL database {Db}", db.Name);
            }
        }

        return dbs;
    }

    public async Task<DatabaseInfo?> GetDatabaseInfoAsync(string databaseName)
    {
        const string sql = """
            SELECT
                datname                                              AS Name,
                'ONLINE'                                             AS State,
                NULL::timestamp                                      AS CreateDate,
                pg_database_size(datname) / 1024.0 / 1024.0        AS SizeMB
            FROM pg_database
            WHERE datname = @DatabaseName
              AND datistemplate = false
            """;

        using var conn = new NpgsqlConnection(_maintenanceConnectionString);
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
        const string sql = """
            SELECT
                tablename                                                                                           AS TableName,
                schemaname                                                                                          AS SchemaName,
                COALESCE(n_live_tup, 0)                                                                            AS RowCount,
                COALESCE(pg_total_relation_size(quote_ident(schemaname)||'.'||quote_ident(tablename)) / 1048576.0, 0) AS TotalSpaceMB,
                COALESCE(pg_relation_size(quote_ident(schemaname)||'.'||quote_ident(tablename))       / 1048576.0, 0) AS UsedSpaceMB
            FROM pg_stat_user_tables
            WHERE schemaname NOT IN ('pg_catalog','information_schema','pg_toast')
            ORDER BY TotalSpaceMB DESC, RowCount DESC
            """;

        using var conn = new NpgsqlConnection(BuildDbConnectionString(databaseName));
        var tables = (await conn.QueryAsync<TableInfo>(sql)).ToList();

        try
        {
            var lastQueries = await GetLastQueriesPerTableAsync(databaseName);
            foreach (var table in tables)
            {
                var key = $"{table.SchemaName}.{table.TableName}";
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
            _logger.LogWarning(ex, "pg_stat_statements not accessible for {Db} — install extension or grant access", databaseName);
            foreach (var t in tables) t.DatabaseName = databaseName;
        }

        return tables;
    }

    public async Task<IEnumerable<QueryInfo>> GetRecentQueriesAsync(string databaseName, int top = 25)
    {
        var hasLastCall = await SupportsLastCallAsync(databaseName);

        var sql = hasLastCall
            ? """
              SELECT
                  last_call                AS LastExecutionTime,
                  calls                    AS ExecutionCount,
                  total_exec_time::bigint  AS TotalElapsedMs,
                  mean_exec_time::bigint   AS AvgElapsedMs,
                  query                    AS QueryText
              FROM pg_stat_statements
              WHERE dbid = (SELECT oid FROM pg_database WHERE datname = current_database())
                AND query NOT LIKE '%pg_stat_statements%'
              ORDER BY last_call DESC NULLS LAST
              LIMIT @Top
              """
            : """
              SELECT
                  NULL::timestamp          AS LastExecutionTime,
                  calls                    AS ExecutionCount,
                  total_exec_time::bigint  AS TotalElapsedMs,
                  mean_exec_time::bigint   AS AvgElapsedMs,
                  query                    AS QueryText
              FROM pg_stat_statements
              WHERE dbid = (SELECT oid FROM pg_database WHERE datname = current_database())
                AND query NOT LIKE '%pg_stat_statements%'
              ORDER BY total_exec_time DESC
              LIMIT @Top
              """;

        using var conn = new NpgsqlConnection(BuildDbConnectionString(databaseName));
        return await conn.QueryAsync<QueryInfo>(sql, new { Top = top });
    }

    public async Task<IEnumerable<QueryInfo>> GetTableQueriesAsync(string databaseName, string tableName, int top = 10)
    {
        var hasLastCall = await SupportsLastCallAsync(databaseName);

        var sql = hasLastCall
            ? """
              SELECT
                  last_call                AS LastExecutionTime,
                  calls                    AS ExecutionCount,
                  total_exec_time::bigint  AS TotalElapsedMs,
                  mean_exec_time::bigint   AS AvgElapsedMs,
                  query                    AS QueryText
              FROM pg_stat_statements
              WHERE dbid = (SELECT oid FROM pg_database WHERE datname = current_database())
                AND query ILIKE '%' || @TableName || '%'
                AND query NOT LIKE '%pg_stat_statements%'
              ORDER BY last_call DESC NULLS LAST
              LIMIT @Top
              """
            : """
              SELECT
                  NULL::timestamp          AS LastExecutionTime,
                  calls                    AS ExecutionCount,
                  total_exec_time::bigint  AS TotalElapsedMs,
                  mean_exec_time::bigint   AS AvgElapsedMs,
                  query                    AS QueryText
              FROM pg_stat_statements
              WHERE dbid = (SELECT oid FROM pg_database WHERE datname = current_database())
                AND query ILIKE '%' || @TableName || '%'
                AND query NOT LIKE '%pg_stat_statements%'
              ORDER BY total_exec_time DESC
              LIMIT @Top
              """;

        using var conn = new NpgsqlConnection(BuildDbConnectionString(databaseName));
        return await conn.QueryAsync<QueryInfo>(sql, new { Top = top, TableName = tableName });
    }

    // ---------- private helpers ----------

    private async Task<(int tableCount, long totalRows)> GetDatabaseStatsAsync(string databaseName)
    {
        const string sql = """
            SELECT
                COUNT(*)                        AS TableCount,
                COALESCE(SUM(n_live_tup), 0)   AS TotalRows
            FROM pg_stat_user_tables
            WHERE schemaname NOT IN ('pg_catalog','information_schema','pg_toast')
            """;

        using var conn = new NpgsqlConnection(BuildDbConnectionString(databaseName));
        var row = await conn.QueryFirstAsync(sql);
        return ((int)row.TableCount, (long)row.TotalRows);
    }

    // Detect PG 14+ last_call column once, then cache per provider instance
    private async Task<bool> SupportsLastCallAsync(string databaseName)
    {
        if (_hasLastCall.HasValue) return _hasLastCall.Value;

        const string sql = "SELECT current_setting('server_version_num')::int >= 140000";
        using var conn = new NpgsqlConnection(BuildDbConnectionString(databaseName));
        _hasLastCall = await conn.ExecuteScalarAsync<bool>(sql);
        return _hasLastCall.Value;
    }

    private async Task<Dictionary<string, QueryInfo>> GetLastQueriesPerTableAsync(string databaseName)
    {
        var hasLastCall = await SupportsLastCallAsync(databaseName);

        var sql = hasLastCall
            ? """
              SELECT last_call AS LastExecutionTime, query AS QueryText
              FROM pg_stat_statements
              WHERE dbid = (SELECT oid FROM pg_database WHERE datname = current_database())
                AND query NOT LIKE '%pg_stat_statements%'
              ORDER BY last_call DESC NULLS LAST
              """
            : """
              SELECT NULL::timestamp AS LastExecutionTime, query AS QueryText
              FROM pg_stat_statements
              WHERE dbid = (SELECT oid FROM pg_database WHERE datname = current_database())
                AND query NOT LIKE '%pg_stat_statements%'
              ORDER BY total_exec_time DESC
              """;

        using var conn = new NpgsqlConnection(BuildDbConnectionString(databaseName));
        var rows = await conn.QueryAsync<QueryInfo>(sql);

        var byTable = new Dictionary<string, QueryInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (string.IsNullOrWhiteSpace(row.QueryText)) continue;
            var key = ExtractPrimaryTable(row.QueryText);
            if (key != null && !byTable.ContainsKey(key))
                byTable[key] = row;
        }
        return byTable;
    }

    private static string? ExtractPrimaryTable(string sql)
    {
        // PostgreSQL uses "schema"."table" or schema.table
        var patterns = new[]
        {
            @"(?:FROM|JOIN)\s+""?(\w+)""?\.""?(\w+)""?",
            @"(?:FROM|JOIN)\s+""?(\w+)""?(?!\s*\.)"
        };

        foreach (var pattern in patterns)
        {
            var m = System.Text.RegularExpressions.Regex.Match(
                sql, pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success)
            {
                return m.Groups.Count >= 3 && m.Groups[2].Success
                    ? $"{m.Groups[1].Value}.{m.Groups[2].Value}"
                    : $"public.{m.Groups[1].Value}";
            }
        }
        return null;
    }

    private string BuildDbConnectionString(string databaseName)
    {
        var builder = new NpgsqlConnectionStringBuilder(_maintenanceConnectionString)
        {
            Database = databaseName
        };
        return builder.ConnectionString;
    }

    private static string TrimQuery(string text, int maxLen) =>
        text.Length <= maxLen ? text : text[..maxLen] + "…";
}
