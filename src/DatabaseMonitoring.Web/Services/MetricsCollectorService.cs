using System.Collections.Concurrent;
using DatabaseMonitoring.Web.Models;
using Microsoft.Extensions.Options;

namespace DatabaseMonitoring.Web.Services;

/// <summary>
/// Periyodik olarak tüm aktif bağlantılardaki veritabanlarından metrik anlık görüntüsü alır,
/// geçmişe kaydeder ve eşik aşımlarında (yeni oluşan uyarılarda) webhook bildirimi tetikler.
/// </summary>
public class MetricsCollectorService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly MonitoringOptions _options;
    private readonly ILogger<MetricsCollectorService> _logger;

    // db başına son döngüde görülen uyarı kodları — yalnızca yeni (önceden yok olan) uyarılarda webhook tetiklenir
    private readonly ConcurrentDictionary<string, HashSet<string>> _lastAlertCodes = new();

    public MetricsCollectorService(
        IServiceScopeFactory scopeFactory,
        IOptions<MonitoringOptions> options,
        ILogger<MetricsCollectorService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(Math.Max(1, _options.CollectorIntervalMinutes));

        // Uygulama ayağa kalkar kalkmaz bir kere, sonra periyodik.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CollectOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (stoppingToken.IsCancellationRequested || ex is ObjectDisposedException)
            {
                // Uygulama kapanırken DI scope'u/HttpClient elden gidebilir — bu beklenen bir durumdur, hata değildir.
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Metrik toplama döngüsü sırasında hata");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task CollectOnceAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<ConnectionStore>();
        var factory = scope.ServiceProvider.GetRequiredService<DatabaseProviderFactory>();
        var history = scope.ServiceProvider.GetRequiredService<MetricsHistoryStore>();
        var alertEvaluator = scope.ServiceProvider.GetRequiredService<AlertEvaluator>();
        var webhook = scope.ServiceProvider.GetRequiredService<WebhookNotifier>();

        var connections = store.GetActive();

        foreach (var conn in connections)
        {
            if (ct.IsCancellationRequested) break;

            try
            {
                var provider = factory.ForEntry(conn);
                var databases = await provider.GetDatabasesAsync();

                foreach (var db in databases)
                {
                    if (ct.IsCancellationRequested) break;

                    var sessions = await TryGet(() => provider.GetActiveSessionsAsync(), []);
                    var longRunning = await TryGet(() => provider.GetLongRunningQueriesAsync(_options.LongRunningQuerySeconds), []);
                    var blocking = await TryGet(() => provider.GetBlockingQueriesAsync(), []);
                    var backups = await TryGet(() => provider.GetBackupStatusAsync(db.Name), []);
                    var serverMetrics = await TryGet(() => provider.GetServerMetricsAsync(), new ServerMetrics());

                    var snapshot = new MetricSnapshot
                    {
                        Timestamp = DateTime.Now,
                        ConnectionId = conn.Id,
                        DatabaseName = db.Name,
                        SizeMB = db.SizeMB,
                        TableCount = db.TableCount,
                        TotalRows = db.TotalRows,
                        ActiveSessionCount = sessions.Count(),
                        LongRunningCount = longRunning.Count(),
                        BlockingCount = blocking.Count()
                    };
                    history.Append(snapshot, _options.HistoryRetentionDays);

                    var alerts = alertEvaluator.Evaluate(db.Name, blocking.ToList(), longRunning.ToList(), backups.ToList(), serverMetrics);
                    await NotifyIfNewAsync(conn.Id, db.Name, alerts, webhook);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Metrik toplama sırasında {Conn} bağlantısı atlandı", conn.Name);
            }
        }
    }

    private async Task NotifyIfNewAsync(string connectionId, string databaseName, List<AlertMessage> alerts, WebhookNotifier webhook)
    {
        var key = $"{connectionId}:{databaseName}";
        var currentCodes = alerts.Select(a => $"{a.Code}").ToHashSet();
        var previousCodes = _lastAlertCodes.GetValueOrDefault(key, []);

        var newAlerts = alerts.Where(a => !previousCodes.Contains(a.Code)).ToList();
        _lastAlertCodes[key] = currentCodes;

        if (newAlerts.Count > 0 && webhook.IsEnabled)
            await webhook.SendAsync(newAlerts);
    }

    private static async Task<IEnumerable<T>> TryGet<T>(Func<Task<IEnumerable<T>>> action, IEnumerable<T> fallback)
    {
        try { return await action(); }
        catch { return fallback; }
    }

    private static async Task<T> TryGet<T>(Func<Task<T>> action, T fallback)
    {
        try { return await action(); }
        catch { return fallback; }
    }
}
