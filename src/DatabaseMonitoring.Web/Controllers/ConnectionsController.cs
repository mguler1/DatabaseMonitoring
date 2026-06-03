using DatabaseMonitoring.Web.Models;
using DatabaseMonitoring.Web.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.SqlClient;
using Npgsql;

namespace DatabaseMonitoring.Web.Controllers;

public class ConnectionsController : Controller
{
    private readonly ConnectionStore _store;
    private readonly ILogger<ConnectionsController> _logger;

    public ConnectionsController(ConnectionStore store, ILogger<ConnectionsController> logger)
    {
        _store = store;
        _logger = logger;
    }

    public IActionResult Index()
    {
        return View(_store.GetAll());
    }

    [HttpPost]
    public IActionResult Add(string name, string type, string connectionString)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(connectionString))
        {
            TempData["Error"] = "Ad ve bağlantı dizesi zorunludur.";
            return RedirectToAction(nameof(Index));
        }

        _store.Add(new ConnectionEntry
        {
            Name = name.Trim(),
            Type = type ?? "mssql",
            ConnectionString = connectionString.Trim()
        });

        TempData["Success"] = $"'{name}' bağlantısı eklendi.";
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public IActionResult GetConnection(string id)
    {
        var entry = _store.GetById(id);
        if (entry == null) return NotFound();
        return Json(new { entry.Id, entry.Name, entry.Type, entry.ConnectionString });
    }

    [HttpPost]
    public IActionResult Edit(string id, string name, string type, string connectionString)
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(connectionString))
        {
            TempData["Error"] = "Ad ve bağlantı dizesi zorunludur.";
            return RedirectToAction(nameof(Index));
        }

        var updated = _store.Update(id, name, type ?? "mssql", connectionString);
        if (!updated) return NotFound();

        TempData["Success"] = $"'{name}' bağlantısı güncellendi.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public IActionResult Delete(string id)
    {
        var entry = _store.GetById(id);
        if (entry == null) return NotFound();
        _store.Delete(id);
        TempData["Success"] = $"'{entry.Name}' silindi.";
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public IActionResult Toggle(string id)
    {
        _store.ToggleActive(id);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> Test([FromBody] TestRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.ConnectionString))
            return Json(new { ok = false, message = "Bağlantı dizesi boş." });

        try
        {
            if (req.Type == "postgresql")
            {
                await using var conn = new NpgsqlConnection(req.ConnectionString);
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT version()";
                var ver = await cmd.ExecuteScalarAsync();
                return Json(new { ok = true, message = $"Bağlantı başarılı — {ver?.ToString()?[..Math.Min(60, ver.ToString()!.Length)]}..." });
            }
            else
            {
                await using var conn = new SqlConnection(req.ConnectionString);
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT @@VERSION";
                var ver = await cmd.ExecuteScalarAsync();
                var short_ver = ver?.ToString()?.Split('\n')[0] ?? "";
                return Json(new { ok = true, message = $"Bağlantı başarılı — {short_ver}" });
            }
        }
        catch (Exception ex)
        {
            return Json(new { ok = false, message = ex.Message });
        }
    }

    public record TestRequest(string Type, string ConnectionString);
}
