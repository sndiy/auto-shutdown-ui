using System.Reflection;
using System.Windows.Threading;

namespace AutoShutdownUI;

internal sealed class DelayTrayHost : IDisposable
{
    private readonly System.Windows.Forms.NotifyIcon _icon;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DateTime _target;
    private readonly Action _onElapsed;
    private int _lastMinutes = -1;
    private bool _disposed;

    public DelayTrayHost(DateTime target, Action onClose, Action onElapsed)
    {
        _target = target;
        _onElapsed = onElapsed;

        // Menu hanya 1 opsi
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Batalkan shutdown", null, (_, _) => onClose());

        _icon = new System.Windows.Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            ContextMenuStrip = menu,
            Visible = true
        };

        // Klik kiri juga membuka menu (default Windows hanya klik kanan)
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button != System.Windows.Forms.MouseButtons.Left) return;
            try
            {
                typeof(System.Windows.Forms.NotifyIcon)
                    .GetMethod("ShowContextMenu", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.Invoke(_icon, null);
            }
            catch { }
        };

        UpdateText();
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    private void Tick()
    {
        if (_disposed) return;

        if (DateTime.Now >= _target)
        {
            _timer.Stop();
            _onElapsed();
            return;
        }
        UpdateText();
    }

    private void UpdateText()
    {
        int mins = (int)Math.Ceiling((_target - DateTime.Now).TotalMinutes);
        if (mins == _lastMinutes) return;
        _lastMinutes = mins;

        // NotifyIcon.Text maksimal 63 karakter
        _icon.Text = $"Shutdown {_target:HH:mm} ({mins} mnt lagi)";
    }

    private static System.Drawing.Icon LoadIcon()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                var icon = System.Drawing.Icon.ExtractAssociatedIcon(exe);
                if (icon != null) return icon;
            }
        }
        catch { }
        return System.Drawing.SystemIcons.Application;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _timer.Stop();
        _icon.Visible = false;       // hapus ikon dari tray sebelum dispose
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
    }
}