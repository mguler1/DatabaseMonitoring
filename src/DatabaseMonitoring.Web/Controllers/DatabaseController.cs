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

            try
            {
                vm.RecentQueries = await provider.GetRecentQueriesAsync(name, 25);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not load recent queries for {Db}", name);
                vm.ErrorMessage = "Son sorgular yüklenemedi: VIEW SERVER STATE yetkisi gereklidir.";
            }
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
}
