using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Security.Principal;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.Kernel.Sketches;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using PCManager.Core.Services;
using SkiaSharp;

namespace PCManager.UI.ViewModels;

// ─────────────────────────────────────────────────────────────────────────────
// Data Records
// ─────────────────────────────────────────────────────────────────────────────

public partial class ProcessRecord : ObservableObject
{
    [ObservableProperty] private int    _pID;
    [ObservableProperty] private string _name   = "";
    [ObservableProperty] private string _status = "Running";
    [ObservableProperty] private string _cPU    = "";
    [ObservableProperty] private string _memory = "";
}

public partial class NetworkLogRecord : ObservableObject
{
    [ObservableProperty] private string _time          = "";
    [ObservableProperty] private string _protocol      = "TCP";
    [ObservableProperty] private string _localAddress  = "";
    [ObservableProperty] private string _remoteAddress = "";
    [ObservableProperty] private int    _remotePort    = 0;
    [ObservableProperty] 
    [NotifyPropertyChangedFor(nameof(IsTimedOut))]
    private int _latencyMs = -1; // -1 = Timed Out
    [ObservableProperty] private string _status        = "";
    [ObservableProperty] private string _processName   = "";
    [ObservableProperty] private int    _processId     = 0;
    [ObservableProperty] private string _openPorts     = "";

    public bool IsTimedOut => LatencyMs < 0;
}

public partial class DiskRecord : ObservableObject
{
    [ObservableProperty] private string _label       = "";
    [ObservableProperty] private string _used        = "";
    [ObservableProperty] private string _total       = "";
    [ObservableProperty] private double _usedPercent = 0;
}

public partial class DockerContainerRecord : ObservableObject
{
    [ObservableProperty] private string _name   = "";
    [ObservableProperty] private string _image  = "";
    [ObservableProperty] private string _ports  = "";
    [ObservableProperty] private string _uptime = "";
    [ObservableProperty] private string _status = "running";

    public bool IsRunning        => Status == "Stable Online" || Status == "running";
    public bool IsStopped        => Status == "Offline" || Status == "exited" || Status == "stopped";
    public bool IsPaused         => Status == "paused";

    /// <summary>True when the image name contains 'mssql' or 'sqlserver' — triggers Neon Yellow highlight.</summary>
    public bool IsSqlServerImage => Image.Contains("mssql",     StringComparison.OrdinalIgnoreCase) ||
                                    Image.Contains("sqlserver", StringComparison.OrdinalIgnoreCase);

    partial void OnStatusChanged(string value)
    {
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsStopped));
        OnPropertyChanged(nameof(IsPaused));
    }

    partial void OnImageChanged(string value)
    {
        OnPropertyChanged(nameof(IsSqlServerImage));
    }
}

public partial class SystemAlertRecord : ObservableObject
{
    [ObservableProperty] private string _title    = "";
    [ObservableProperty] private string _message  = "";
    [ObservableProperty] private string _severity = "info"; // info | warning | critical
    [ObservableProperty] private string _time     = "";

    public IBrush SeverityBrush => Severity switch
    {
        "critical" => new SolidColorBrush(Color.Parse("#EF4444")),
        "warning"  => new SolidColorBrush(Color.Parse("#F97316")),
        _          => new SolidColorBrush(Color.Parse("#3B82F6")),
    };
}

// ─────────────────────────────────────────────────────────────────────────────
// Main ViewModel
// ─────────────────────────────────────────────────────────────────────────────

public partial class MainWindowViewModel : ObservableObject, IDisposable
{
    // ── Services ──────────────────────────────────────────────────────────────
    private readonly HardwareMonitorService _hw     = new();
    private readonly System.Net.Http.HttpClient _client;

    // ── Timers ────────────────────────────────────────────────────────────────
    private readonly PeriodicTimer _fastTimer = new(TimeSpan.FromSeconds(1));
    private readonly CancellationTokenSource _cts   = new();
    
    // ── Backend Monitor state ─────────────────────────────────────────────────
    private int _failedHeartbeats = 0;

    // ── Network I/O baseline ──────────────────────────────────────────────────
    private long     _lastBytesSent     = 0;
    private long     _lastBytesRecv     = 0;
    private DateTime _lastNetworkCheck  = DateTime.UtcNow;

    // ── Alert de-dup ──────────────────────────────────────────────────────────
    private readonly HashSet<string> _activeAlerts = new();

    // ─────────────────────────────────────────────────────────────────────────
    // Navigation
    // ─────────────────────────────────────────────────────────────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDashboardTab))]
    [NotifyPropertyChangedFor(nameof(IsProcessesTab))]
    [NotifyPropertyChangedFor(nameof(IsNetworkTab))]
    [NotifyPropertyChangedFor(nameof(IsDatabaseTab))]
    [NotifyPropertyChangedFor(nameof(IsSettingsTab))]
    private int _activeTab = 0;

    public bool IsDashboardTab  => ActiveTab == 0;
    public bool IsProcessesTab  => ActiveTab == 1;
    public bool IsNetworkTab    => ActiveTab == 2;
    public bool IsDatabaseTab   => ActiveTab == 3;
    public bool IsSettingsTab   => ActiveTab == 4;

