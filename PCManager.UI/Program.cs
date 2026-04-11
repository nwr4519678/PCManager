using Avalonia;
using System;
using System.IO;
using System.Threading.Tasks;

using System.Security.Principal;
using System.Diagnostics;

namespace PCManager.UI;

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            LogCrash("AppDomain.UnhandledException", e.ExceptionObject as Exception);
        };

        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            LogCrash("TaskScheduler.UnobservedTaskException", e.Exception);
            e.SetObserved();
        };

        try
        {
            if (!IsAdministrator())
            {
                var exeName = Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exeName))
                {
                    try
                    {
                        var proc = new Process
                        {
                            StartInfo = new ProcessStartInfo
                            {
                                FileName = exeName,
                                UseShellExecute = true,
                                Verb = "runas"
                            }
                        };
                        proc.Start();
                    }
                    catch
                    {
                        // User cancelled the UAC prompt or it failed.
                    }
                }
                return; // Exit current process
            }

            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            LogCrash("Main/Fatal", ex);
        }
    }

    private static bool IsAdministrator()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var identity = WindowsIdentity.GetCurrent();
                var principal = new WindowsPrincipal(identity);
                return principal.IsInRole(WindowsBuiltInRole.Administrator);
            }
            return false;
        }
        catch { return false; }
    }

    private static void LogCrash(string source, Exception? ex)
    {
        try
        {
            var logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash_log.txt");
            var msg = $"[{DateTime.Now:O}] [{source}] {ex?.GetType().Name}: {ex?.Message}\n{ex?.StackTrace}\n\n";
            File.AppendAllText(logPath, msg);
        }
        catch { /* Fallback fails silently */ }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .With(new Win32PlatformOptions 
            { 
                RenderingMode = new[] { Win32RenderingMode.AngleEgl, Win32RenderingMode.Software } 
            })
            .LogToTrace();
}
