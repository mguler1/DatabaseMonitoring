namespace DatabaseMonitoring.Web.Models;

public class ConnectionEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
    public string Type { get; set; } = "mssql";
    public string ConnectionString { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public DateTime AddedAt { get; set; } = DateTime.Now;
}
