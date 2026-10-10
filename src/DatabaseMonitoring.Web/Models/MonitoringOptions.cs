namespace DatabaseMonitoring.Web.Models;

public class MonitoringOptions
{
    public const string SectionName = "Monitoring";

    public int LongRunningQuerySeconds { get; set; } = 30;
    public int BlockingWaitMsCritical { get; set; } = 5000;
    public int BackupStaleHours { get; set; } = 26;
    public double DiskFreePctCritical { get; set; } = 10;
    public int CollectorIntervalMinutes { get; set; } = 5;
    public int HistoryRetentionDays { get; set; } = 30;
    public string WebhookUrl { get; set; } = "";
}
