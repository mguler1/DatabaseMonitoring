using Dapper;
using DatabaseMonitoring.Web.Interfaces;
using DatabaseMonitoring.Web.Models;
using Npgsql;

namespace DatabaseMonitoring.Web.Providers;

public class PostgreSqlProvider : IDatabaseProvider
{
    private readonly string _maintenanceConnectionString;
    private readonly ILogger<PostgreSqlProvider> _logger;

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

    public async Task<IEnumerable<ActiveSession>> GetActiveSessionsAsync()
    {
        const string sql = """
            SELECT
                pid                                                             AS SessionId,
                usename                                                         AS LoginName,
                COALESCE(client_hostname, client_addr::text, 'local')          AS HostName,
                datname                                                         AS DatabaseName,
                state                                                           AS Status,
                COALESCE(wait_event_type, 'Active')                            AS Command,
                NULL::int                                                       AS CpuTime,
                NULL::int                                                       AS MemoryUsageKB,
                backend_start                                                   AS LoginTime,
                state_change                                                    AS LastRequestTime,
                application_name                                                AS ProgramName,
                NULL::int                                                       AS BlockingSessionId,
                wait_event_type                                                 AS WaitType,
                NULL::int                                                       AS WaitTimeMs
            FROM pg_stat_activity
            WHERE datname IS NOT NULL
              AND pid != pg_backend_pid()
            ORDER BY state_change DESC NULLS LAST
            """;

        using var conn = new NpgsqlConnection(_maintenanceConnectionString);
        return await conn.QueryAsync<ActiveSession>(sql);
    }

    public async Task<IEnumerable<LongRunningQuery>> GetLongRunningQueriesAsync(int thresholdSeconds = 30)
    {
        const string sql = """
            SELECT
                pid                                                             AS SessionId,
                usename                                                         AS LoginName,
                datname                                                         AS DatabaseName,
                state                                                           AS Status,
                COALESCE(wait_event_type, 'Running')                           AS Command,
                EXTRACT(EPOCH FROM (now() - query_start))::int                 AS ElapsedSeconds,
                NULL::int                                                       AS WaitTimeMs,
                wait_event_type                                                 AS WaitType,
                NULL::int                                                       AS BlockingSessionId,
                NULL::int                                                       AS CpuTime,
                NULL::bigint                                                    AS LogicalReads,
                query                                                           AS QueryText
            FROM pg_stat_activity
            WHERE datname IS NOT NULL
              AND state = 'active'
              AND query_start IS NOT NULL
              AND EXTRACT(EPOCH FROM (now() - query_start)) >= @ThresholdSeconds
            ORDER BY query_start ASC
            """;

        using var conn = new NpgsqlConnection(_maintenanceConnectionString);
        return await conn.QueryAsync<LongRunningQuery>(sql, new { ThresholdSeconds = thresholdSeconds });
    }

    public async Task<IEnumerable<BlockingInfo>> GetBlockingQueriesAsync()
    {
        const string sql = """
            WITH blocking AS (
                SELECT
                    a.pid                                                       AS BlockedSessionId,
                    unnest(pg_blocking_pids(a.pid))                             AS BlockingSessionId,
                    a.usename                                                   AS BlockedLogin,
                    a.wait_event_type                                           AS WaitType,
                    EXTRACT(EPOCH FROM (now() - a.state_change))::int * 1000   AS WaitTimeMs,
                    a.datname                                                   AS DatabaseName,
                    a.query                                                     AS BlockedQuery
                FROM pg_stat_activity a
                WHERE cardinality(pg_blocking_pids(a.pid)) > 0
            )
            SELECT
                b.BlockedSessionId,
                b.BlockingSessionId,
                b.BlockedLogin,
                COALESCE(a2.usename, '?')                                       AS BlockingLogin,
                b.WaitType,
                b.WaitTimeMs,
                b.DatabaseName,
                b.BlockedQuery,
                COALESCE(a2.query, '')                                          AS BlockingQuery
            FROM blocking b
            LEFT JOIN pg_stat_activity a2 ON a2.pid = b.BlockingSessionId
            """;

        using var conn = new NpgsqlConnection(_maintenanceConnectionString);
        return await conn.QueryAsync<BlockingInfo>(sql);
    }

