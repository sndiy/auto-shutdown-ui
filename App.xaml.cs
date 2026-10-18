using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;

namespace AutoShutdownUI;

public partial class App : Application
{
    public const string MainMutexName = @"Local\AutoShutdown_MainMutex_v3";
    public const string WatchdogMutexName = @"Local\AutoShutdown_GuardMutex_v3";
    public const string CancelEventName = @"Local\AutoShutdown_CancelEvent_v3";
    public const string SharedMemName = @"Local\AutoShutdown_SharedTimer_v3";

    private const int MaxDelayMinutes = 720;

    private Mutex? _mainMutex;
    private DelayTrayHost? _tray;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(IntPtr hDesktop);

    private void Application_Startup(object sender, StartupEventArgs e)
    {
        // Mode tunda tidak punya window, jadi app tidak boleh tutup otomatis
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // 1. MODE WATCHDOG
        if (e.Args.Contains("--watchdog"))
        {
            RunWatchdogLoop();
            Shutdown(0);
            return;
        }

        int delayMinutes = ParseDelay(e.Args);
        bool isDelay = delayMinutes > 0;

        // 2. CEK LOCK SCREEN (mode tunda dicek saat waktunya habis)
        if (!isDelay && IsScreenLocked())
        {
            Shutdown(0);
            return;
        }

        // 3. SINGLE INSTANCE
        //    Mode tunda menunggu sebentar: proses lama mungkin masih melepas mutex.
        if (!AcquireMainMutex(isDelay ? TimeSpan.FromSeconds(4) : TimeSpan.Zero))
        {
            Shutdown(0);
            return;
        }

        // 4. PILIH MODE
        if (isDelay) StartDelayMode(delayMinutes);
        else ShowCountdown();
    }

    private bool AcquireMainMutex(TimeSpan wait)
    {
        _mainMutex = new Mutex(false, MainMutexName);
        bool acquired;
        try
        {
            acquired = _mainMutex.WaitOne(wait);
        }
        catch (AbandonedMutexException)
        {
            acquired = true; // proses sebelumnya mati paksa, mutex sekarang milik kita
        }

        if (!acquired)
        {
            _mainMutex.Dispose();
            _mainMutex = null;
        }
        return acquired;
    }

    private static int ParseDelay(string[] args)
    {
        int i = Array.IndexOf(args, "--delay");
        if (i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out int m) && m > 0)
            return Math.Min(m, MaxDelayMinutes);
        return 0;
    }

    // ================= MODE COUNTDOWN =================
    private void ShowCountdown()
    {
        var mainWindow = new MainWindow(_mainMutex!);
        MainWindow = mainWindow;
        mainWindow.Show();
    }

    // ================= MODE TUNDA (TRAY SAJA) =================
    private void StartDelayMode(int minutes)
    {
        DateTime target = DateTime.Now.AddMinutes(minutes);

        _tray = new DelayTrayHost(
            target,
            onClose: ExitWithoutShutdown,
            onElapsed: () =>
            {
                _tray?.Dispose();
                _tray = null;

                // Sama seperti perilaku lamamu: layar terkunci -> tidak jadi shutdown
                if (IsScreenLocked())
                {
                    ExitWithoutShutdown();
                    return;
                }
                ShowCountdown();
            });
    }

    // Menu tray "Tutup program": tidak ada proses yang menghitung lagi = shutdown batal
    private void ExitWithoutShutdown()
    {
        _tray?.Dispose();
        _tray = null;

        try { _mainMutex?.ReleaseMutex(); } catch { }
        _mainMutex?.Dispose();
        _mainMutex = null;

        Shutdown(0);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose(); // cegah ikon hantu di tray
        base.OnExit(e);
    }

    // ================= WATCHDOG (logika aslimu) =================
    private static void RunWatchdogLoop()
    {
        using var guardMutex = new Mutex(true, WatchdogMutexName, out bool isNewGuard);
        if (!isNewGuard) return;

        using var cancelEvent = new EventWaitHandle(false, EventResetMode.ManualReset, CancelEventName);
        string? exePath = Environment.ProcessPath;

        while (!cancelEvent.WaitOne(120))
        {
            if (IsScreenLocked())
            {
                cancelEvent.Set();
                break;
            }

            if (!IsMutexAlive(MainMutexName) && !string.IsNullOrEmpty(exePath))
            {
                try
                {
                    using var p = Process.Start(new ProcessStartInfo(exePath)
                    {
                        CreateNoWindow = true,
                        UseShellExecute = false
                    });
                }
                catch { }
                Thread.Sleep(250);
            }
        }
    }

    public static bool IsMutexAlive(string name)
    {
        try
        {
            if (Mutex.TryOpenExisting(name, out Mutex? m))
            {
                m.Dispose();
                return true;
            }
            return false;
        }
        catch
        {
            return true;
        }
    }

    public static bool IsScreenLocked()
    {
        try
        {
            if (Process.GetProcessesByName("LogonUI").Length > 0) return true;
            IntPtr hDesktop = OpenInputDesktop(0, false, 0x0001);
            if (hDesktop == IntPtr.Zero) return true;
            CloseDesktop(hDesktop);
        }
        catch { }
        return false;
    }
}