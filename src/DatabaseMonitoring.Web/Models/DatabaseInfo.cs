namespace DatabaseMonitoring.Web.Models;

public class DatabaseInfo
{
    public string Name { get; set; } = "";
    public string State { get; set; } = "ONLINE";
    public DateTime? CreateDate { get; set; }
    public double SizeMB { get; set; }
    public int TableCount { get; set; }
    public long TotalRows { get; set; }
    public string ProviderType { get; set; } = "mssql";
    public string ConnectionId { get; set; } = "";
    public string ConnectionName { get; set; } = "";
}
