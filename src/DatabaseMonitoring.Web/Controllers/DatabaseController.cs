using System.Text;
using DatabaseMonitoring.Web.Models;
using DatabaseMonitoring.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace DatabaseMonitoring.Web.Controllers;

public class DatabaseController : Controller
{
    private readonly DatabaseProviderFactory _factory;
    private readonly ConnectionStore _store;
    private readonly MetricsHistoryStore _history;
    private readonly AlertEvaluator _alertEvaluator;
    private readonly MonitoringOptions _monitoringOptions;
    private readonly ILogger<DatabaseController> _logger;

    public DatabaseController(
        DatabaseProviderFactory factory,
        ConnectionStore store,
        MetricsHistoryStore history,
        AlertEvaluator alertEvaluator,
        IOptions<MonitoringOptions> monitoringOptions,
        ILogger<DatabaseController> logger)
    {
        _factory = factory;
        _store = store;
        _history = history;
        _alertEvaluator = alertEvaluator;
        _monitoringOptions = monitoringOptions.Value;
        _logger = logger;
    }

    public async Task<IActionResult> Details(string name, string connectionId)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(connectionId))
            return BadRequest();

        var conn = _store.GetById(connectionId);
        if (conn == null) return NotFound();

        var vm = new DatabaseDetailsViewModel { Connection = conn, BackupStaleHours = _monitoringOptions.BackupStaleHours };

        try
        {
            var provider = _factory.GetById(connectionId);
            var dbInfo = await provider.GetDatabaseInfoAsync(name);
            if (dbInfo == null) return NotFound();

            dbInfo.ConnectionId = conn.Id;
            dbInfo.ConnectionName = conn.Name;
            vm.Database = dbInfo;
            vm.Tables = await provider.GetTablesAsync(name);

            await TryLoad("RecentQueries", async () =>
                vm.RecentQueries = await provider.GetRecentQueriesAsync(name, 25), vm);

            await TryLoad("ActiveSessions", async () =>
                vm.ActiveSessions = await provider.GetActiveSessionsAsync(), vm);

            await TryLoad("LongRunningQueries", async () =>
                vm.LongRunningQueries = await provider.GetLongRunningQueriesAsync(_monitoringOptions.LongRunningQuerySeconds), vm);

            await TryLoad("BlockingQueries", async () =>
                vm.BlockingQueries = await provider.GetBlockingQueriesAsync(), vm);

            await TryLoad("Indexes", async () =>
                vm.Indexes = await provider.GetIndexInfoAsync(name), vm);

            await TryLoad("BackupStatus", async () =>
                vm.BackupStatus = await provider.GetBackupStatusAsync(name), vm);

            await TryLoad("ServerMetrics", async () =>
                vm.ServerMetrics = await provider.GetServerMetricsAsync(), vm);

            vm.Alerts = _alertEvaluator.Evaluate(
                name,
                vm.BlockingQueries.ToList(),
                vm.LongRunningQueries.ToList(),
                vm.BackupStatus.ToList(),
                vm.ServerMetrics);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load details for {Db}", name);
            vm.ErrorMessage = ex.Message;
        }

        return View(vm);
    }

    /// <summary>Oto-yenileme için canlı metrikleri JSON olarak döner (sayfayı yeniden yüklemeden).</summary>
    [HttpGet]
    public async Task<IActionResult> LiveStatus(string name, string connectionId)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(connectionId))
            return BadRequest();

        var conn = _store.GetById(connectionId);
        if (conn == null) return NotFound();

        try
        {
            var provider = _factory.GetById(connectionId);
            var sessions = await provider.GetActiveSessionsAsync();
            var longRunning = await provider.GetLongRunningQueriesAsync(_monitoringOptions.LongRunningQuerySeconds);
            var blocking = await provider.GetBlockingQueriesAsync();

            return Json(new
            {
                sessions,
                longRunning,
                blocking,
                sessionCount = sessions.Count(),
                longRunningCount = longRunning.Count(),
                blockingCount = blocking.Count(),
                timestamp = DateTime.Now
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "LiveStatus failed for {Db}", name);
            return Json(new { error = ex.Message });
        }
    }

    /// <summary>Trend grafikleri için geçmiş metrik anlık görüntülerini JSON olarak döner.</summary>
    [HttpGet]
    public IActionResult History(string name, string connectionId, int hours = 24)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(connectionId))
            return BadRequest();

        var snapshots = _history.GetHistory(connectionId, name, hours);
        return Json(snapshots.Select(s => new
        {
            timestamp = s.Timestamp,
            sizeMB = s.SizeMB,
            tableCount = s.TableCount,
            totalRows = s.TotalRows,
            activeSessionCount = s.ActiveSessionCount,
            longRunningCount = s.LongRunningCount,
            blockingCount = s.BlockingCount
        }));
    }

    public async Task<IActionResult> TableQueries(string db, string table, string connectionId)
    {
        if (string.IsNullOrWhiteSpace(db) || string.IsNullOrWhiteSpace(table) || string.IsNullOrWhiteSpace(connectionId))
            return BadRequest();

        var conn = _store.GetById(connectionId);
        if (conn == null) return NotFound();

        var vm = new TableQueriesViewModel { DatabaseName = db, TableName = table, Connection = conn };

        try
        {
            var provider = _factory.GetById(connectionId);
            vm.Queries = await provider.GetTableQueriesAsync(db, table, 15);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load queries for {Table} in {Db}", table, db);
            ViewBag.ErrorMessage = ex.Message;
        }

        return View(vm);
    }

    [HttpGet]
    public async Task<IActionResult> GetColumns(string db, string table, string schema, string connectionId)
    {
        if (string.IsNullOrWhiteSpace(db) || string.IsNullOrWhiteSpace(table) || string.IsNullOrWhiteSpace(connectionId))
            return BadRequest();

        var conn = _store.GetById(connectionId);
        if (conn == null) return NotFound();

        try
        {
            var provider = _factory.GetById(connectionId);
            var schemaName = string.IsNullOrWhiteSpace(schema)
                ? (conn.Type == "postgresql" ? "public" : "dbo")
                : schema;
            var columns = await provider.GetColumnsAsync(db, table, schemaName);
            return Json(columns);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load columns for {Table}", table);
            return Json(new { error = ex.Message });
        }
    }

    [HttpGet]
    public async Task<IActionResult> ExportCsv(string db, string connectionId)
    {
        if (string.IsNullOrWhiteSpace(db) || string.IsNullOrWhiteSpace(connectionId))
            return BadRequest();

        var conn = _store.GetById(connectionId);
        if (conn == null) return NotFound();

        try
        {
            var provider = _factory.GetById(connectionId);
            var tables = await provider.GetTablesAsync(db);

            var sb = new StringBuilder();
            sb.AppendLine("Schema,Table,Rows,Total MB,Used MB");
            foreach (var t in tables)
            {
                sb.AppendLine($"\"{t.SchemaName}\",\"{t.TableName}\",{t.RowCount},{t.TotalSpaceMB:F2},{t.UsedSpaceMB:F2}");
            }

            var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
            return File(bytes, "text/csv", $"{db}_tables_{DateTime.Now:yyyyMMdd_HHmm}.csv");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export CSV for {Db}", db);
            return BadRequest(ex.Message);
        }
    }

    [HttpGet]
    public async Task<IActionResult> ExportJson(string db, string connectionId)
    {
        if (string.IsNullOrWhiteSpace(db) || string.IsNullOrWhiteSpace(connectionId))
            return BadRequest();

        var conn = _store.GetById(connectionId);
        if (conn == null) return NotFound();

        try
        {
            var provider = _factory.GetById(connectionId);
            var tables = await provider.GetTablesAsync(db);
            return File(
                Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(tables, new System.Text.Json.JsonSerializerOptions { WriteIndented = true })),
                "application/json",
                $"{db}_tables_{DateTime.Now:yyyyMMdd_HHmm}.json");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export JSON for {Db}", db);
            return BadRequest(ex.Message);
        }
    }

    [HttpGet]
    public async Task<IActionResult> ExportSessionsCsv(string connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
            return BadRequest();

        var conn = _store.GetById(connectionId);
        if (conn == null) return NotFound();

        try
        {
            var provider = _factory.GetById(connectionId);
            var sessions = await provider.GetActiveSessionsAsync();

            var sb = new StringBuilder();
            sb.AppendLine("SessionId,LoginName,HostName,DatabaseName,Status,Command,CpuTime,MemoryUsageKB,LoginTime,ProgramName,BlockingSessionId,WaitType,WaitTimeMs");
            foreach (var s in sessions)
            {
                sb.AppendLine($"{s.SessionId},\"{s.LoginName}\",\"{s.HostName}\",\"{s.DatabaseName}\",\"{s.Status}\",\"{s.Command}\",{s.CpuTime},{s.MemoryUsageKB},{s.LoginTime:O},\"{s.ProgramName}\",{s.BlockingSessionId},\"{s.WaitType}\",{s.WaitTimeMs}");
            }

            var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
            return File(bytes, "text/csv", $"sessions_{conn.Name}_{DateTime.Now:yyyyMMdd_HHmm}.csv");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export sessions CSV for {Conn}", connectionId);
            return BadRequest(ex.Message);
        }
    }

    [HttpGet]
    public async Task<IActionResult> ExportLongRunningCsv(string connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
            return BadRequest();

        var conn = _store.GetById(connectionId);
        if (conn == null) return NotFound();

        try
        {
            var provider = _factory.GetById(connectionId);
            var queries = await provider.GetLongRunningQueriesAsync(_monitoringOptions.LongRunningQuerySeconds);

            var sb = new StringBuilder();
            sb.AppendLine("SessionId,LoginName,DatabaseName,Status,ElapsedSeconds,WaitTimeMs,WaitType,BlockingSessionId,CpuTime,LogicalReads,QueryText");
            foreach (var q in queries)
            {
                var text = q.QueryText?.Replace("\"", "'").Replace("\r", " ").Replace("\n", " ") ?? "";
                sb.AppendLine($"{q.SessionId},\"{q.LoginName}\",\"{q.DatabaseName}\",\"{q.Status}\",{q.ElapsedSeconds},{q.WaitTimeMs},\"{q.WaitType}\",{q.BlockingSessionId},{q.CpuTime},{q.LogicalReads},\"{text}\"");
            }

            var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
            return File(bytes, "text/csv", $"long_running_{conn.Name}_{DateTime.Now:yyyyMMdd_HHmm}.csv");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to export long running CSV for {Conn}", connectionId);
            return BadRequest(ex.Message);
        }
    }

    private async Task TryLoad(string key, Func<Task> action, DatabaseDetailsViewModel vm)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load {Key}", key);
            vm.WarningMessages[key] = ex.Message;
        }
    }
}
