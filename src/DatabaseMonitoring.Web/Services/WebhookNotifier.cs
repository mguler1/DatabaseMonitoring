using System.Text;
using System.Text.Json;
using DatabaseMonitoring.Web.Models;
using Microsoft.Extensions.Options;

namespace DatabaseMonitoring.Web.Services;

/// <summary>
/// Yapılandırılmış bir webhook URL'sine (Slack/Teams uyumlu basit JSON gövde) uyarı gönderir.
/// Monitoring:WebhookUrl boşsa sessizce devre dışıdır.
/// </summary>
public class WebhookNotifier
{
    private readonly HttpClient _http;
    private readonly MonitoringOptions _options;
    private readonly ILogger<WebhookNotifier> _logger;

    public WebhookNotifier(HttpClient http, IOptions<MonitoringOptions> options, ILogger<WebhookNotifier> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    public bool IsEnabled => !string.IsNullOrWhiteSpace(_options.WebhookUrl);

    public async Task SendAsync(IEnumerable<AlertMessage> alerts, CancellationToken ct = default)
    {
        if (!IsEnabled) return;

        var list = alerts.ToList();
        if (list.Count == 0) return;

        var text = string.Join("\n", list.Select(a => $"[{a.Severity}] {a.Message}"));
        var payload = JsonSerializer.Serialize(new { text });

        try
        {
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            var resp = await _http.PostAsync(_options.WebhookUrl, content, ct);
            if (!resp.IsSuccessStatusCode)
                _logger.LogWarning("Webhook bildirimi başarısız: {Status}", resp.StatusCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Webhook bildirimi gönderilemedi");
        }
    }
}
