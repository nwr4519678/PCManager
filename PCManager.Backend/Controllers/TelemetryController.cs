using Microsoft.AspNetCore.Mvc;
using PCManager.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace PCManager.Backend.Controllers;

[ApiController]
[Route("api/[controller]")]
public class TelemetryController : ControllerBase
{
    private readonly AppDbContext _dbContext;

    public TelemetryController(AppDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    [HttpGet("stats")]
    public async Task<IActionResult> GetDbStats()
    {
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            // Ping DB to get engine latency
            await _dbContext.Database.ExecuteSqlRawAsync("SELECT 1");
            sw.Stop();

            var logsCount = await _dbContext.ResourceLogs.CountAsync();
            var secCount = await _dbContext.SecurityEvents.CountAsync();
            
            var lastLog = await _dbContext.ResourceLogs.OrderByDescending(x => x.Timestamp).FirstOrDefaultAsync();
            var lastBackup = lastLog != null ? lastLog.Timestamp.ToString("HH:mm:ss") : "N/A";

            return Ok(new { 
                totalLogs = logsCount + secCount, 
                lastBackup = lastBackup,
                healthStatus = $"ONLINE ({sw.ElapsedMilliseconds} ms ping)"
            });
        }
        catch 
        {
            return Ok(new { totalLogs = 0, lastBackup = "N/A", healthStatus = "DB OFFLINE" });
        }
    }

    public class HeartbeatPayload
    {
        public double Cpu { get; set; }
        public double Ram { get; set; }
    }

    [HttpPost("heartbeat")]
    public async Task<IActionResult> PostHeartbeat([FromBody] HeartbeatPayload payload)
    {
        try
        {
            var log = new PCManager.Core.Models.ResourceLog
            {
                Timestamp = DateTime.UtcNow,
                CpuUsage = payload.Cpu,
                RamUsage = payload.Ram,
                DiskUsagePercent = 0,
                ProcessDumpJson = "[]"
            };
            _dbContext.ResourceLogs.Add(log);
            await _dbContext.SaveChangesAsync();
            return Ok();
        }
        catch
        {
            return StatusCode(500);
        }
    }
}