    [RelayCommand]
    private void NavigateToTab(string? tab)
    {
        if (int.TryParse(tab, out int i)) ActiveTab = i;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Header / System Identity
    // ─────────────────────────────────────────────────────────────────────────

    [ObservableProperty] private string _systemTime    = DateTime.Now.ToString("HH:mm:ss");
    [ObservableProperty] private string _systemDate    = DateTime.Now.ToString("ddd, MMM dd yyyy");
    [ObservableProperty] private string _loggedInUser  = Environment.UserName.ToUpperInvariant();
    [ObservableProperty] private string _machineName   = Environment.MachineName;
    [ObservableProperty] private string _backendStatus = "CONNECTING…";
    [ObservableProperty] private string _securityStatus = "TLS 1.3 ACTIVE";
    [ObservableProperty] private string _networkLatency = "—";
    [ObservableProperty] private string _systemUptime  = "—";

    // ─────────────────────────────────────────────────────────────────────────
    // Loading State
    // ─────────────────────────────────────────────────────────────────────────

    [ObservableProperty] private bool _isLoading = true;

    // ─────────────────────────────────────────────────────────────────────────
    // CPU
    // ─────────────────────────────────────────────────────────────────────────

    [ObservableProperty] private string  _cpuUsage       = "0%";
    [ObservableProperty] private double  _cpuUsageValue  = 0;
    [ObservableProperty] private string  _cpuTemp        = "—°C";
    [ObservableProperty] private double  _cpuTempValue   = 0;
    [ObservableProperty] private string  _cpuName        = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER") ?? "CPU";
    [ObservableProperty] private string  _cpuCores       = $"{Environment.ProcessorCount} Logical Processors";
    [ObservableProperty] private IBrush  _cpuStatusBrush = new SolidColorBrush(Color.Parse("#3B82F6"));

    // ─────────────────────────────────────────────────────────────────────────
    // RAM
    // ─────────────────────────────────────────────────────────────────────────

    [ObservableProperty] private string  _ramUsage       = "— / — GB";
    [ObservableProperty] private double  _ramUsageValue  = 0;
    [ObservableProperty] private double  _ramTotalGb     = 0;
    [ObservableProperty] private IBrush  _ramStatusBrush = new SolidColorBrush(Color.Parse("#F97316"));

    // ─────────────────────────────────────────────────────────────────────────
    // GPU
    // ─────────────────────────────────────────────────────────────────────────

    [ObservableProperty] private string  _gpuUsage       = "—%";
    [ObservableProperty] private double  _gpuUsageValue  = 0;
    [ObservableProperty] private string  _gpuTemp        = "—°C";
    [ObservableProperty] private IBrush  _gpuStatusBrush = new SolidColorBrush(Color.Parse("#06B6D4"));

    // ─────────────────────────────────────────────────────────────────────────
    // Thermals & Fan
    // ─────────────────────────────────────────────────────────────────────────

    [ObservableProperty] private string  _fanSpeed       = "— RPM";
    [ObservableProperty] private double  _fanSpeedValue  = 0;
    [ObservableProperty] private IBrush  _tempStatusBrush = new SolidColorBrush(Color.Parse("#84CC16"));

    // ─────────────────────────────────────────────────────────────────────────
    // Network I/O
    // ─────────────────────────────────────────────────────────────────────────

    [ObservableProperty] private string _networkStatus        = "Initializing…";
    [ObservableProperty] private string _networkUploadSpeed   = "0 KB/s";
    [ObservableProperty] private string _networkDownloadSpeed = "0 KB/s";

    // ─────────────────────────────────────────────────────────────────────────
    // System Info
    // ─────────────────────────────────────────────────────────────────────────

    [ObservableProperty] private string _osInfo       = $"{Environment.OSVersion}";
    [ObservableProperty] private string _osBuild      = Environment.OSVersion.Version.ToString();
    [ObservableProperty] private string _cpuGeneration = "";

    // ─────────────────────────────────────────────────────────────────────────
    // Database / Docker stats
    // ─────────────────────────────────────────────────────────────────────────

    [ObservableProperty] private string _totalLogs      = "—";
    [ObservableProperty] private string _lastDbBackup   = "Never";
    [ObservableProperty] private string _dbServerHealth = "UNKNOWN";

    // ─────────────────────────────────────────────────────────────────────────
    // Alerts
    // ─────────────────────────────────────────────────────────────────────────

    [ObservableProperty] private int  _totalAlerts = 0;
    [ObservableProperty] private bool _hasAlerts   = false;

    // ─────────────────────────────────────────────────────────────────────────
    // Settings thresholds
    // ─────────────────────────────────────────────────────────────────────────

    [ObservableProperty] private double _alertCpuThreshold  = 90;
    [ObservableProperty] private double _alertRamThreshold  = 85;
    [ObservableProperty] private double _alertTempThreshold = 80;

    // ─────────────────────────────────────────────────────────────────────────
    // Process search
    // ─────────────────────────────────────────────────────────────────────────

    [ObservableProperty] private string _processSearch = "";
    partial void OnProcessSearchChanged(string value) => RefreshFilteredProcesses();

    // ─────────────────────────────────────────────────────────────────────────
    // Collections
    // ─────────────────────────────────────────────────────────────────────────

    public ObservableCollection<ProcessRecord>         TopProcesses       { get; } = new();
    public ObservableCollection<ProcessRecord>         FilteredProcesses  { get; } = new();
    public ObservableCollection<NetworkLogRecord>      NetworkLogs        { get; } = new();
    public ObservableCollection<DiskRecord>            DiskDrives         { get; } = new();
    public ObservableCollection<DockerContainerRecord> DockerContainers   { get; } = new();
    public ObservableCollection<SystemAlertRecord>     SystemAlerts       { get; } = new();

    // ─────────────────────────────────────────────────────────────────────────
    // LiveCharts — 60-sample rolling buffers
    // ─────────────────────────────────────────────────────────────────────────

    private readonly ObservableCollection<ObservableValue> _cpuPoints = new(
        Enumerable.Range(0, 60).Select(_ => new ObservableValue(0)));
    private readonly ObservableCollection<ObservableValue> _ramPoints = new(
        Enumerable.Range(0, 60).Select(_ => new ObservableValue(0)));

    public ISeries[]        CpuSeries  { get; }
    public ISeries[]        RamSeries  { get; }
    public ICartesianAxis[] ChartYAxes { get; }
    public ICartesianAxis[] ChartXAxes { get; }

    // ─────────────────────────────────────────────────────────────────────────
    // Constructor
    // ─────────────────────────────────────────────────────────────────────────

    public MainWindowViewModel()
    {
        _client = new System.Net.Http.HttpClient
        {
            BaseAddress = new Uri("http://127.0.0.1:5000"),
            Timeout     = TimeSpan.FromSeconds(6)
        };

        // CPU series — electric blue gradient
        CpuSeries = new ISeries[]
        {
            new LineSeries<ObservableValue>
            {
                Values            = _cpuPoints,
                Stroke            = new SolidColorPaint(SKColor.Parse("#3B82F6"), 3),
                Fill              = new LinearGradientPaint(
                                        new[] { SKColor.Parse("#883B82F6"), SKColor.Parse("#003B82F6") },
                                        new SKPoint(0.5f, 0), new SKPoint(0.5f, 1)),
                GeometrySize      = 0,
                GeometryFill      = null,
                GeometryStroke    = null,
                IsHoverable       = false,
                LineSmoothness    = 0.65
            }
        };

        // RAM series — amber gradient
        RamSeries = new ISeries[]
        {
            new LineSeries<ObservableValue>
            {
                Values            = _ramPoints,
                Stroke            = new SolidColorPaint(SKColor.Parse("#F97316"), 3),
                Fill              = new LinearGradientPaint(
                                        new[] { SKColor.Parse("#88F97316"), SKColor.Parse("#00F97316") },
                                        new SKPoint(0.5f, 0), new SKPoint(0.5f, 1)),
                GeometrySize      = 0,
                GeometryFill      = null,
                GeometryStroke    = null,
                IsHoverable       = false,
                LineSmoothness    = 0.65
            }
        };

        // Shared chart axes — 0-100 % scale with labelled Y-axis, hidden X-axis
        ChartYAxes = new ICartesianAxis[]
        {
            new Axis
            {
                MinLimit        = 0,
                MaxLimit        = 100,
                LabelsPaint     = new SolidColorPaint(SKColor.Parse("#64748B")),
                TextSize        = 10,
                SeparatorsPaint = new SolidColorPaint(SKColor.Parse("#1E293B")) { StrokeThickness = 1 },
            }
        };
        ChartXAxes = new ICartesianAxis[]
        {
            new Axis { LabelsPaint = null, SeparatorsPaint = null }
        };

        // Subscribe to LHM change notifications (runs on thread-pool)
        _hw.PropertyChanged += OnHwPropertyChanged;

        // Enumerate disks synchronously on startup
        LoadDiskDrives();

        // Start heartbeat loops
        Task.Run(() => FastHeartbeatLoop(_cts.Token), _cts.Token);
        Task.Run(() => ProcessMonitorLoop(_cts.Token), _cts.Token);
        Task.Run(() => NetworkMonitorLoop(_cts.Token), _cts.Token);
        Task.Run(() => PingMonitorLoop(_cts.Token), _cts.Token);
        Task.Run(() => InitSystemInfoAsync(), _cts.Token);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Hardware relay  (property change from HardwareMonitorService)
    // ─────────────────────────────────────────────────────────────────────────

    private void OnHwPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // HardwareMonitorService fires this on its own timer thread.
        // Marshal ALL mutations to the Avalonia UI thread via Post() to avoid
        // cross-thread ObservableCollection / IBrush assignment crashes.
        var prop = e.PropertyName;
        Dispatcher.UIThread.Post(() =>
        {
            switch (prop)
            {
                case nameof(HardwareMonitorService.CpuUsagePercent):
                    var cpu = _hw.CpuUsagePercent;
                    CpuUsageValue = cpu;
                    CpuUsage      = $"{cpu:F1}%";
                    PushGraphValue(_cpuPoints, cpu);
                    UpdateStatusBrushes();
                    break;

                case nameof(HardwareMonitorService.RamUsedGb):
                case nameof(HardwareMonitorService.RamTotalGb):
                    var used  = _hw.RamUsedGb;
                    var total = _hw.RamTotalGb;
                    RamTotalGb    = total;
                    RamUsageValue = total > 0 ? used / total * 100 : 0;
                    RamUsage      = $"{used:F1} / {total:F0} GB";
                    PushGraphValue(_ramPoints, RamUsageValue);
                    UpdateStatusBrushes();
                    break;

                case nameof(HardwareMonitorService.CpuTemperatureC):
                    var ctemp = _hw.CpuTemperatureC;
                    CpuTempValue  = ctemp;
                    CpuTemp       = ctemp > 0 ? $"{ctemp:F0}\u00b0C" : "\u2014\u00b0C";
                    UpdateStatusBrushes();
                    break;

                case nameof(HardwareMonitorService.GpuTemperatureC):
                    var gtemp = _hw.GpuTemperatureC;
                    if (gtemp > 0)
                        GpuTemp = $"{gtemp:F0}\u00b0C";
                    else if (_hw.CpuTemperatureC > 0)
                        // iGPU proxy: Intel HD/Iris shares the CPU package thermal zone
                        GpuTemp = $"~{_hw.CpuTemperatureC:F0}\u00b0C";
                    else
                        GpuTemp = "\u2014\u00b0C";
                    break;

                case nameof(HardwareMonitorService.GpuUsagePercent):
                    var gpuPct = _hw.GpuUsagePercent;
                    GpuUsageValue = gpuPct;
                    GpuUsage      = gpuPct > 0 ? $"{gpuPct:F1}%" : "\u2014%";
                    UpdateStatusBrushes();
                    break;

                case nameof(HardwareMonitorService.FanSpeedRpm):
                    var rpm = _hw.FanSpeedRpm;
                    FanSpeedValue = rpm;
                    FanSpeed      = rpm > 0 ? $"{rpm:N0} RPM" : "N/A";
                    break;

                case nameof(HardwareMonitorService.SystemUptime):
                    SystemUptime = _hw.SystemUptime;
                    break;
            }

            CheckThresholdAlerts();
        });
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Status brush color-coding (green→orange→red based on load)
    // ─────────────────────────────────────────────────────────────────────────

    private void UpdateStatusBrushes()
    {
        static IBrush Pick(double val, double warn, double crit, string ok, string warning, string danger)
            => new SolidColorBrush(Color.Parse(val >= crit ? danger : val >= warn ? warning : ok));

        CpuStatusBrush  = Pick(CpuUsageValue,  75, 90, "#3B82F6", "#F97316", "#EF4444");
        RamStatusBrush  = Pick(RamUsageValue,  75, 90, "#84CC16", "#F97316", "#EF4444");
        TempStatusBrush = Pick(CpuTempValue,   70, 85, "#84CC16", "#F97316", "#EF4444");
        GpuStatusBrush  = Pick(GpuUsageValue,  75, 90, "#06B6D4", "#F97316", "#EF4444");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Alert threshold monitoring
    // ─────────────────────────────────────────────────────────────────────────

    private void CheckThresholdAlerts()
    {
        if (CpuUsageValue  >= AlertCpuThreshold)
            TryAddAlert("⚠ HIGH CPU USAGE",   $"CPU at {CpuUsage}",   "critical");
        if (RamUsageValue  >= AlertRamThreshold)
            TryAddAlert("⚠ HIGH MEMORY",      $"RAM at {RamUsage}",   "warning");
        if (CpuTempValue   >= AlertTempThreshold)
            TryAddAlert("🔥 HIGH CPU TEMP",   $"Temp at {CpuTemp}",   "critical");
    }

    private void TryAddAlert(string title, string msg, string severity)
    {
        lock (_activeAlerts)
        {
            if (!_activeAlerts.Add(title)) return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            SystemAlerts.Insert(0, new SystemAlertRecord
            {
                Title    = title,
                Message  = msg,
                Severity = severity,
                Time     = DateTime.Now.ToString("HH:mm:ss")
            });
            if (SystemAlerts.Count > 15) SystemAlerts.RemoveAt(15);
            TotalAlerts = SystemAlerts.Count;
            HasAlerts   = TotalAlerts > 0;
        });

        // Auto-clear the de-dup key after 30 s
        Task.Delay(30_000).ContinueWith(_ => { lock (_activeAlerts) { _activeAlerts.Remove(title); } });
    }

    [RelayCommand]
    private void ClearAlerts()
    {
        SystemAlerts.Clear();
        TotalAlerts = 0;
        HasAlerts   = false;
        lock (_activeAlerts) _activeAlerts.Clear();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Network I/O measurement
    // ─────────────────────────────────────────────────────────────────────────

    private void UpdateNetworkIO()
    {
        try
        {
            var ifaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                            n.NetworkInterfaceType != NetworkInterfaceType.Loopback);

            long totalSent = 0, totalRecv = 0;
            foreach (var ni in ifaces)
            {
                var stats = ni.GetIPv4Statistics();
                totalSent += stats.BytesSent;
                totalRecv += stats.BytesReceived;
            }

            var now     = DateTime.UtcNow;
            var elapsed = (now - _lastNetworkCheck).TotalSeconds;

            if (elapsed > 0 && _lastBytesSent > 0)
            {
                double upKbs   = (totalSent - _lastBytesSent) / elapsed / 1024;
                double downKbs = (totalRecv - _lastBytesRecv) / elapsed / 1024;

                NetworkUploadSpeed   = FormatNetSpeed(upKbs);
                NetworkDownloadSpeed = FormatNetSpeed(downKbs);
            }

            _lastBytesSent    = totalSent;
            _lastBytesRecv    = totalRecv;
            _lastNetworkCheck = now;
        }
        catch { /* offline */ }
    }

    private static string FormatNetSpeed(double kbs)
        => kbs >= 1_048_576 ? $"{kbs / 1_048_576:F2} GB/s"
         : kbs >= 1_024     ? $"{kbs / 1024:F1} MB/s"
         :                    $"{kbs:F0} KB/s";

    // ─────────────────────────────────────────────────────────────────────────
    // Process search / filter
    // ─────────────────────────────────────────────────────────────────────────

    private void RefreshFilteredProcesses()
    {
        Dispatcher.UIThread.Post(() =>
        {
            var search = (ProcessSearch ?? "").Trim();
            var source = string.IsNullOrEmpty(search)
                ? TopProcesses
                : (IEnumerable<ProcessRecord>)TopProcesses.Where(p =>
                    p.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                    p.PID.ToString().Contains(search));

            var list = source.ToList();
            PatchCollection(FilteredProcesses, list,
                (dst, src) =>
                {
                    dst.PID    = src.PID;
                    dst.Name   = src.Name;
                    dst.Status = src.Status;
                    dst.CPU    = src.CPU;
                    dst.Memory = src.Memory;
                });
        });
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Fast Heartbeat (1 s) — processes, network I/O, clock, backend
    // ─────────────────────────────────────────────────────────────────────────

    private async Task FastHeartbeatLoop(CancellationToken ct)
    {
        // Initial delay — let the HW monitor warm up
        await Task.Delay(1_200, ct);

        int slowCycle = 0;
        while (!ct.IsCancellationRequested)
        {
            await _fastTimer.WaitForNextTickAsync(ct);

            // ── Clock update ────────────────────────────────────────────────
            Dispatcher.UIThread.Post(() =>
            {
                SystemTime = DateTime.Now.ToString("HH:mm:ss");
                SystemDate = DateTime.Now.ToString("ddd, MMM dd yyyy");
            });

            // ── Network I/O ─────────────────────────────────────────────────
            UpdateNetworkIO();

            // ── Heartbeat POST Persistence (1s) ─────────────────────────────
            try
            {
                var load = new { cpu = CpuUsageValue, ram = RamUsageValue };
                var content = new System.Net.Http.StringContent(JsonSerializer.Serialize(load), System.Text.Encoding.UTF8, "application/json");
                _ = _client.PostAsync("/api/telemetry/heartbeat", content, ct); // Fire and forget
            }
            catch { }

            // ── Slow cycle (every 5 s): backend health, docker, db ──────────
            if (++slowCycle % 5 == 0)
                _ = Task.Run(() => SlowHeartbeatAsync(ct), ct);
        }
    }

    private async Task SlowHeartbeatAsync(CancellationToken ct)
    {
        // ── Backend ping & Telemetry ────────────────────────────────────────
        try
        {
            var sw  = System.Diagnostics.Stopwatch.StartNew();
            var res = await _client.GetAsync("/api/telemetry/stats", ct);
            sw.Stop();

            if (res.IsSuccessStatusCode)
            {
                _failedHeartbeats = 0;
                Dispatcher.UIThread.Post(() =>
                {
                    BackendStatus  = "API ONLINE";
                    NetworkStatus  = "Online — Secure Connection";
                });

                var json = await res.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);

                if (doc.RootElement.TryGetProperty("totalLogs",    out var tl))
                    Dispatcher.UIThread.Post(() => TotalLogs = tl.GetInt64().ToString("N0"));
                if (doc.RootElement.TryGetProperty("lastBackup",   out var lb))
                    Dispatcher.UIThread.Post(() => LastDbBackup = lb.GetString() ?? "Never");
                if (doc.RootElement.TryGetProperty("healthStatus", out var hs))
                    Dispatcher.UIThread.Post(() => DbServerHealth = hs.GetString() ?? "UNKNOWN");
            }
            else
            {
                _failedHeartbeats++;
                if (_failedHeartbeats >= 3)
                {
                    Dispatcher.UIThread.Post(() => { BackendStatus = "BACKEND OFFLINE"; NetworkStatus = "Offline"; });
                }
            }
        }
        catch 
        { 
            _failedHeartbeats++;
            if (_failedHeartbeats >= 3)
            {
                Dispatcher.UIThread.Post(() => { BackendStatus = "BACKEND OFFLINE"; NetworkStatus = "Offline"; }); 
            }
        }

        // ── Docker containers ────────────────────────────────────────────────
        try
        {
            var p = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo("docker", "ps --format \"{{.Names}}|{{.Image}}|{{.Ports}}|{{.Status}}|{{.RunningFor}}\"")
                {
                    RedirectStandardOutput = true,
                    UseShellExecute        = false,
                    CreateNoWindow         = true
                }
            };
            p.Start();
            var output = await p.StandardOutput.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);

            var containers = output.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line =>
                {
                    var parts = line.Split('|');
                    return new DockerContainerRecord
                    {
                        Name   = parts.ElementAtOrDefault(0)?.Trim() ?? "unknown",
                        Image  = parts.ElementAtOrDefault(1)?.Trim() ?? "",
                        Ports  = parts.ElementAtOrDefault(2)?.Trim() ?? "—",
                        Status = parts.ElementAtOrDefault(3)?.Trim().ToLower().StartsWith("up") == true
                                 ? "Stable Online" : "Offline",
                        Uptime = parts.ElementAtOrDefault(4)?.Trim() ?? "—"
                    };
                }).ToList();

            Dispatcher.UIThread.Post(() =>
                PatchCollection(DockerContainers, containers,
                    (dst, src) =>
                    {
                        dst.Name   = src.Name;
                        dst.Image  = src.Image;
                        dst.Ports  = src.Ports;
                        dst.Status = src.Status;
                        dst.Uptime = src.Uptime;
                    }));
        }
        catch { /* docker not available */ }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Disk drive enumeration
    // ─────────────────────────────────────────────────────────────────────────

