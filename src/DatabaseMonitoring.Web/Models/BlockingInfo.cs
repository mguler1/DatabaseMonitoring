namespace DatabaseMonitoring.Web.Models;

public class BlockingInfo
{
    public int BlockedSessionId { get; set; }
    public int BlockingSessionId { get; set; }
    public string? BlockedLogin { get; set; }
    public string? BlockingLogin { get; set; }
    public string? WaitType { get; set; }
    public int WaitTimeMs { get; set; }
    public string? DatabaseName { get; set; }
    public string? BlockedQuery { get; set; }
    public string? BlockingQuery { get; set; }
}
