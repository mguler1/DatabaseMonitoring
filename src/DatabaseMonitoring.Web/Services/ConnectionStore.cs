using System.Text.Json;
using DatabaseMonitoring.Web.Models;

namespace DatabaseMonitoring.Web.Services;

public class ConnectionStore
{
    private readonly string _filePath;
    private List<ConnectionEntry> _cache = [];
    private readonly Lock _lock = new();

    private static readonly JsonSerializerOptions _json = new() { WriteIndented = true };

    public ConnectionStore(IWebHostEnvironment env)
    {
        _filePath = Path.Combine(env.ContentRootPath, "connections.json");
        Load();
    }

    public IReadOnlyList<ConnectionEntry> GetAll()
    {
        lock (_lock) return _cache.ToList();
    }

    public IReadOnlyList<ConnectionEntry> GetActive()
    {
        lock (_lock) return _cache.Where(c => c.IsActive).ToList();
    }

    public ConnectionEntry? GetById(string id)
    {
        lock (_lock) return _cache.FirstOrDefault(c => c.Id == id);
    }

    public void Add(ConnectionEntry entry)
    {
        lock (_lock)
        {
            entry.Id = Guid.NewGuid().ToString("N")[..8];
            entry.AddedAt = DateTime.Now;
            _cache.Add(entry);
            Save();
        }
    }

    public void Delete(string id)
    {
        lock (_lock)
        {
            _cache.RemoveAll(c => c.Id == id);
            Save();
        }
    }

    public void ToggleActive(string id)
    {
        lock (_lock)
        {
            var entry = _cache.FirstOrDefault(c => c.Id == id);
            if (entry != null) { entry.IsActive = !entry.IsActive; Save(); }
        }
    }

    public bool Update(string id, string name, string type, string connectionString)
    {
        lock (_lock)
        {
            var entry = _cache.FirstOrDefault(c => c.Id == id);
            if (entry == null) return false;
            entry.Name = name.Trim();
            entry.Type = type;
            entry.ConnectionString = connectionString.Trim();
            Save();
            return true;
        }
    }

    private void Load()
    {
        if (!File.Exists(_filePath)) { _cache = []; return; }
        try
        {
            var json = File.ReadAllText(_filePath);
            _cache = JsonSerializer.Deserialize<List<ConnectionEntry>>(json, _json) ?? [];
        }
        catch { _cache = []; }
    }

    private void Save()
    {
        File.WriteAllText(_filePath, JsonSerializer.Serialize(_cache, _json));
    }
}
