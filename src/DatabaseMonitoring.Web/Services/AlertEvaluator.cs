using DatabaseMonitoring.Web.Models;
using Microsoft.Extensions.Options;

namespace DatabaseMonitoring.Web.Services;

/// <summary>
/// Mevcut (anlık) izleme verisini yapılandırılmış eşiklerle karşılaştırıp uyarı listesi üretir.
/// Saf mantık — DB/ağ çağrısı yapmaz, bu yüzden birim testi kolaydır.
/// </summary>
public class AlertEvaluator
{
    private readonly MonitoringOptions _options;

    public AlertEvaluator(IOptions<MonitoringOptions> options)
    {
        _options = options.Value;
    }

    public List<AlertMessage> Evaluate(
        string databaseName,
        IReadOnlyCollection<BlockingInfo> blocking,
        IReadOnlyCollection<LongRunningQuery> longRunning,
        IReadOnlyCollection<BackupInfo> backups,
        ServerMetrics? serverMetrics)
    {
        var alerts = new List<AlertMessage>();

        foreach (var b in blocking)
        {
            if (b.WaitTimeMs >= _options.BlockingWaitMsCritical)
            {
                alerts.Add(new AlertMessage
                {
                    Severity = AlertSeverity.Critical,
                    Code = "blocking",
                    Message = $"{databaseName}: Oturum {b.BlockedSessionId}, {b.BlockingSessionId} tarafından {b.WaitTimeMs:N0} ms'dir bloklanıyor."
                });
            }
        }

        var longest = longRunning.Count > 0 ? longRunning.Max(q => q.ElapsedSeconds) : 0;
        if (longest >= _options.LongRunningQuerySeconds * 4)
        {
            alerts.Add(new AlertMessage
            {
                Severity = AlertSeverity.Warning,
                Code = "long-running",
                Message = $"{databaseName}: {longRunning.Count} uzun süren sorgu var, en uzunu {longest:N0} sn."
            });
        }

        foreach (var bk in backups)
        {
            if (!bk.LastBackupDate.HasValue)
            {
                alerts.Add(new AlertMessage
                {
                    Severity = AlertSeverity.Warning,
                    Code = "backup-missing",
                    Message = $"{databaseName}: {bk.BackupType} yedek kaydı bulunamadı."
                });
                continue;
            }

            var hoursSince = (DateTime.Now - bk.LastBackupDate.Value).TotalHours;
            if (bk.BackupType == "Full" && hoursSince >= _options.BackupStaleHours)
            {
                alerts.Add(new AlertMessage
                {
                    Severity = AlertSeverity.Critical,
                    Code = "backup-stale",
                    Message = $"{databaseName}: Son tam yedek {hoursSince / 24:N1} gün önce alınmış (eşik: {_options.BackupStaleHours} saat)."
                });
            }
        }

        if (serverMetrics != null)
        {
            foreach (var vol in serverMetrics.Volumes)
            {
                if (vol.TotalMB > 0 && vol.FreePct <= _options.DiskFreePctCritical)
                {
                    alerts.Add(new AlertMessage
                    {
                        Severity = AlertSeverity.Critical,
                        Code = "disk-low",
                        Message = $"{databaseName}: {vol.VolumePath} üzerinde disk alanı kritik seviyede — %{vol.FreePct:N1} boş."
                    });
                }
            }
        }

        return alerts;
    }
}
