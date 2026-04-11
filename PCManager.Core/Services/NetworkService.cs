using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Collections.Concurrent;

namespace PCManager.Core.Services;

public class NetworkService : INetworkService
{
    public async Task<double> GetPingLatencyAsync(string host = "1.1.1.1")
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        try
        {
            // Resolve host to IPAddress as required by the .NET 10 SendPingAsync overload
            IPAddress? address = null;
            if (!IPAddress.TryParse(host, out address))
            {
                var addresses = await Dns.GetHostAddressesAsync(host);
                address = addresses.FirstOrDefault();
            }

            if (address == null) return -1;

            using var ping = new Ping();
            var reply = await ping.SendPingAsync(address, TimeSpan.FromMilliseconds(1000), null, null, cts.Token);
            if (reply.Status == IPStatus.Success)
            {
                return reply.RoundtripTime;
            }
            return -1;
        }
        catch (OperationCanceledException)
        {
            return -1;
        }
        catch
        {
            return -1;
        }
    }

    public async Task<List<int>> ScanOpenPortsAsync(string ipAddress, int[] portsToScan)
    {
        var openPorts = new ConcurrentBag<int>();
        var tasks = portsToScan.Select(async port =>
        {
            try
            {
                using var client = new TcpClient();
                var connectTask = client.ConnectAsync(ipAddress, port);
                if (await Task.WhenAny(connectTask, Task.Delay(500)) == connectTask)
                {
                    if (client.Connected)
                    {
                        openPorts.Add(port);
                    }
                }
            }
            catch { }
        });

        await Task.WhenAll(tasks);
        return openPorts.ToList();
    }

    public async Task BlockIpViaHostsAsync(string ipToBlock)
    {
        var hostsPath = OperatingSystem.IsWindows() 
            ? @"C:\Windows\System32\drivers\etc\hosts" 
            : "/etc/hosts";
            
        try
        {
            var entry = $"0.0.0.0 {ipToBlock}";
            var lines = await File.ReadAllLinesAsync(hostsPath);
            if (!lines.Any(l => l.Contains(ipToBlock)))
            {
                await File.AppendAllLinesAsync(hostsPath, new[] { entry });
            }
        }
        catch { }
    }

    public async Task<List<string>> DiscoverLocalDevicesAsync(string subnetPrefix)
    {
        var activeIps = new ConcurrentBag<string>();
        
        var tasks = Enumerable.Range(1, 254).Select(async i =>
        {
            try
            {
                var ip = $"{subnetPrefix}.{i}";
                using var p = new Ping();
                var res = await p.SendPingAsync(ip, 200);
                if (res.Status == IPStatus.Success)
                {
                    activeIps.Add(ip);
                }
            }
            catch { }
        });

        await Task.WhenAll(tasks);
        return activeIps.ToList();
    }

    private List<ActiveConnectionInfo> _cachedConnections = new();
    private readonly object _lock = new();

    public NetworkService()
    {
        // Start a background polling loop for active connections (every 2 seconds for more responsiveness)
        Task.Run(async () => {
            while(true)
            {
                try
                {
                    var props = IPGlobalProperties.GetIPGlobalProperties();
                    var tcpConns = props.GetActiveTcpConnections();
                    var tcpListeners = props.GetActiveTcpListeners();
                    var udpListeners = props.GetActiveUdpListeners();
                    
                    var newConnections = tcpConns.Select(c => {
                        var remote = c.RemoteEndPoint.ToString();
                        int lastColon = remote.LastIndexOf(':');
                        return new ActiveConnectionInfo
                        {
                            LocalEndPoint = c.LocalEndPoint.ToString(),
                            RemoteAddress = lastColon > -1 ? remote.Substring(0, lastColon) : remote,
                            RemotePort    = lastColon > -1 ? remote.Substring(lastColon + 1) : "-",
                            State         = c.State.ToString()
                        };
                    }).ToList();

                    // Add listeners for visibility
                    foreach(var l in tcpListeners)
                    {
                        var ep = l.ToString();
                        int lastColon = ep.LastIndexOf(':');
                        newConnections.Add(new ActiveConnectionInfo { 
                            LocalEndPoint = ep, 
                            RemoteAddress = "0.0.0.0", 
                            RemotePort = lastColon > -1 ? ep.Substring(lastColon + 1) : "*", 
                            State = "LISTENING" 
                        });
                    }
                    
                    foreach(var l in udpListeners)
                    {
                        var ep = l.ToString();
                        int lastColon = ep.LastIndexOf(':');
                        newConnections.Add(new ActiveConnectionInfo { 
                            LocalEndPoint = ep, 
                            RemoteAddress = "*", 
                            RemotePort = lastColon > -1 ? ep.Substring(lastColon + 1) : "*", 
                            State = "UDP" 
                        });
                    }

                    lock(_lock)
                    {
                        _cachedConnections = newConnections;
                    }
                }
                catch { }
                await Task.Delay(2000);
            }
        });
    }

    public Task<List<ActiveConnectionInfo>> GetActiveTcpConnectionsAsync()
    {
        lock(_lock)
        {
            return Task.FromResult(new List<ActiveConnectionInfo>(_cachedConnections));
        }
    }
}
