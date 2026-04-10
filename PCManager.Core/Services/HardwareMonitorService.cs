using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;

namespace PCManager.Core.Services;

// ─── LHM Visitor ─────────────────────────────────────────────────────────────

internal sealed class UpdateVisitor : IVisitor
{
    public void VisitComputer(IComputer computer)  { computer.Traverse(this); }
    public void VisitHardware(IHardware hardware)  { hardware.Update(); hardware.Traverse(this); }
    public void VisitSensor(ISensor sensor)        { }
    public void VisitParameter(IParameter param)  { }
}

// ─── Service ─────────────────────────────────────────────────────────────────

/// <summary>
/// Dual-speed hardware telemetry service.
///  • CPU (% Processor Utility) + RAM (P/Invoke)  → every 1 000 ms  (Task Manager parity)
///  • CPU temp + GPU temp + Fan RPM (LHM)         → every 3 000 ms
/// Implements <see cref="INotifyPropertyChanged"/> for direct MVVM binding.
/// All sensor reads are wrapped in try-catch to survive unresponsive drivers.
/// </summary>
public sealed class HardwareMonitorService : INotifyPropertyChanged, IDisposable
{
    // ── INotifyPropertyChanged ────────────────────────────────────────────────

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify(string name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    // ── Backing fields ────────────────────────────────────────────────────────

    private double _cpuUsagePercent;
    private double _ramUsedGb;
    private double _ramTotalGb;
    private double _cpuTemperatureC;
    private double _gpuTemperatureC;
    private double _gpuUsagePercent;
    private int    _fanSpeedRpm;
    private string _systemUptime = "00:00:00";

    // ── Public Properties ─────────────────────────────────────────────────────

    /// <summary>Frequency-scaled CPU utilisation — identical to Task Manager.</summary>
    public double CpuUsagePercent
    {
        get => _cpuUsagePercent;
        private set { if (_cpuUsagePercent == value) return; _cpuUsagePercent = value; Notify(nameof(CpuUsagePercent)); }
    }

    /// <summary>Used physical RAM in gigabytes.</summary>
    public double RamUsedGb
    {
        get => _ramUsedGb;
        private set { if (_ramUsedGb == value) return; _ramUsedGb = value; Notify(nameof(RamUsedGb)); }
    }

    /// <summary>Total installed physical RAM in gigabytes.</summary>
    public double RamTotalGb
    {
        get => _ramTotalGb;
        private set { if (_ramTotalGb == value) return; _ramTotalGb = value; Notify(nameof(RamTotalGb)); }
    }

    /// <summary>
    /// CPU temperature in °C.
    /// Fallback hierarchy (per-tick): Package → Tdie/Tctl (AMD) → max Core → any temperature sensor.
    /// </summary>
    public double CpuTemperatureC
    {
        get => _cpuTemperatureC;
        private set { if (_cpuTemperatureC == value) return; _cpuTemperatureC = value; Notify(nameof(CpuTemperatureC)); }
    }

    /// <summary>
    /// GPU temperature in °C (NVIDIA / AMD / Intel Arc).
    /// Returns 0 if no GPU sensor is available.
    /// </summary>
    public double GpuTemperatureC
    {
        get => _gpuTemperatureC;
        private set { if (Math.Abs(_gpuTemperatureC - value) < 0.05) return; _gpuTemperatureC = value; Notify(nameof(GpuTemperatureC)); }
    }

    /// <summary>GPU Core utilisation (%) from LibreHardwareMonitor Load sensor.</summary>
    public double GpuUsagePercent
    {
        get => _gpuUsagePercent;
        private set { if (Math.Abs(_gpuUsagePercent - value) < 0.1) return; _gpuUsagePercent = value; Notify(nameof(GpuUsagePercent)); }
    }

    /// <summary>Primary fan speed in RPM (first non-zero fan sensor found).</summary>
    public int FanSpeedRpm
    {
        get => _fanSpeedRpm;
        private set { if (_fanSpeedRpm == value) return; _fanSpeedRpm = value; Notify(nameof(FanSpeedRpm)); }
    }

    /// <summary>System uptime formatted as HH:MM:SS.</summary>
    public string SystemUptime
    {
        get => _systemUptime;
        private set { if (_systemUptime == value) return; _systemUptime = value; Notify(nameof(SystemUptime)); }
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    private PerformanceCounter?  _cpuCounter;
    private Computer?            _lhmComputer;
    private readonly UpdateVisitor _lhmVisitor = new();

    private readonly System.Timers.Timer _fastTimer; // 1 000 ms — CPU + RAM
    private readonly System.Timers.Timer _slowTimer; // 3 000 ms — temps + fan

    private bool _disposed;
    private const double BytesToGb = 1.0 / (1024.0 * 1024.0 * 1024.0);

    // ── Constructor ───────────────────────────────────────────────────────────

    public HardwareMonitorService()
    {
        InitCpuCounter();
        InitLhm();

        try { QueryRam(); } catch { /* prime RAM total before first tick */ }

        _fastTimer = new System.Timers.Timer(1000) { AutoReset = true };
        _fastTimer.Elapsed += (_, _) => OnFastTick();
        _fastTimer.Start();

        _slowTimer = new System.Timers.Timer(3000) { AutoReset = true };
        _slowTimer.Elapsed += (_, _) => OnSlowTick();
        _slowTimer.Start();
    }

    // ── Init ──────────────────────────────────────────────────────────────────

    private void InitCpuCounter()
    {
        try
        {
            // "% Processor Utility" is frequency-scaled (Intel Speed Shift / AMD Boost).
            // "% Processor Time" ignores P-state boosting — always under-reports on modern CPUs.
            _cpuCounter = new PerformanceCounter(
                "Processor Information", "% Processor Utility", "_Total", readOnly: true);
            _ = _cpuCounter.NextValue(); // Discard PDH warm-up zero
            Console.WriteLine("[HWMonitor] CPU counter: % Processor Utility — OK");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[HWMonitor] CPU counter failed ({ex.Message}). Fallback: 0%.");
            _cpuCounter = null;
        }
    }

    private void InitLhm()
    {
        try
        {
            _lhmComputer = new Computer
            {
                IsCpuEnabled         = true,  // CPU Package / Core temps
                IsMotherboardEnabled = true,  // Fan headers (Nuvoton, ITE, Fintek chips)
                IsGpuEnabled         = true,  // NVIDIA / AMD / Intel Arc GPU temp
                IsMemoryEnabled      = false, // RAM handled by P/Invoke
                IsStorageEnabled     = false,
            };
            _lhmComputer.Open();
            Console.WriteLine("[HWMonitor] LibreHardwareMonitor — OK");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[HWMonitor] LHM init failed ({ex.Message}).");
            _lhmComputer = null;
        }
    }

    // ── Timer Callbacks ───────────────────────────────────────────────────────

    private void OnFastTick()
    {
        try { QueryCpu();    } catch (Exception ex) { Console.WriteLine($"[HWMonitor/CPU] {ex.Message}"); }
        try { QueryRam();    } catch (Exception ex) { Console.WriteLine($"[HWMonitor/RAM] {ex.Message}"); }
        try { QueryUptime(); } catch { /* non-fatal */ }
    }

    private void OnSlowTick()
    {
        try
        {
            // Global guard: a frozen ring-0 driver will log, never crash.
            QuerySensors();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[HWMonitor/LHM] Sensor read failed — {ex.Message}");
        }
    }

    // ── CPU % Processor Utility ───────────────────────────────────────────────

    private void QueryCpu()
    {
        if (_cpuCounter is null) return;
        try 
        {
            CpuUsagePercent = Math.Clamp(Math.Round(_cpuCounter.NextValue(), 1), 0.0, 100.0);
        }
        catch 
        {
            _cpuCounter.Dispose();
            _cpuCounter = null; // Enter silent mode if counter fails mid-flight
            CpuUsagePercent = 0.0;
        }
    }

    // ── RAM via GlobalMemoryStatusEx (P/Invoke) ───────────────────────────────

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct MEMORYSTATUSEX
    {
        public uint  dwLength;
        public uint  dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    private void QueryRam()
    {
        try
        {
            var msx = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (!GlobalMemoryStatusEx(ref msx)) return;
            RamTotalGb = Math.Round(msx.ullTotalPhys * BytesToGb, 1);
            RamUsedGb  = Math.Round((msx.ullTotalPhys - msx.ullAvailPhys) * BytesToGb, 2);
        }
        catch { RamTotalGb = 0; RamUsedGb = 0; }
    }

    // ── Uptime ────────────────────────────────────────────────────────────────

    private void QueryUptime()
    {
        var ts = TimeSpan.FromMilliseconds(Environment.TickCount64);
        SystemUptime = $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
    }

    // ── LHM Sensors: CPU temp · GPU temp · Fan RPM ────────────────────────────

    private void QuerySensors()
    {
        if (_lhmComputer is null) return;

        double cpuTempC = 0;
        double gpuTempC = 0;
        double gpuLoad  = 0;
        int    fanRpm   = 0;
        bool   gotCpuT  = false;
        bool   gotGpuT  = false;
        bool   gotFan   = false;

        // UpdateVisitor walks all hardware and calls hw.Update() on each node.
        _lhmComputer.Accept(_lhmVisitor);

        foreach (var hw in _lhmComputer.Hardware)
        {
            switch (hw.HardwareType)
            {
                // ── CPU ───────────────────────────────────────────────────────
                case HardwareType.Cpu:
                    if (!gotCpuT)
                    {
                        var t = ExtractCpuTemp(hw);
                        if (t > 0) { cpuTempC = t; gotCpuT = true; }
                    }
                    break;

                // ── GPU (NVIDIA / AMD / Intel Arc) ────────────────────────────
                case HardwareType.GpuNvidia:
                case HardwareType.GpuAmd:
                case HardwareType.GpuIntel:
                    if (!gotGpuT)
                    {
                        var t = ExtractGpuTemp(hw);
                        if (t > 0) { gpuTempC = t; gotGpuT = true; }
                    }
                    // GPU Load (Core utilisation) — separate flag
                    if (gpuLoad < 0.1)
                    {
                        foreach (var s in hw.Sensors)
                        {
                            if (s.SensorType != SensorType.Load) continue;
                            if (!s.Value.HasValue || s.Value.Value <= 0) continue;
                            if (s.Name.Contains("Core", StringComparison.OrdinalIgnoreCase) ||
                                s.Name.Contains("GPU",  StringComparison.OrdinalIgnoreCase))
                            {
                                gpuLoad = Math.Round(s.Value.Value, 1);
                                break;
                            }
                        }
                        // Fallback: any Load sensor on the GPU
                        if (gpuLoad < 0.1)
                            foreach (var s in hw.Sensors)
                                if (s.SensorType == SensorType.Load && s.Value.HasValue && s.Value.Value > 0)
                                { gpuLoad = Math.Round(s.Value.Value, 1); break; }
                    }
                    break;
            }

            // Fan sensors — scan every hardware node (and sub-chips)
            if (!gotFan) ScanFan(hw, ref fanRpm, ref gotFan);
            foreach (var sub in hw.SubHardware)
                if (!gotFan) ScanFan(sub, ref fanRpm, ref gotFan);
        }

        CpuTemperatureC  = Math.Round(cpuTempC, 1);
        GpuTemperatureC  = Math.Round(gpuTempC, 1);
        GpuUsagePercent  = gpuLoad;
        FanSpeedRpm      = fanRpm;
    }

    // ── CPU temperature — multi-level fallback ────────────────────────────────
    //
    //  Priority 1 — "Package"          (Intel Package, AMD Tdie package rollup)
    //  Priority 2 — "Tdie" / "Tctl"   (AMD Ryzen direct die temps)
    //  Priority 3 — max of Core #N     (per-core, take hottest for safety)
    //  Priority 4 — any temperature    (last resort)
    //
    private static double ExtractCpuTemp(IHardware cpu)
    {
        double packageTemp = 0;
        double maxCoreTemp = 0;
        double firstTemp   = 0;
        bool   hasPackage  = false;
        bool   hasCore     = false;
        bool   hasAny      = false;

        foreach (var s in cpu.Sensors)
        {
            if (s.SensorType != SensorType.Temperature) continue;
            if (!s.Value.HasValue || s.Value.Value <= 0) continue;

            double v   = s.Value.Value;
            string nm  = s.Name;

            if (!hasAny) { firstTemp = v; hasAny = true; }

            // P1: Package / Tdie / Tctl
            if (nm.Contains("Package", StringComparison.OrdinalIgnoreCase) ||
                nm.Contains("Tdie",    StringComparison.OrdinalIgnoreCase) ||
                nm.Contains("Tctl",    StringComparison.OrdinalIgnoreCase))
            {
                // Prefer "Package" over "Tctl" (Tctl includes +10°С offset on Ryzen)
                if (!hasPackage || nm.Contains("Package", StringComparison.OrdinalIgnoreCase))
                {
                    packageTemp = v;
                    hasPackage  = true;
                }
            }

            // P2: Core temperatures (accumulate max)
            if (nm.StartsWith("Core",     StringComparison.OrdinalIgnoreCase) ||
                nm.StartsWith("CPU Core", StringComparison.OrdinalIgnoreCase))
            {
                if (v > maxCoreTemp) { maxCoreTemp = v; hasCore = true; }
            }
        }

        if (hasPackage) return packageTemp;
        if (hasCore)    return maxCoreTemp;
        if (hasAny)     return firstTemp;
        return 0;
    }

    // ── GPU temperature ────────────────────────────────────────────────────────
    //  Priority 1 — "GPU Core" (most GPUs)
    //  Priority 2 — "Hot Spot"  (NVIDIA Turing+)
    //  Priority 3 — first available temperature sensor
    //
    private static double ExtractGpuTemp(IHardware gpu)
    {
        double coreTemp   = 0;
        double hotSpot    = 0;
        double firstTemp  = 0;
        bool   hasCore    = false;
        bool   hasHotSpot = false;
        bool   hasAny     = false;

        foreach (var s in gpu.Sensors)
        {
            if (s.SensorType != SensorType.Temperature) continue;
            if (!s.Value.HasValue || s.Value.Value <= 0) continue;

            double v  = s.Value.Value;
            string nm = s.Name;

            if (!hasAny) { firstTemp = v; hasAny = true; }

            if (nm.Contains("Core",    StringComparison.OrdinalIgnoreCase) ||
                nm.Contains("GPU",     StringComparison.OrdinalIgnoreCase))
            { coreTemp = v; hasCore = true; }

            if (nm.Contains("Hot Spot", StringComparison.OrdinalIgnoreCase) ||
                nm.Contains("HotSpot",  StringComparison.OrdinalIgnoreCase))
            { hotSpot = v; hasHotSpot = true; }
        }

        if (hasCore)    return coreTemp;
        if (hasHotSpot) return hotSpot;
        if (hasAny)     return firstTemp;
        return 0;
    }

    // ── Fan sensor scan (walks all sensor types, any hardware node) ───────────

    private static void ScanFan(IHardware hw, ref int fanRpm, ref bool gotFan)
    {
        foreach (var s in hw.Sensors)
        {
            if (s.SensorType != SensorType.Fan) continue;
            if (!s.Value.HasValue || s.Value.Value <= 0) continue;
            fanRpm = (int)s.Value.Value;
            gotFan = true;
            return;
        }
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _fastTimer.Stop(); _fastTimer.Dispose();
        _slowTimer.Stop(); _slowTimer.Dispose();

        _cpuCounter?.Dispose();
        try { _lhmComputer?.Close(); } catch { /* LHM Close() can throw on some drivers */ }
    }
}
