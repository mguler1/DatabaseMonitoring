namespace DatabaseMonitoring.Web.Models;

public class BackupInfo
{
    public string DatabaseName { get; set; } = "";
    public string BackupType { get; set; } = "";
    public DateTime? LastBackupDate { get; set; }
    public double BackupSizeMB { get; set; }
    public string? BackupPath { get; set; }
    public string Status { get; set; } = "";
}
