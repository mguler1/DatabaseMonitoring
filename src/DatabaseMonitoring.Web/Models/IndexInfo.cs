namespace DatabaseMonitoring.Web.Models;

public class IndexInfo
{
    public string TableName { get; set; } = "";
    public string SchemaName { get; set; } = "";
    public string IndexName { get; set; } = "";
    public string IndexType { get; set; } = "";
    public bool IsUnique { get; set; }
    public bool IsPrimaryKey { get; set; }
    public double FragmentationPct { get; set; }
    public long PageCount { get; set; }
    public long UserSeeks { get; set; }
    public long UserScans { get; set; }
    public long UserLookups { get; set; }
    public long UserUpdates { get; set; }
    public DateTime? LastUserSeek { get; set; }
    public string? Columns { get; set; }
}
