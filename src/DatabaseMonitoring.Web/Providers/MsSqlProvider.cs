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

    public async Task<IEnumerable<ActiveSession>> GetActiveSessionsAsync()
    {
        const string sql = """
            SELECT
                s.session_id                    AS SessionId,
                s.login_name                    AS LoginName,
                s.host_name                     AS HostName,
                DB_NAME(s.database_id)          AS DatabaseName,
                s.status                        AS Status,
                ISNULL(r.command, 'IDLE')       AS Command,
                s.cpu_time                      AS CpuTime,
                s.memory_usage * 8              AS MemoryUsageKB,
                s.login_time                    AS LoginTime,
                s.last_request_start_time       AS LastRequestTime,
                s.program_name                  AS ProgramName,
                r.blocking_session_id           AS BlockingSessionId,
                r.wait_type                     AS WaitType,
                r.wait_time                     AS WaitTimeMs
            FROM sys.dm_exec_sessions s
            LEFT JOIN sys.dm_exec_requests r ON s.session_id = r.session_id
            WHERE s.is_user_process = 1
              AND s.session_id != @@SPID
            ORDER BY s.last_request_start_time DESC
            """;

        using var conn = new SqlConnection(_masterConnectionString);
        return await conn.QueryAsync<ActiveSession>(sql);
    }

    public async Task<IEnumerable<LongRunningQuery>> GetLongRunningQueriesAsync(int thresholdSeconds = 30)
    {
        const string sql = """
            SELECT
                r.session_id                    AS SessionId,
                s.login_name                    AS LoginName,
                DB_NAME(r.database_id)          AS DatabaseName,
                r.status                        AS Status,
                r.command                       AS Command,
                r.total_elapsed_time / 1000     AS ElapsedSeconds,
                r.wait_time                     AS WaitTimeMs,
                r.wait_type                     AS WaitType,
                r.blocking_session_id           AS BlockingSessionId,
                r.cpu_time                      AS CpuTime,
                r.logical_reads                 AS LogicalReads,
                SUBSTRING(
                    st.text,
                    (r.statement_start_offset / 2) + 1,
                    (CASE r.statement_end_offset
                         WHEN -1 THEN DATALENGTH(st.text)
                         ELSE r.statement_end_offset
                     END - r.statement_start_offset) / 2 + 1
                )                               AS QueryText
            FROM sys.dm_exec_requests r
            INNER JOIN sys.dm_exec_sessions s ON r.session_id = s.session_id
            CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) st
            WHERE s.is_user_process = 1
              AND r.total_elapsed_time / 1000 >= @ThresholdSeconds
            ORDER BY r.total_elapsed_time DESC
            """;

        using var conn = new SqlConnection(_masterConnectionString);
        return await conn.QueryAsync<LongRunningQuery>(sql, new { ThresholdSeconds = thresholdSeconds });
    }

    public async Task<IEnumerable<BlockingInfo>> GetBlockingQueriesAsync()
    {
        const string sql = """
            SELECT
                r.session_id                    AS BlockedSessionId,
                r.blocking_session_id           AS BlockingSessionId,
                s1.login_name                   AS BlockedLogin,
                ISNULL(s2.login_name, '?')      AS BlockingLogin,
                r.wait_type                     AS WaitType,
                r.wait_time                     AS WaitTimeMs,
                DB_NAME(r.database_id)          AS DatabaseName,
                SUBSTRING(
                    st1.text,
                    (r.statement_start_offset / 2) + 1,
                    (CASE r.statement_end_offset
                         WHEN -1 THEN DATALENGTH(st1.text)
                         ELSE r.statement_end_offset
                     END - r.statement_start_offset) / 2 + 1
                )                               AS BlockedQuery,
                ISNULL(blocker.query_text, '')  AS BlockingQuery
            FROM sys.dm_exec_requests r
            INNER JOIN sys.dm_exec_sessions s1 ON r.session_id = s1.session_id
            LEFT  JOIN sys.dm_exec_sessions s2 ON r.blocking_session_id = s2.session_id
            CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) st1
            OUTER APPLY (
                SELECT TOP 1 st2.text AS query_text
                FROM sys.dm_exec_requests r2
                CROSS APPLY sys.dm_exec_sql_text(r2.sql_handle) st2
                WHERE r2.session_id = r.blocking_session_id
            ) blocker
            WHERE r.blocking_session_id > 0
            ORDER BY r.wait_time DESC
            """;

        using var conn = new SqlConnection(_masterConnectionString);
        return await conn.QueryAsync<BlockingInfo>(sql);
    }

    public async Task<IEnumerable<IndexInfo>> GetIndexInfoAsync(string databaseName)
    {
        const string sql = """
            SELECT
                t.name                                                          AS TableName,
                s.name                                                          AS SchemaName,
                ISNULL(i.name, '(heap)')                                        AS IndexName,
                i.type_desc                                                     AS IndexType,
                i.is_unique                                                     AS IsUnique,
                i.is_primary_key                                                AS IsPrimaryKey,
                ISNULL(ips.avg_fragmentation_in_percent, 0)                     AS FragmentationPct,
                ISNULL(ips.page_count, 0)                                       AS PageCount,
                ISNULL(ius.user_seeks, 0)                                       AS UserSeeks,
                ISNULL(ius.user_scans, 0)                                       AS UserScans,
                ISNULL(ius.user_lookups, 0)                                     AS UserLookups,
                ISNULL(ius.user_updates, 0)                                     AS UserUpdates,
                ius.last_user_seek                                              AS LastUserSeek,
                ISNULL(
                    STUFF((
                        SELECT ', ' + c.name
                        FROM sys.index_columns ic
                        INNER JOIN sys.columns c ON ic.object_id = c.object_id AND ic.column_id = c.column_id
                        WHERE ic.object_id = i.object_id AND ic.index_id = i.index_id
                          AND ic.is_included_column = 0
                        ORDER BY ic.key_ordinal
                        FOR XML PATH(''), TYPE
                    ).value('.','nvarchar(max)'), 1, 2, ''), '') AS Columns
            FROM sys.indexes i
            INNER JOIN sys.tables t ON i.object_id = t.object_id
            INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
            OUTER APPLY sys.dm_db_index_physical_stats(DB_ID(), i.object_id, i.index_id, NULL, 'LIMITED') ips
            LEFT JOIN sys.dm_db_index_usage_stats ius
                ON ius.object_id = i.object_id
               AND ius.index_id = i.index_id
               AND ius.database_id = DB_ID()
            WHERE t.is_ms_shipped = 0
              AND i.type > 0
            ORDER BY ips.avg_fragmentation_in_percent DESC, t.name, i.name
            """;

        using var conn = new SqlConnection(BuildDbConnectionString(databaseName));
        return await conn.QueryAsync<IndexInfo>(sql);
    }

    public async Task<IEnumerable<BackupInfo>> GetBackupStatusAsync(string databaseName)
    {
        const string sql = """
            SELECT
                latest.database_name                        AS DatabaseName,
                CASE latest.type
                    WHEN 'D' THEN 'Full'
                    WHEN 'I' THEN 'Differential'
                    WHEN 'L' THEN 'Log'
                    ELSE ISNULL(latest.type, '?')
                END                                         AS BackupType,
                bs.backup_finish_date                       AS LastBackupDate,
                ISNULL(bs.backup_size / 1048576.0, 0)       AS BackupSizeMB,
                ISNULL(bmf.physical_device_name, '')        AS BackupPath,
                'Completed'                                 AS Status
            FROM (
                SELECT database_name, type, MAX(backup_finish_date) AS backup_finish_date
                FROM msdb.dbo.backupset
                WHERE database_name = @DatabaseName
                GROUP BY database_name, type
            ) latest
            INNER JOIN msdb.dbo.backupset bs
                ON bs.database_name = latest.database_name
               AND bs.type = latest.type
               AND bs.backup_finish_date = latest.backup_finish_date
            LEFT JOIN msdb.dbo.backupmediafamily bmf
                ON bs.media_set_id = bmf.media_set_id
               AND bmf.media_count = 1
            ORDER BY latest.type
            """;

        using var conn = new SqlConnection(_masterConnectionString);
        return await conn.QueryAsync<BackupInfo>(sql, new { DatabaseName = databaseName });
    }

    public async Task<IEnumerable<ColumnInfo>> GetColumnsAsync(string databaseName, string tableName, string schemaName = "dbo")
    {
        const string sql = """
            SELECT
                c.column_id                         AS OrdinalPosition,
                c.name                              AS ColumnName,
                tp.name +
                CASE
                    WHEN tp.name IN ('varchar','nvarchar','char','nchar')
                    THEN '(' + CASE WHEN c.max_length = -1 THEN 'MAX'
                                    ELSE CAST(c.max_length / CASE tp.name WHEN 'nvarchar' THEN 2 WHEN 'nchar' THEN 2 ELSE 1 END AS varchar)
                               END + ')'
                    WHEN tp.name IN ('decimal','numeric')
                    THEN '(' + CAST(c.precision AS varchar) + ',' + CAST(c.scale AS varchar) + ')'
                    ELSE ''
                END                                 AS DataType,
                c.is_nullable                       AS IsNullable,
                c.max_length                        AS MaxLength,
                CAST(CASE WHEN pk.column_id IS NOT NULL THEN 1 ELSE 0 END AS bit) AS IsPrimaryKey,
                CAST(CASE WHEN fk.parent_column_id IS NOT NULL THEN 1 ELSE 0 END AS bit) AS IsForeignKey,
                dc.definition                       AS DefaultValue,
                c.is_identity                       AS IsIdentity
            FROM sys.columns c
            INNER JOIN sys.tables t  ON c.object_id = t.object_id
            INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
            INNER JOIN sys.types tp  ON c.user_type_id = tp.user_type_id
            LEFT JOIN sys.default_constraints dc ON c.default_object_id = dc.object_id
            LEFT JOIN (
                SELECT ic.object_id, ic.column_id
                FROM sys.index_columns ic
                INNER JOIN sys.indexes i ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                WHERE i.is_primary_key = 1
            ) pk ON pk.object_id = c.object_id AND pk.column_id = c.column_id
            LEFT JOIN (
                SELECT fkc.parent_object_id, fkc.parent_column_id
                FROM sys.foreign_key_columns fkc
            ) fk ON fk.parent_object_id = c.object_id AND fk.parent_column_id = c.column_id
            WHERE t.name   = @TableName
              AND s.name   = @SchemaName
            ORDER BY c.column_id
            """;

        using var conn = new SqlConnection(BuildDbConnectionString(databaseName));
        return await conn.QueryAsync<ColumnInfo>(sql, new { TableName = tableName, SchemaName = schemaName });
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
        const string sql = """
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
