using DatabaseMonitoring.Web.Services;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace DatabaseMonitoring.Web.HealthChecks;

/// <summary>
/// Kayıtlı aktif her bağlantıyı açmayı deneyerek genel sağlık durumunu raporlar.
/// Tümü başarılıysa Healthy, hiçbiri başarılı değilse Unhealthy, aradaysa Degraded döner.
/// </summary>
public class ConnectionsHealthCheck : IHealthCheck
{
    private readonly ConnectionStore _store;

    public ConnectionsHealthCheck(ConnectionStore store)
    {
        _store = store;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        var connections = _store.GetActive();
        if (connections.Count == 0)
            return HealthCheckResult.Healthy("Aktif bağlantı tanımlı değil.");

        var data = new Dictionary<string, object>();
        var failures = 0;

        foreach (var conn in connections)
        {
            try
            {
                if (conn.Type == "postgresql")
                {
                    await using var npg = new NpgsqlConnection(conn.ConnectionString);
                    await npg.OpenAsync(ct);
                }
                else
                {
                    await using var sql = new SqlConnection(conn.ConnectionString);
                    await sql.OpenAsync(ct);
                }
                data[conn.Name] = "ok";
            }
            catch (Exception ex)
            {
                failures++;
                data[conn.Name] = $"hata: {ex.Message}";
            }
        }

        if (failures == 0)
            return HealthCheckResult.Healthy("Tüm bağlantılar erişilebilir.", data);

        if (failures == connections.Count)
            return HealthCheckResult.Unhealthy("Hiçbir bağlantıya erişilemiyor.", data: data);

        return HealthCheckResult.Degraded($"{failures}/{connections.Count} bağlantıya erişilemiyor.", data: data);
    }
}
