namespace DatabaseMonitoring.Web.Models;

public class TableInfo
{
    public string TableName { get; set; } = "";
    public string SchemaName { get; set; } = "dbo";
    public string FullName => $"[{SchemaName}].[{TableName}]";
    public long RowCount { get; set; }
    public double TotalSpaceMB { get; set; }
    public double UsedSpaceMB { get; set; }
    public double UnusedSpaceMB => TotalSpaceMB - UsedSpaceMB;
    public DateTime? LastQueryTime { get; set; }
    public string? LastQueryText { get; set; }
    public string DatabaseName { get; set; } = "";
}
