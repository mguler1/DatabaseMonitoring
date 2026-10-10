using System.Collections.Concurrent;
using System.Text.Json;
using DatabaseMonitoring.Web.Models;

namespace DatabaseMonitoring.Web.Services;

/// <summary>
/// Veritabanı başına JSON Lines dosyasında zaman serisi metrik geçmişi tutar.
/// Dosya başına kilit kullanılır; ConnectionStore ile aynı dosya-tabanlı basit depolama yaklaşımıdır.
/// </summary>
public class MetricsHistoryStore
{
    private readonly string _directory;
    private readonly ConcurrentDictionary<string, Lock> _fileLocks = new();
    private readonly ConcurrentDictionary<string, DateTime> _lastPrune = new();

    public MetricsHistoryStore(IWebHostEnvironment env)
    {
        _directory = Path.Combine(env.ContentRootPath, "App_Data", "metrics");
        Directory.CreateDirectory(_directory);
    }

    public void Append(MetricSnapshot snapshot, int retentionDays)
    {
        var path = GetPath(snapshot.ConnectionId, snapshot.DatabaseName);
        var fileLock = _fileLocks.GetOrAdd(path, _ => new Lock());

        lock (fileLock)
        {
            File.AppendAllText(path, JsonSerializer.Serialize(snapshot) + Environment.NewLine);

            var lastPrune = _lastPrune.GetValueOrDefault(path, DateTime.MinValue);
            if (DateTime.Now - lastPrune > TimeSpan.FromHours(1))
            {
                PruneFile(path, retentionDays);
                _lastPrune[path] = DateTime.Now;
            }
        }
    }

    public List<MetricSnapshot> GetHistory(string connectionId, string databaseName, int hours)
    {
        var path = GetPath(connectionId, databaseName);
        if (!File.Exists(path)) return [];

        var fileLock = _fileLocks.GetOrAdd(path, _ => new Lock());
        var cutoff = DateTime.Now.AddHours(-hours);

        lock (fileLock)
        {
            var result = new List<MetricSnapshot>();
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var snap = JsonSerializer.Deserialize<MetricSnapshot>(line);
                    if (snap != null && snap.Timestamp >= cutoff)
                        result.Add(snap);
                }
                catch (JsonException) { /* bozuk satır — atla */ }
            }
            return result.OrderBy(s => s.Timestamp).ToList();
        }
    }

    private void PruneFile(string path, int retentionDays)
    {
        var cutoff = DateTime.Now.AddDays(-retentionDays);
        var kept = new List<string>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var snap = JsonSerializer.Deserialize<MetricSnapshot>(line);
                if (snap != null && snap.Timestamp >= cutoff)
                    kept.Add(line);
            }
            catch (JsonException) { /* bozuk satır — at */ }
        }
        File.WriteAllLines(path, kept);
    }

    private string GetPath(string connectionId, string databaseName)
    {
        var safeDb = string.Concat(databaseName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        return Path.Combine(_directory, $"{connectionId}_{safeDb}.jsonl");
    }
}
