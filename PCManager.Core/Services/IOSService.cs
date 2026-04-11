namespace PCManager.Core.Services;

public record ProcessInfo(int Id, string Name, double MemoryMb, double CpuUsage = 0);

public interface IOSService
{
    Task<double> GetCpuUsageAsync();
    Task<double> GetRamUsageMbAsync();
    Task<double> GetDiskUsagePercentAsync();
    Task<double> GetCpuTempAsync();
    Task<double> GetGpuTempAsync();
    Task<int> GetFanSpeedAsync();
    Task<TimeSpan> GetSystemUptimeAsync();
    Task<List<ProcessInfo>> GetActiveProcessesAsync();
    Task<bool> KillProcessAsync(int processId);
    Task<bool> PurgeTempDataAsync();
    Task ExportHardwareSpecsAsync(string exportPath);
}
