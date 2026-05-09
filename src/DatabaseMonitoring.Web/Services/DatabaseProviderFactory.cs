using DatabaseMonitoring.Web.Interfaces;
using DatabaseMonitoring.Web.Models;
using DatabaseMonitoring.Web.Providers;

namespace DatabaseMonitoring.Web.Services;

public class DatabaseProviderFactory
{
    private readonly ConnectionStore _store;
    private readonly ILoggerFactory _loggerFactory;

    public DatabaseProviderFactory(ConnectionStore store, ILoggerFactory loggerFactory)
    {
        _store = store;
        _loggerFactory = loggerFactory;
    }

    public IDatabaseProvider GetById(string connectionId)
    {
        var entry = _store.GetById(connectionId)
            ?? throw new KeyNotFoundException($"'{connectionId}' ID'li bağlantı bulunamadı");
        return ForEntry(entry);
    }

    public IDatabaseProvider ForEntry(ConnectionEntry entry)
    {
        return entry.Type.ToLowerInvariant() switch
        {
            "mssql"      => new MsSqlProvider(entry.ConnectionString, _loggerFactory.CreateLogger<MsSqlProvider>()),
            "postgresql" => new PostgreSqlProvider(entry.ConnectionString, _loggerFactory.CreateLogger<PostgreSqlProvider>()),
            _            => throw new NotSupportedException($"Provider tipi '{entry.Type}' desteklenmiyor")
        };
    }
}
