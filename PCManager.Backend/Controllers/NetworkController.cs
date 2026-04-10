using Microsoft.AspNetCore.Mvc;
using PCManager.Core.Services;

namespace PCManager.Backend.Controllers;

[ApiController]
[Route("api/[controller]")]
public class NetworkController : ControllerBase
{
    private readonly INetworkService _networkService;

    public NetworkController(INetworkService networkService)
    {
        _networkService = networkService;
    }

    [HttpGet("ping")]
    public async Task<IActionResult> GetPing([FromQuery] string host = "8.8.8.8")
    {
        var latency = await _networkService.GetPingLatencyAsync(host);
        return Ok(new { Host = host, LatencyMs = latency });
    }

    [HttpPost("scan")]
    public async Task<IActionResult> ScanPorts([FromBody] ScanRequest request)
    {
        var ports = await _networkService.ScanOpenPortsAsync(request.IpAddress, request.Ports);
        return Ok(new { OpenPorts = ports });
    }

    [HttpPost("action/sweep")]
    public async Task<IActionResult> SweepSubnet([FromQuery] string prefix = "192.168.1")
    {
        var devices = await _networkService.DiscoverLocalDevicesAsync(prefix);
        var logs = new List<object>();

        foreach(var ip in devices.Take(5)) // Take top 5 for UI performance in logs
        {
            var ports = await _networkService.ScanOpenPortsAsync(ip, new[] { 80, 443, 22 });
            logs.Add(new {
                Time = DateTime.Now.ToString("HH:mm:ss"),
                TargetIP = ip,
                Ping = await _networkService.GetPingLatencyAsync(ip) + " ms",
                Status = ports.Any() ? "VULNERABLE" : "OK",
                OpenPorts = string.Join(", ", ports)
            });
        }
        
        return Ok(logs);
    }
    [HttpGet("connections")]
    public async Task<IActionResult> GetActiveConnections()
    {
        var connections = await _networkService.GetActiveTcpConnectionsAsync();
        var topConnections = connections.Where(c => c.RemoteAddress != "0.0.0.0" && c.RemoteAddress != "*" && c.RemoteAddress != "::").Take(10).ToList();
        
        var tasks = topConnections.Select(async c => {
            var ip = c.RemoteAddress;
            string pingVal = "-";
            if (ip != "127.0.0.1" && ip != "0.0.0.0" && ip != "::1" && !string.IsNullOrEmpty(ip))
            {
                var latency = await _networkService.GetPingLatencyAsync(ip);
                pingVal = latency > 0 ? $"{latency} ms" : "Timeout";
            }

            return new {
                Time = DateTime.Now.ToString("HH:mm:ss"),
                TargetAddress = c.RemoteAddress,
                TargetPort = c.RemotePort,
                Ping = pingVal,
                Status = c.State,
                OpenPorts = ""
            };
        });

        var results = await Task.WhenAll(tasks);
        return Ok(results);
    }
}

public class ScanRequest { public string IpAddress { get; set; } = "127.0.0.1"; public int[] Ports { get; set; } = Array.Empty<int>(); }
