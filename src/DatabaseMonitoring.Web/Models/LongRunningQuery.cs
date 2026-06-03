namespace DatabaseMonitoring.Web.Models;

public class LongRunningQuery
{
    public int SessionId { get; set; }
    public string? LoginName { get; set; }
    public string? DatabaseName { get; set; }
    public string? Status { get; set; }
    public string? Command { get; set; }
    public int ElapsedSeconds { get; set; }
    public string? QueryText { get; set; }
    public int? WaitTimeMs { get; set; }
    public string? WaitType { get; set; }
    public int? BlockingSessionId { get; set; }
    public int? CpuTime { get; set; }
    public long? LogicalReads { get; set; }
}
