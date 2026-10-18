using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Media;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace AutoShutdownUI;

public partial class MainWindow : Window
{
    private const int TotalDuration = 60;
    private const double CardWidth = 410.0;

    // Tinggi window saat panel tunda tertutup / terbuka
    private bool _customMode;

    // Batas tunda custom (menit)
    private const int MinSnooze = 1;
    private const int MaxSnooze = 720;   // 12 jam
    private const int StepSnooze = 5;
    private const int DefaultSnooze = 15;

    private readonly Mutex _instanceMutex;
    private EventWaitHandle? _cancelEvent;
    private MemoryMappedFile? _sharedMemory;
    private readonly DispatcherTimer _uiTimer = new();
    private CancellationTokenSource? _watchdogCts;
    private IntPtr _keyboardHookId = IntPtr.Zero;
    private LowLevelKeyboardProc? _hookCallback;
    private bool _isAuthorizedExit = false;

    // Pre-allocated Frozen Brushes (Nol alokasi memori saat ganti warna)
    private static readonly SolidColorBrush BrushSafe = CreateFrozenBrush(110, 231, 183);   // Mint Santai (#6EE7B7)
    private static readonly SolidColorBrush BrushWarn = CreateFrozenBrush(251, 191, 36);    // Kuning Hangat (#FBBF24)
    private static readonly SolidColorBrush BrushDanger = CreateFrozenBrush(248, 113, 113); // Merah Coral (#F87171)

    private static SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    // Nada dibuat sekali, dipakai berulang
    private static readonly byte[] ToneWarn = BuildTone(750, 80);
    private static readonly byte[] ToneDanger = BuildTone(1100, 150);

    private static byte[] BuildTone(int freq, int durationMs)
    {
        const int rate = 22050;
        int samples = rate * durationMs / 1000;
        int dataLen = samples * 2;

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);

        bw.Write(new[] { 'R', 'I', 'F', 'F' });
        bw.Write(36 + dataLen);
        bw.Write(new[] { 'W', 'A', 'V', 'E', 'f', 'm', 't', ' ' });
        bw.Write(16);              // ukuran chunk fmt
        bw.Write((short)1);        // PCM
        bw.Write((short)1);        // mono
        bw.Write(rate);
        bw.Write(rate * 2);        // byte rate
        bw.Write((short)2);        // block align
        bw.Write((short)16);       // bit depth
        bw.Write(new[] { 'd', 'a', 't', 'a' });
        bw.Write(dataLen);

