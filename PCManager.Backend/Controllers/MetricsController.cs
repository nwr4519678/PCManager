using Microsoft.AspNetCore.Mvc;
using PCManager.Core.Services;

namespace PCManager.Backend.Controllers;

[ApiController]
[Route("api/[controller]")]
public class MetricsController : ControllerBase
{
    private readonly IOSService _osService;

    public MetricsController(IOSService osService)
    {
        _osService = osService;
    }

    [HttpGet("system")]
    public async Task<IActionResult> GetSystemMetrics()
    {
    
        var cpu = await _osService.GetCpuUsageAsync();
        var ram = await _osService.GetRamUsageMbAsync();
        var disk = await _osService.GetDiskUsagePercentAsync();
        
        var cpuTemp = await _osService.GetCpuTempAsync();
        var gpuTemp = await _osService.GetGpuTempAsync();
        var fanSpeed = await _osService.GetFanSpeedAsync();
        var uptime = await _osService.GetSystemUptimeAsync();

        return Ok(new { 
            CpuUsagePercent = cpu, 
            RamUsageMb = ram, 
            DiskUsagePercent = disk,
            CpuTemp = cpuTemp, 
            GpuTemp = gpuTemp, 
            FanSpeed = fanSpeed, 
            Uptime = uptime.ToString(@"hh\:mm\:ss")
        });
    }

    [HttpPost("action/purge-temp")]
    public async Task<IActionResult> PurgeTemp()
    {
        var success = await _osService.PurgeTempDataAsync();
        return success ? Ok() : StatusCode(500, "Failed to purge temp folder.");
    }
}