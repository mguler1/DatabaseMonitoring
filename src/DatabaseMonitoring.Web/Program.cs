using System.Text.Json;
using DatabaseMonitoring.Web.HealthChecks;
using DatabaseMonitoring.Web.Models;
using DatabaseMonitoring.Web.Services;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllersWithViews();
builder.Services.AddSingleton<ConnectionStore>();
builder.Services.AddScoped<DatabaseProviderFactory>();

builder.Services.Configure<MonitoringOptions>(builder.Configuration.GetSection(MonitoringOptions.SectionName));
builder.Services.AddSingleton<MetricsHistoryStore>();
builder.Services.AddScoped<AlertEvaluator>();
builder.Services.AddHttpClient<WebhookNotifier>();
builder.Services.AddHostedService<MetricsCollectorService>();

builder.Services.AddHealthChecks()
    .AddCheck<ConnectionsHealthCheck>("connections");

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();
app.UseAuthorization();
app.MapStaticAssets();

app.MapHealthChecks("/health", new HealthCheckOptions
{
    ResponseWriter = async (context, report) =>
    {
        context.Response.ContentType = "application/json";
        var payload = JsonSerializer.Serialize(new
        {
            status = report.Status.ToString(),
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                description = e.Value.Description,
                data = e.Value.Data
            })
        });
        await context.Response.WriteAsync(payload);
    }
});

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();

app.Run();
