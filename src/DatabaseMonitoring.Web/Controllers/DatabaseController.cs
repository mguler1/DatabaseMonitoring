using System.Text;
using DatabaseMonitoring.Web.Models;
using DatabaseMonitoring.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace DatabaseMonitoring.Web.Controllers;

public class DatabaseController : Controller
{
    private readonly DatabaseProviderFactory _factory;
    private readonly ConnectionStore _store;
    private readonly ILogger<DatabaseController> _logger;

    public DatabaseController(DatabaseProviderFactory factory, ConnectionStore store, ILogger<DatabaseController> logger)
    {
        _factory = factory;
        _store = store;
        _logger = logger;
    }

    public async Task<IActionResult> Details(string name, string connectionId)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(connectionId))
            return BadRequest();

        var conn = _store.GetById(connectionId);
        if (conn == null) return NotFound();

        var vm = new DatabaseDetailsViewModel { Connection = conn };

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
                vm.LongRunningQueries = await provider.GetLongRunningQueriesAsync(30), vm);

            await TryLoad("BlockingQueries", async () =>
                vm.BlockingQueries = await provider.GetBlockingQueriesAsync(), vm);

            await TryLoad("Indexes", async () =>
                vm.Indexes = await provider.GetIndexInfoAsync(name), vm);

            await TryLoad("BackupStatus", async () =>
                vm.BackupStatus = await provider.GetBackupStatusAsync(name), vm);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load details for {Db}", name);
            vm.ErrorMessage = ex.Message;
        }

        return View(vm);
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
