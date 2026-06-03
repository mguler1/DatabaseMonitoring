namespace DatabaseMonitoring.Web.Models;

public class DatabaseDetailsViewModel
{
    public DatabaseInfo Database { get; set; } = new();
    public IEnumerable<TableInfo> Tables { get; set; } = [];
    public IEnumerable<QueryInfo> RecentQueries { get; set; } = [];
    public IEnumerable<ActiveSession> ActiveSessions { get; set; } = [];
    public IEnumerable<LongRunningQuery> LongRunningQueries { get; set; } = [];
    public IEnumerable<BlockingInfo> BlockingQueries { get; set; } = [];
    public IEnumerable<IndexInfo> Indexes { get; set; } = [];
    public IEnumerable<BackupInfo> BackupStatus { get; set; } = [];
    public ConnectionEntry Connection { get; set; } = new();
    public string? ErrorMessage { get; set; }
    public Dictionary<string, string> WarningMessages { get; set; } = [];
}

public class TableQueriesViewModel
{
    public string DatabaseName { get; set; } = "";
    public string TableName { get; set; } = "";
    public IEnumerable<QueryInfo> Queries { get; set; } = [];
    public ConnectionEntry Connection { get; set; } = new();
}
