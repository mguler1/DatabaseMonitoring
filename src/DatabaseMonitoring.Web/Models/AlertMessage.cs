namespace DatabaseMonitoring.Web.Models;

public enum AlertSeverity { Info, Warning, Critical }

public class AlertMessage
{
    public AlertSeverity Severity { get; set; }
    public string Code { get; set; } = "";
    public string Message { get; set; } = "";
}
