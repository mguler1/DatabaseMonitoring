namespace DatabaseMonitoring.Web.Models;

public class QueryInfo
{
    public DateTime? LastExecutionTime { get; set; }
    public long ExecutionCount { get; set; }
    public long TotalElapsedMs { get; set; }
    public long AvgElapsedMs { get; set; }
    public string QueryText { get; set; } = "";
    public string? DatabaseName { get; set; }
    public string? TableName { get; set; }
}
