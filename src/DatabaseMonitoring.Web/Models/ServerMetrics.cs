namespace DatabaseMonitoring.Web.Models;

public class ServerMetrics
{
    public int? CpuCount { get; set; }
    public DateTime? ServerStartTime { get; set; }
    public double? PhysicalMemoryTotalMB { get; set; }
    public double? MemoryUsedMB { get; set; }
    public double? MemoryUsedPct =>
        PhysicalMemoryTotalMB is > 0 && MemoryUsedMB.HasValue
            ? MemoryUsedMB.Value / PhysicalMemoryTotalMB.Value * 100
            : null;
    public string? Edition { get; set; }
    public string? VersionInfo { get; set; }
    public List<DiskVolumeInfo> Volumes { get; set; } = [];
}

public class DiskVolumeInfo
{
    public string VolumePath { get; set; } = "";
    public double TotalMB { get; set; }
    public double FreeMB { get; set; }
    public double FreePct => TotalMB > 0 ? FreeMB / TotalMB * 100 : 0;
}
