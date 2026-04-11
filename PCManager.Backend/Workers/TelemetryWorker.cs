using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using PCManager.Core.Services;
using PCManager.Infrastructure.Data;
using PCManager.Core.Models;
using System.Text.Json;

namespace PCManager.Backend.Workers;

public class TelemetryWorker : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILogger<TelemetryWorker> _logger;

    public TelemetryWorker(IServiceProvider serviceProvider, ILogger<TelemetryWorker> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("TelemetryWorker running...");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceProvider.CreateScope();
                var osService = scope.ServiceProvider.GetRequiredService<IOSService>();
                var netService = scope.ServiceProvider.GetRequiredService<INetworkService>();
                var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

                // 1. Log top processes
                var topProcs = await osService.GetActiveProcessesAsync();
                var cpuUsage = await osService.GetCpuUsageAsync();
                
                var log = new ResourceLog
                {
                    Timestamp = DateTime.UtcNow,
                    CpuUsage = cpuUsage,
                    RamUsage = await osService.GetRamUsageMbAsync(),
                    ProcessDumpJson = JsonSerializer.Serialize(topProcs)
                };

                dbContext.ResourceLogs.Add(log);

                // 2. Log Network TCP Connections
                var conns = await netService.GetActiveTcpConnectionsAsync();
                
                var secEvent = new SecurityEvent
                {
                    Timestamp = DateTime.UtcNow,
                    EventType = "Subnet_Connections_Sweep",
                    SeverityContext = conns.Count > 100 ? "WARNING" : "OK",
                    ActionTaken = "LOGGED",
                    DetailsJson = JsonSerializer.Serialize(conns)
                };

                dbContext.SecurityEvents.Add(secEvent);

                // Save to database
                await dbContext.SaveChangesAsync(stoppingToken);

                _logger.LogInformation($"Telemetry payload synced to SQL Server. Total DB Logs: {dbContext.ResourceLogs.Count()}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to capture system telemetry to Database.");
            }

            // High-frequency heartbeat: log to SQL Server every 1 second
            await Task.Delay(1000, stoppingToken);
        }
    }
}