    public async Task<IEnumerable<IndexInfo>> GetIndexInfoAsync(string databaseName)
    {
        const string sql = """
            SELECT
                t.relname                                                       AS TableName,
                n.nspname                                                       AS SchemaName,
                i.relname                                                       AS IndexName,
                am.amname                                                       AS IndexType,
                ix.indisunique                                                  AS IsUnique,
                ix.indisprimary                                                 AS IsPrimaryKey,
                0.0::double precision                                           AS FragmentationPct,
                COALESCE(i.relpages, 0)                                         AS PageCount,
                COALESCE(ius.idx_scan, 0)                                       AS UserSeeks,
                0                                                               AS UserScans,
                0                                                               AS UserLookups,
                COALESCE(ius.idx_tup_fetch, 0)                                  AS UserUpdates,
                NULL::timestamp                                                 AS LastUserSeek,
                string_agg(a.attname, ', ' ORDER BY array_position(ix.indkey, a.attnum)) AS Columns
            FROM pg_class t
            INNER JOIN pg_namespace n  ON t.relnamespace  = n.oid
            INNER JOIN pg_index ix     ON t.oid            = ix.indrelid
            INNER JOIN pg_class i      ON ix.indexrelid    = i.oid
            INNER JOIN pg_am am        ON i.relam          = am.oid
            INNER JOIN pg_attribute a  ON a.attrelid       = t.oid
                                      AND a.attnum         = ANY(ix.indkey)
                                      AND a.attnum > 0
            LEFT JOIN pg_stat_user_indexes ius ON ius.indexrelid = ix.indexrelid
            WHERE n.nspname NOT IN ('pg_catalog','information_schema','pg_toast')
              AND t.relkind = 'r'
            GROUP BY t.relname, n.nspname, i.relname, am.amname,
                     ix.indisunique, ix.indisprimary, ix.indexrelid,
                     i.relpages, ius.idx_scan, ius.idx_tup_fetch
            ORDER BY t.relname, i.relname
            """;

        using var conn = new NpgsqlConnection(BuildDbConnectionString(databaseName));
        return await conn.QueryAsync<IndexInfo>(sql);
    }

    public async Task<IEnumerable<BackupInfo>> GetBackupStatusAsync(string databaseName)
    {
        const string sql = """
            SELECT
                current_database()                                              AS DatabaseName,
                'WAL Archive'                                                   AS BackupType,
                last_archived_time                                              AS LastBackupDate,
                0::double precision                                             AS BackupSizeMB,
                COALESCE(last_archived_wal, '—')                               AS BackupPath,
                CASE
                    WHEN last_failed_time IS NULL AND last_archived_time IS NULL THEN 'Not Configured'
                    WHEN last_failed_time IS NULL THEN 'Active'
                    WHEN last_archived_time IS NULL THEN 'Failed'
                    WHEN last_archived_time >= last_failed_time THEN 'Active'
                    ELSE 'Failed'
                END                                                             AS Status
            FROM pg_stat_archiver
            """;

        using var conn = new NpgsqlConnection(BuildDbConnectionString(databaseName));
        return await conn.QueryAsync<BackupInfo>(sql);
    }

    public async Task<IEnumerable<ColumnInfo>> GetColumnsAsync(string databaseName, string tableName, string schemaName = "public")
    {
        const string sql = """
            SELECT
                a.attnum                                                        AS OrdinalPosition,
                a.attname                                                       AS ColumnName,
                format_type(a.atttypid, a.atttypmod)                           AS DataType,
                NOT a.attnotnull                                                AS IsNullable,
                a.attlen                                                        AS MaxLength,
                EXISTS(
                    SELECT 1 FROM pg_constraint con
                    WHERE con.contype = 'p'
                      AND con.conrelid = a.attrelid
                      AND a.attnum = ANY(con.conkey)
                )                                                               AS IsPrimaryKey,
                EXISTS(
                    SELECT 1 FROM pg_constraint con
                    WHERE con.contype = 'f'
                      AND con.conrelid = a.attrelid
                      AND a.attnum = ANY(con.conkey)
                )                                                               AS IsForeignKey,
                pg_get_expr(ad.adbin, ad.adrelid)                              AS DefaultValue,
                (a.attidentity != '')                                           AS IsIdentity
            FROM pg_attribute a
            INNER JOIN pg_class c     ON a.attrelid    = c.oid
            INNER JOIN pg_namespace n ON c.relnamespace = n.oid
            LEFT  JOIN pg_attrdef ad  ON ad.adrelid     = a.attrelid
                                     AND ad.adnum       = a.attnum
            WHERE c.relname  = @TableName
              AND n.nspname  = @SchemaName
              AND a.attnum   > 0
              AND NOT a.attisdropped
            ORDER BY a.attnum
            """;

        using var conn = new NpgsqlConnection(BuildDbConnectionString(databaseName));
        return await conn.QueryAsync<ColumnInfo>(sql, new { TableName = tableName, SchemaName = schemaName });
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
