namespace DatabaseMonitoring.Web.Models;

public class ColumnInfo
{
    public int OrdinalPosition { get; set; }
    public string ColumnName { get; set; } = "";
    public string DataType { get; set; } = "";
    public bool IsNullable { get; set; }
    public int? MaxLength { get; set; }
    public bool IsPrimaryKey { get; set; }
    public bool IsForeignKey { get; set; }
    public string? DefaultValue { get; set; }
    public bool IsIdentity { get; set; }
}
