using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PCManager.Core.Services;

public class ActiveConnectionInfo
{
    public string LocalEndPoint { get; set; } = string.Empty;
    public string RemoteAddress { get; set; } = string.Empty;
    public string RemotePort    { get; set; } = string.Empty;
    public string State         { get; set; } = string.Empty;
}

public interface INetworkService
{
    Task<double> GetPingLatencyAsync(string host = "1.1.1.1");
    Task<List<int>> ScanOpenPortsAsync(string ipAddress, int[] portsToScan);
    Task BlockIpViaHostsAsync(string ipToBlock);
    Task<List<string>> DiscoverLocalDevicesAsync(string subnetPrefix);
    Task<List<ActiveConnectionInfo>> GetActiveTcpConnectionsAsync();
}