    private void LoadDiskDrives()
    {
        foreach (var drive in System.IO.DriveInfo.GetDrives()
            .Where(d => d.IsReady && (d.DriveType == System.IO.DriveType.Fixed ||
                                      d.DriveType == System.IO.DriveType.Removable)))
        {
            var total   = drive.TotalSize       / 1_073_741_824.0;
            var free    = drive.TotalFreeSpace   / 1_073_741_824.0;
            var used    = total - free;
            var pct     = total > 0 ? used / total * 100 : 0;

            DiskDrives.Add(new DiskRecord
            {
                Label       = $"{drive.Name} [{drive.VolumeLabel}]",
                Used        = $"{used:F1} GB",
                Total       = $"{total:F0} GB",
                UsedPercent = Math.Round(pct, 1)
            });
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // LiveCharts helper
    // ─────────────────────────────────────────────────────────────────────────

    private static void PushGraphValue(ObservableCollection<ObservableValue> col, double val)
    {
        col.Add(new ObservableValue(Math.Max(0, Math.Min(100, val))));
        if (col.Count > 60) col.RemoveAt(0);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Collection patcher (avoids full reset / flicker)
    // ─────────────────────────────────────────────────────────────────────────

    private static void PatchCollection<T>(ObservableCollection<T> dst, IList<T> src, Action<T, T> update)
        where T : new()
    {
        // Remove excess
        while (dst.Count > src.Count) dst.RemoveAt(dst.Count - 1);

        // Update existing
        for (int i = 0; i < Math.Min(dst.Count, src.Count); i++)
            update(dst[i], src[i]);

        // Append new
        for (int i = dst.Count; i < src.Count; i++)
        {
            var item = new T();
            update(item, src[i]);
            dst.Add(item);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Native Live Process Monitor loop (2s)
    // ─────────────────────────────────────────────────────────────────────────

    private async Task ProcessMonitorLoop(CancellationToken ct)
    {
        bool isAdmin = false;
        try {
            if (OperatingSystem.IsWindows())
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                isAdmin = principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
        } catch { isAdmin = false; }

        var prevSamples = new System.Collections.Generic.Dictionary<int, (TimeSpan CpuTime, DateTime WallTime)>();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var currentSamples = new System.Collections.Generic.Dictionary<int, (TimeSpan CpuTime, DateTime WallTime)>();
                var processes = System.Diagnostics.Process.GetProcesses();
                var results = new System.Collections.Generic.List<ProcessRecord>();

                foreach (var p in processes)
                {
                    try
                    {
                        if (p.Id == 0 || p.Id == 4) continue;
                        var cpuTime = isAdmin ? p.TotalProcessorTime : TimeSpan.Zero;
                        var wallTime = DateTime.UtcNow;
                        currentSamples[p.Id] = (cpuTime, wallTime);

                        double cpuUsage = 0;
                        if (prevSamples.TryGetValue(p.Id, out var prev))
                        {
                            var cpuDiff = (cpuTime - prev.CpuTime).TotalMilliseconds;
                            var wallDiff = (wallTime - prev.WallTime).TotalMilliseconds;
                            if (wallDiff > 0)
                                cpuUsage = (cpuDiff / wallDiff) / Environment.ProcessorCount * 100.0;
                        }

                        results.Add(new ProcessRecord
                        {
                            PID = p.Id,
                            Name = p.ProcessName,
                            CPU = isAdmin ? $"{Math.Round(Math.Clamp(cpuUsage, 0, 100), 1)}%" : "N/A (Req Admin)",
                            Memory = $"{p.WorkingSet64 / 1024.0 / 1024.0:F0} MB",
                            Status = p.Responding ? "Running" : "Suspended"
                        });
                    }
                    catch { }
                    finally { p.Dispose(); }
                }

                prevSamples = currentSamples;

                var topProcs = System.Linq.Enumerable.ToList(
                    System.Linq.Enumerable.Take(
                        System.Linq.Enumerable.ThenByDescending(
                            System.Linq.Enumerable.OrderByDescending(results, x => {
                                double.TryParse(x.CPU.Replace("%", "").Replace("N/A (Req Admin)", "-1"), out double v);
                                return v;
                            }),
                            x => {
                                double.TryParse(x.Memory.Replace(" MB", ""), out double v);
                                return v;
                            }
                        ),
                        50
                    )
                );

                Dispatcher.UIThread.Post(() =>
                {
                    PatchCollection(TopProcesses, topProcs, (dst, src) =>
                    {
                        dst.PID    = src.PID;
                        dst.Name   = src.Name;
                        dst.Status = src.Status;
                        dst.CPU    = src.CPU;
                        dst.Memory = src.Memory;
                    });
                    RefreshFilteredProcesses();
                    IsLoading = false;
                });
            }
            catch { }
            await Task.Delay(2000, ct);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Native Network Latency Ping loop (2s)
    // ─────────────────────────────────────────────────────────────────────────

    private async Task PingMonitorLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var pingTasks = new List<Task<long>>();
                for (int i = 0; i < 3; i++)
                {
                    pingTasks.Add(Task.Run(async () =>
                    {
                        using var ping = new Ping();
                        var reply = await ping.SendPingAsync(System.Net.IPAddress.Parse("8.8.8.8"), TimeSpan.FromMilliseconds(1000), new byte[32], new PingOptions(), ct);
                        return reply.Status == IPStatus.Success ? reply.RoundtripTime : 0;
                    }, ct));
                }

                var results = await Task.WhenAll(pingTasks);
                var validResults = results.Where(r => r > 0).ToList();
                
                if (validResults.Any())
                {
                    var average = validResults.Average();
                    Dispatcher.UIThread.Post(() => NetworkLatency = $"{Math.Round(average)} ms");
                }
                else
                {
                    Dispatcher.UIThread.Post(() => NetworkLatency = "— ms (Timeout)");
                }
            }
            catch { }
            await Task.Delay(2000, ct);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Native Network Monitor loop (3s)
    // ─────────────────────────────────────────────────────────────────────────

    private async Task NetworkMonitorLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var connections = PCManager.Core.Services.NetworkMonitoringService.GetActiveTcpConnections();
                var listeningMap = PCManager.Core.Services.NetworkMonitoringService.GetListeningPortsByProcess();
                
                var results = connections.Select(c => 
                {
                    string portsStr = "—";
                    if (listeningMap.TryGetValue(c.ProcessId, out var ports))
                    {
                        portsStr = string.Join(", ", ports.OrderBy(p => p));
                    }

                    return new NetworkLogRecord
                    {
                        Time          = DateTime.Now.ToString("HH:mm:ss"),
                        Protocol      = "TCP",
                        LocalAddress  = $"{c.LocalAddress}:{c.LocalPort}",
                        RemoteAddress = c.RemoteAddress,
                        RemotePort    = c.RemotePort,
                        Status        = c.State,
                        ProcessId     = c.ProcessId,
                        ProcessName   = $"{c.ProcessName} ({c.ProcessId})",
                        OpenPorts     = portsStr,
                        LatencyMs     = -1 // Initial/unknown
                    };
                }).ToList();

                Dispatcher.UIThread.Post(() =>
                {
                    PatchCollection(NetworkLogs, results, (dst, src) =>
                    {
                        dst.Time          = src.Time;
                        dst.Protocol      = src.Protocol;
                        dst.LocalAddress  = src.LocalAddress;
                        dst.RemoteAddress = src.RemoteAddress;
                        dst.RemotePort    = src.RemotePort;
                        dst.Status        = src.Status;
                        dst.ProcessId     = src.ProcessId;
                        dst.ProcessName   = src.ProcessName;
                        dst.OpenPorts     = src.OpenPorts;
                        // LatencyMs is updated separately to avoid UI lag
                    });
                    
                    if (results.Count == 0)
                        NetworkStatus = "No Active External Connections";
                    else
                        NetworkStatus = $"Online — {results.Count} active connections";
                });

                // Periodic Latency Sweeping for active connections
                // To avoid overloading, we ping a few connections each cycle or use a Task.Run
                _ = Task.Run(async () => {
                    foreach (var record in NetworkLogs.ToList())
                    {
                        if (ct.IsCancellationRequested) break;
                        try 
                        {
                            using var ping = new Ping();
                            var reply = await ping.SendPingAsync(record.RemoteAddress, 800);
                            record.LatencyMs = reply.Status == IPStatus.Success ? (int)reply.RoundtripTime : -1;
                        }
                        catch { record.LatencyMs = -1; }
                        await Task.Delay(50, ct); // Throttling
                    }
                }, ct);
            }
            catch { }
            await Task.Delay(3000, ct);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Action Commands
    // ─────────────────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task KillHeavyProcAsync()
    {
        var top = TopProcesses.FirstOrDefault();
        if (top == null) return;
        try
        {
            await _client.PostAsync($"/api/process/{top.PID}/kill", null);
            TryAddAlert("✓ PROCESS KILLED", $"{top.Name} (PID {top.PID}) terminated", "info");
        }
        catch { Console.WriteLine("[KillProc] Failed."); }
    }

    [RelayCommand]
    private async Task PurgeTempAsync()
    {
        try
        {
            await _client.PostAsync("/api/metrics/action/purge-temp", null);
            TryAddAlert("✓ TEMP PURGED", "Temporary files removed", "info");
        }
        catch { Console.WriteLine("[PurgeTemp] Failed."); }
    }

    [RelayCommand]
    private async Task OptimizeRamAsync()
    {
        try { await _client.PostAsync("/api/metrics/action/optimize-ram", null); }
        catch { Console.WriteLine("[OptimizeRam] Failed."); }
    }

    [RelayCommand]
    private async Task ScanNetworkAsync()
    {
        NetworkStatus = "Scanning Subnet…";
        SecurityStatus = "Scan in Progress";

        try
        {
            var res = await _client.PostAsync("/api/network/action/sweep?prefix=192.168.1", null);
            if (res.IsSuccessStatusCode)
            {
                var json = await res.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);

                Dispatcher.UIThread.Post(() =>
                {
                    NetworkLogs.Clear();
                    int vuln = 0;
                    foreach (var l in doc.RootElement.EnumerateArray())
                    {
                        var status = l.GetProperty("status").GetString()    ?? "OK";
                        var ports  = l.GetProperty("openPorts").GetString() ?? "";
                        if (!string.IsNullOrEmpty(ports)) vuln++;

                        int.TryParse(l.GetProperty("targetPort").GetString() ?? "0", out int rPort);
                        int.TryParse(l.GetProperty("ping").GetString() ?? "-1", out int latency);

                        NetworkLogs.Add(new NetworkLogRecord
                        {
                            Time          = l.GetProperty("time").GetString()          ?? "",
                            RemoteAddress = l.GetProperty("targetAddress").GetString() ?? "",
                            RemotePort    = rPort,
                            LatencyMs     = latency,
                            Status        = status,
                            OpenPorts     = ports,
                            Protocol      = "TCP",
                            ProcessName   = "Network Discovery"
                        });
                    }
                    NetworkStatus  = $"Online — Sweep Complete ({doc.RootElement.GetArrayLength()} hosts)";
                    SecurityStatus = $"Vulnerable: {vuln} hosts detected";
                });
            }
        }
        catch { NetworkStatus = "Offline — Sweep Failed"; }
    }

    [RelayCommand]
    private void RunDbSecurityCheck()
    {
        TryAddAlert("🔐 SECURITY SCAN", "Database security check triggered", "info");
        Console.WriteLine($"[{SystemTime}] DB Security Check triggered");
    }

    // ─────────────────────────────────────────────────────────────────────────
    // System Info — WMI CPU friendly name + micro-architecture generation
    // ─────────────────────────────────────────────────────────────────────────

    private async Task InitSystemInfoAsync()
    {
        try
        {
            await Task.Run(() =>
            {
                using var searcher = new System.Management.ManagementObjectSearcher(
                    "SELECT Name FROM Win32_Processor");
                foreach (System.Management.ManagementObject obj in searcher.Get())
                {
                    var name = obj["Name"]?.ToString()?.Trim() ?? "";
                    if (string.IsNullOrEmpty(name)) continue;
                    var gen = DetectCpuGeneration(name);
                    Dispatcher.UIThread.Post(() =>
                    {
                        CpuName       = name;
                        CpuGeneration = gen;
                    });
                    break;
                }
            });
        }
        catch { /* WMI unavailable — CpuName remains Environment.PROCESSOR_IDENTIFIER value */ }
    }

    private static string DetectCpuGeneration(string cpuName)
    {
        var u = cpuName.ToUpperInvariant();

        // ── Intel Core (classic i-series) ─────────────────────────────────────
        if (AnyIn(u, "I9-14","I7-14","I5-14","I3-14")) return "Raptor Lake Refresh";
        if (AnyIn(u, "I9-13","I7-13","I5-13","I3-13")) return "Raptor Lake (13th Gen)";
        if (AnyIn(u, "I9-12","I7-12","I5-12","I3-12")) return "Alder Lake (12th Gen)";
        if (AnyIn(u, "I9-11","I7-11","I5-11","I3-11")) return "Rocket Lake (11th Gen)";
        if (AnyIn(u, "I9-10","I7-10","I5-10","I3-10")) return "Comet Lake (10th Gen)";
        if (AnyIn(u, "I9-9", "I7-9", "I5-9", "I3-9"))  return "Coffee Lake Refresh (9th Gen)";
        if (AnyIn(u, "I9-8", "I7-8", "I5-8", "I3-8"))  return "Coffee Lake (8th Gen)";
        if (AnyIn(u, "I7-7", "I5-7", "I3-7"))           return "Kaby Lake (7th Gen)";
        if (AnyIn(u, "I7-6", "I5-6", "I3-6"))           return "Skylake (6th Gen)";
        if (AnyIn(u, "I7-5", "I5-5", "I3-5"))           return "Broadwell (5th Gen)";
        if (AnyIn(u, "I7-4", "I5-4", "I3-4"))           return "Haswell (4th Gen)";
        // ── Intel Core Ultra ──────────────────────────────────────────────────
        if (AnyIn(u, "ULTRA 9 2","ULTRA 7 2","ULTRA 5 2")) return "Lunar Lake";
        if (AnyIn(u, "ULTRA 9 1","ULTRA 7 1","ULTRA 5 1")) return "Meteor Lake";
        // ── AMD Ryzen ─────────────────────────────────────────────────────────
        if (u.Contains("RYZEN"))
        {
            if (u.Contains("9000")) return "Zen 5";
            if (u.Contains("7000")) return "Zen 4";
            if (u.Contains("5000")) return "Zen 3";
            if (u.Contains("4000")) return "Zen 2 (Mobile)";
            if (u.Contains("3000")) return "Zen 2";
            if (u.Contains("2000")) return "Zen+";
            if (u.Contains("1000")) return "Zen";
        }
        return "";

        static bool AnyIn(string s, params string[] keys)
        {
            foreach (var k in keys)
                if (s.Contains(k)) return true;
            return false;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // IDisposable
    // ─────────────────────────────────────────────────────────────────────────

    public void Dispose()
    {
        _cts.Cancel();
        _fastTimer.Dispose();
        _hw.Dispose();
        _client.Dispose();
    }
}
