namespace DatabaseMonitoring.Web.Models;

public class MetricSnapshot
{
    public DateTime Timestamp { get; set; }
    public string ConnectionId { get; set; } = "";
    public string DatabaseName { get; set; } = "";
    public double SizeMB { get; set; }
    public int TableCount { get; set; }
    public long TotalRows { get; set; }
    public int ActiveSessionCount { get; set; }
    public int LongRunningCount { get; set; }
    public int BlockingCount { get; set; }
}
