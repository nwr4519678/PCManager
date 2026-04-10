namespace PCManager.Core.Models;

public class ResourceLog
{
    public int Id { get; set; }
    public DateTime Timestamp { get; set; }
    public double CpuUsage { get; set; }
    public double RamUsage { get; set; }
    public double DiskUsagePercent { get; set; }
    public string ProcessDumpJson { get; set; } = "[]";
}
