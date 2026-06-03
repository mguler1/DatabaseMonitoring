namespace DatabaseMonitoring.Web.Models;

public class ActiveSession
{
    public int SessionId { get; set; }
    public string? LoginName { get; set; }
    public string? HostName { get; set; }
    public string? DatabaseName { get; set; }
    public string? Status { get; set; }
    public string? Command { get; set; }
    public int? CpuTime { get; set; }
    public int? MemoryUsageKB { get; set; }
    public DateTime? LoginTime { get; set; }
    public DateTime? LastRequestTime { get; set; }
    public string? ProgramName { get; set; }
    public int? BlockingSessionId { get; set; }
    public string? WaitType { get; set; }
    public int? WaitTimeMs { get; set; }
}