        int fade = rate / 200;     // fade 5 ms supaya tidak ada bunyi "klik"
        for (int i = 0; i < samples; i++)
        {
            double env = Math.Min(1.0, Math.Min(i, samples - i) / (double)fade);
            double v = Math.Sin(2 * Math.PI * freq * i / rate) * 0.5 * env;
            bw.Write((short)(v * short.MaxValue));
        }
        bw.Flush();
        return ms.ToArray();
    }

    private static void PlayTone(byte[] wav)
    {
        try
        {
            using var player = new SoundPlayer(new MemoryStream(wav));
            player.PlaySync();
        }
        catch { }
    }

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int VK_F4 = 0x73;
    private const int VK_TAB = 0x09;
    private const int VK_ESCAPE = 0x1B;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    public MainWindow(Mutex instanceMutex)
    {
        _instanceMutex = instanceMutex;
        InitializeComponent();

        // 1. Baca atau inisialisasi sisa detik di Shared RAM
        _sharedMemory = MemoryMappedFile.CreateOrOpen(App.SharedMemName, 4);
        int currentSec = ReadSharedSeconds();
        if (currentSec <= 0 || currentSec > TotalDuration)
        {
            WriteSharedSeconds(TotalDuration);
            currentSec = TotalDuration;
        }

        // 2. Set tampilan awal (Jam target & Visual)
        DateTime targetTime = DateTime.Now.AddSeconds(currentSec);
        TxtTargetTime.Text = $"Jadwal mati otomatis pukul {targetTime:HH:mm:ss}";
        UpdateVisualState(currentSec);

        // 3. Reset sinyal batal lintas proses
        _cancelEvent = new EventWaitHandle(false, EventResetMode.ManualReset, App.CancelEventName);
        _cancelEvent.Reset();

        // 4. Pasang Keyboard Hook
        InstallKeyboardHook();

        Closing += (s, e) =>
        {
            if (!_isAuthorizedExit) e.Cancel = true;
        };

        // 5. Nyalakan Watchdog di background
        _watchdogCts = new CancellationTokenSource();
        Task.Run(() => KeepWatchdogAlive(_watchdogCts.Token));

        // 6. Mulai Timer 1 Detik
        _uiTimer.Interval = TimeSpan.FromSeconds(1);
        _uiTimer.Tick += UiTimer_Tick;
        _uiTimer.Start();
    }

    private void UpdateVisualState(int remainingSeconds)
    {
        TxtSeconds.Text = remainingSeconds.ToString();

        // Pilih warna berdasarkan zona waktu
        SolidColorBrush activeBrush = remainingSeconds > 20 ? BrushSafe
                                    : remainingSeconds > 10 ? BrushWarn
                                    : BrushDanger;

        TxtSeconds.Foreground = activeBrush;
        ProgressBarFill.Background = activeBrush;

        // Susutkan lebar progress bar secara proporsional
        double trackWidth = ProgressTrack.ActualWidth > 0 ? ProgressTrack.ActualWidth : CardWidth - 44;
        double ratio = Math.Clamp((double)remainingSeconds / TotalDuration, 0.0, 1.0);
        ProgressBarFill.Width = trackWidth * ratio;
    }

    private void UiTimer_Tick(object? sender, EventArgs e)
    {
        if (App.IsScreenLocked())
        {
            CleanExit();
            return;
        }

        int rem = ReadSharedSeconds() - 1;
        WriteSharedSeconds(rem);
        UpdateVisualState(rem);

        // Suara peringatan di background thread
        int currentRem = rem;
        if (currentRem <= 5 && currentRem > 0)
            Task.Run(() => PlayTone(ToneDanger));
        else if (currentRem > 5 && currentRem <= 20)
            Task.Run(() => PlayTone(ToneWarn));

        if (rem <= 0)
        {
            _uiTimer.Stop();
            ExecuteSystemShutdown();
            CleanExit();
        }
    }

    // ================= PANEL TUNDA: BUKA / TUTUP =================

    private void BtnToggleCustom_Click(object sender, RoutedEventArgs e)
    {
        _customMode = !_customMode;
        PresetRow.Visibility = _customMode ? Visibility.Collapsed : Visibility.Visible;
        CustomRow.Visibility = _customMode ? Visibility.Visible : Visibility.Collapsed;
        BtnToggleCustom.Content = _customMode ? "‹ Kembali" : "Atur sendiri";

        if (_customMode)
        {
            // Fokus ke input supaya langsung bisa ketik angka lalu Enter
            Dispatcher.BeginInvoke(new Action(() => TxtCustomMinutes.Focus()), DispatcherPriority.Input);
        }
    }

    // ================= TUNDA: PRESET =================

    // Handler tombol preset (+5 mnt, +15 mnt, +30 mnt, +1 jam) -> baca dari Tag
    private void BtnSnoozeTemplate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && int.TryParse(btn.Tag?.ToString(), out int minutes))
        {
            TriggerSnoozeAndExit(minutes);
        }
    }

    // ================= TUNDA: CUSTOM =================

    // Baca nilai input, selalu mengembalikan angka valid (1..720)
    private int GetCustomMinutes()
    {
        int v = int.TryParse(TxtCustomMinutes.Text, out int n) ? n : DefaultSnooze;
        return Math.Clamp(v, MinSnooze, MaxSnooze);
    }

    private void SetCustomMinutes(int v)
    {
        v = Math.Clamp(v, MinSnooze, MaxSnooze);
        TxtCustomMinutes.Text = v.ToString();
        TxtCustomMinutes.SelectAll(); // ketik angka baru langsung menimpa
    }

    private void BtnStepMinus_Click(object sender, RoutedEventArgs e)
        => SetCustomMinutes(GetCustomMinutes() - StepSnooze);

    private void BtnStepPlus_Click(object sender, RoutedEventArgs e)
        => SetCustomMinutes(GetCustomMinutes() + StepSnooze);

    // Hanya terima digit 0-9
    private void TxtCustomMinutes_PreviewTextInput(object sender, TextCompositionEventArgs e)
        => e.Handled = !e.Text.All(c => c >= '0' && c <= '9');

    // Blok paste yang bukan angka
    private void TxtCustomMinutes_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (!e.DataObject.GetDataPresent(typeof(string)) ||
            e.DataObject.GetData(typeof(string)) is not string s ||
            !s.All(c => c >= '0' && c <= '9'))
        {
            e.CancelCommand();
        }
    }

    // Klik pertama langsung fokus + select all (tidak perlu hapus manual)
    private void TxtCustomMinutes_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!TxtCustomMinutes.IsKeyboardFocusWithin)
        {
            e.Handled = true;
            TxtCustomMinutes.Focus();
        }
    }

    private void TxtCustomMinutes_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        => TxtCustomMinutes.SelectAll();

    // Keluar fokus: rapikan nilai (kosong/0/999 -> valid)
    private void TxtCustomMinutes_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        int v = GetCustomMinutes();
        if (TxtCustomMinutes.Text != v.ToString())
            TxtCustomMinutes.Text = v.ToString();
    }

    // Enter = tunda, panah atas/bawah = +/- 5 menit
    private void TxtCustomMinutes_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                BtnCustomSnooze_Click(sender, e);
                e.Handled = true;
                break;
            case Key.Up:
                SetCustomMinutes(GetCustomMinutes() + StepSnooze);
                e.Handled = true;
                break;
            case Key.Down:
                SetCustomMinutes(GetCustomMinutes() - StepSnooze);
                e.Handled = true;
                break;
        }
    }

    // Scroll mouse di atas input (XAML: PreviewMouseWheel)
    private void TxtCustomMinutes_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        SetCustomMinutes(GetCustomMinutes() + (e.Delta > 0 ? StepSnooze : -StepSnooze));
        e.Handled = true;
    }

    // Label tombol selalu menampilkan hasil akhir: "Tunda 45 mnt"
    private void TxtCustomMinutes_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (BtnCustomSnooze == null) return; // event ini terpanggil saat InitializeComponent
        BtnCustomSnooze.Content = $"Tunda {GetCustomMinutes()} mnt";
    }

    private void BtnCustomSnooze_Click(object sender, RoutedEventArgs e)
    {
        TriggerSnoozeAndExit(GetCustomMinutes());
    }

    // ================= LOGIKA TUNDA & KELUAR =================

    private void TriggerSnoozeAndExit(int minutes)
    {
        string? exePath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exePath))
        {
            try
            {
                // Jalankan proses baru dalam mode tunda (--delay) sebelum menutup UI ini
                using var p = Process.Start(new ProcessStartInfo(exePath, $"--delay {minutes}")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
            }
            catch { }
        }
        CleanExit();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        CleanExit();
    }

    private static void KeepWatchdogAlive(CancellationToken token)
    {
        string? exePath = Environment.ProcessPath;
        using var cancelEvent = new EventWaitHandle(false, EventResetMode.ManualReset, App.CancelEventName);

        while (!token.IsCancellationRequested && !cancelEvent.WaitOne(120))
        {
            if (!App.IsMutexAlive(App.WatchdogMutexName) && !string.IsNullOrEmpty(exePath))
            {
                try
                {
                    using var p = Process.Start(new ProcessStartInfo(exePath, "--watchdog")
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

    private int ReadSharedSeconds()
    {
        try
        {
            if (_sharedMemory == null) return TotalDuration;
            using var accessor = _sharedMemory.CreateViewAccessor(0, 4);
            return accessor.ReadInt32(0);
        }
        catch { return TotalDuration; }
    }

    private void WriteSharedSeconds(int val)
    {
        try
        {
            if (_sharedMemory == null) return;
            using var accessor = _sharedMemory.CreateViewAccessor(0, 4);
            accessor.Write(0, val);
        }
        catch { }
    }

    private void InstallKeyboardHook()
    {
        try
        {
            _hookCallback = HookCallback;
            _keyboardHookId = SetWindowsHookEx(WH_KEYBOARD_LL, _hookCallback, GetModuleHandle(null), 0);
        }
        catch { }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
        {
            int vkCode = Marshal.ReadInt32(lParam);
            if (vkCode == VK_F4 || vkCode == VK_TAB || vkCode == VK_ESCAPE || vkCode == VK_LWIN || vkCode == VK_RWIN)
            {
                return (IntPtr)1;
            }
        }
        return CallNextHookEx(_keyboardHookId, nCode, wParam, lParam);
    }

    private void CleanExit()
    {
        if (_isAuthorizedExit) return;
        _isAuthorizedExit = true;

        _uiTimer.Stop();

        try { _cancelEvent?.Set(); } catch { }
        try { _watchdogCts?.Cancel(); } catch { }

        if (_keyboardHookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_keyboardHookId);
            _keyboardHookId = IntPtr.Zero;
        }

        WriteSharedSeconds(0);
        try { _sharedMemory?.Dispose(); } catch { }
        try { _cancelEvent?.Dispose(); } catch { }
        try { _watchdogCts?.Dispose(); } catch { }

        try
        {
            _instanceMutex.ReleaseMutex();
            _instanceMutex.Dispose();
        }
        catch { }

        Close();
        Application.Current.Shutdown(0);
    }

    private static void ExecuteSystemShutdown()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("shutdown.exe", "/s /f /t 0")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            });
        }
        catch { }
    }
}