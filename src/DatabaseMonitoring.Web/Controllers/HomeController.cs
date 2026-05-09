using DatabaseMonitoring.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace DatabaseMonitoring.Web.Controllers;

public class HomeController : Controller
{
    private readonly ConnectionStore _store;
    private readonly DatabaseProviderFactory _factory;
    private readonly ILogger<HomeController> _logger;

    public HomeController(ConnectionStore store, DatabaseProviderFactory factory, ILogger<HomeController> logger)
    {
        _store = store;
        _factory = factory;
        _logger = logger;
    }

    public async Task<IActionResult> Index()
    {
        var connections = _store.GetActive();

        if (!connections.Any())
            return RedirectToAction("Index", "Connections");

        var results = new List<Models.DatabaseInfo>();
        var errors = new List<string>();

        await Parallel.ForEachAsync(connections,
            new ParallelOptions { MaxDegreeOfParallelism = 4 },
            async (conn, _) =>
            {
                try
                {
                    var provider = _factory.ForEntry(conn);
                    var dbs = await provider.GetDatabasesAsync();
                    foreach (var db in dbs)
                    {
                        db.ConnectionId = conn.Id;
                        db.ConnectionName = conn.Name;
                    }
                    lock (results) results.AddRange(dbs);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Connection {Name} failed", conn.Name);
                    lock (errors) errors.Add($"{conn.Name}: {ex.Message}");
                }
            });

        ViewBag.Connections = connections;
        ViewBag.Errors = errors;
        return View(results.OrderBy(d => d.ConnectionName).ThenByDescending(d => d.SizeMB));
    }
}
