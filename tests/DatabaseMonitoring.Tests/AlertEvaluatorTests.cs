using DatabaseMonitoring.Web.Models;
using DatabaseMonitoring.Web.Services;
using Microsoft.Extensions.Options;

namespace DatabaseMonitoring.Tests;

public class AlertEvaluatorTests
{
    private static AlertEvaluator CreateEvaluator(MonitoringOptions? options = null)
    {
        options ??= new MonitoringOptions
        {
            LongRunningQuerySeconds = 30,
            BlockingWaitMsCritical = 5000,
            BackupStaleHours = 26,
            DiskFreePctCritical = 10
        };
        return new AlertEvaluator(Options.Create(options));
    }

    [Fact]
    public void Evaluate_NoData_ReturnsNoAlerts()
    {
        var evaluator = CreateEvaluator();
        var alerts = evaluator.Evaluate("TestDb", [], [], [], null);
        Assert.Empty(alerts);
    }

    [Fact]
    public void Evaluate_BlockingBelowThreshold_NoAlert()
    {
        var evaluator = CreateEvaluator();
        var blocking = new List<BlockingInfo> { new() { WaitTimeMs = 1000 } };

        var alerts = evaluator.Evaluate("TestDb", blocking, [], [], null);

        Assert.Empty(alerts);
    }

    [Fact]
    public void Evaluate_BlockingAtOrAboveThreshold_RaisesCriticalAlert()
    {
        var evaluator = CreateEvaluator();
        var blocking = new List<BlockingInfo> { new() { BlockedSessionId = 10, BlockingSessionId = 20, WaitTimeMs = 5000 } };

        var alerts = evaluator.Evaluate("TestDb", blocking, [], [], null);

        Assert.Single(alerts);
        Assert.Equal(AlertSeverity.Critical, alerts[0].Severity);
        Assert.Equal("blocking", alerts[0].Code);
    }

    [Fact]
    public void Evaluate_LongRunningQueryWellBeyondThreshold_RaisesWarning()
    {
        var evaluator = CreateEvaluator();
        // eşik 30 sn, tetikleme eşiği 4x = 120 sn
        var longRunning = new List<LongRunningQuery> { new() { ElapsedSeconds = 130 } };

        var alerts = evaluator.Evaluate("TestDb", [], longRunning, [], null);

        Assert.Single(alerts);
        Assert.Equal(AlertSeverity.Warning, alerts[0].Severity);
        Assert.Equal("long-running", alerts[0].Code);
    }

    [Fact]
    public void Evaluate_LongRunningQueryJustOverThreshold_NoAlertYet()
    {
        var evaluator = CreateEvaluator();
        var longRunning = new List<LongRunningQuery> { new() { ElapsedSeconds = 35 } };

        var alerts = evaluator.Evaluate("TestDb", [], longRunning, [], null);

        Assert.Empty(alerts);
    }

    [Fact]
    public void Evaluate_MissingBackup_RaisesWarning()
    {
        var evaluator = CreateEvaluator();
        var backups = new List<BackupInfo> { new() { BackupType = "Full", LastBackupDate = null } };

        var alerts = evaluator.Evaluate("TestDb", [], [], backups, null);

        Assert.Single(alerts);
        Assert.Equal("backup-missing", alerts[0].Code);
    }

    [Fact]
    public void Evaluate_StaleFullBackup_RaisesCriticalAlert()
    {
        var evaluator = CreateEvaluator();
        var backups = new List<BackupInfo> { new() { BackupType = "Full", LastBackupDate = DateTime.Now.AddHours(-30) } };

        var alerts = evaluator.Evaluate("TestDb", [], [], backups, null);

        Assert.Single(alerts);
        Assert.Equal(AlertSeverity.Critical, alerts[0].Severity);
        Assert.Equal("backup-stale", alerts[0].Code);
    }

    [Fact]
    public void Evaluate_RecentFullBackup_NoAlert()
    {
        var evaluator = CreateEvaluator();
        var backups = new List<BackupInfo> { new() { BackupType = "Full", LastBackupDate = DateTime.Now.AddHours(-2) } };

        var alerts = evaluator.Evaluate("TestDb", [], [], backups, null);

        Assert.Empty(alerts);
    }

    [Fact]
    public void Evaluate_LowDiskSpace_RaisesCriticalAlert()
    {
        var evaluator = CreateEvaluator();
        var server = new ServerMetrics
        {
            Volumes = [new DiskVolumeInfo { VolumePath = "C:\\", TotalMB = 100_000, FreeMB = 5_000 }]
        };

        var alerts = evaluator.Evaluate("TestDb", [], [], [], server);

        Assert.Single(alerts);
        Assert.Equal("disk-low", alerts[0].Code);
    }

    [Fact]
    public void Evaluate_HealthyDiskSpace_NoAlert()
    {
        var evaluator = CreateEvaluator();
        var server = new ServerMetrics
        {
            Volumes = [new DiskVolumeInfo { VolumePath = "C:\\", TotalMB = 100_000, FreeMB = 50_000 }]
        };

        var alerts = evaluator.Evaluate("TestDb", [], [], [], server);

        Assert.Empty(alerts);
    }
}
