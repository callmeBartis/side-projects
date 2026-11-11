using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace WslTray;

public sealed class TrayApp : IDisposable
{
    public static TrayApp? Instance { get; private set; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    private readonly Config _cfg;
    private readonly NotifyIcon _icon;
    private Monitor _monitor;
    private MainForm? _window;
    private Icon? _currentIcon;
    private double _lastDrawnCpu = -2;

    /// NotifyIcon, its text and its GDI icons belong to the thread that created them, but
    /// the monitor raises its events from the polling thread. This invisible control is the
    /// way back onto the UI thread; without it every poll touched the shell from the wrong one.
    private readonly Control _ui = new();

    public TrayApp(Config cfg)
    {
        Instance = this;
        _ = _ui.Handle; // forces the handle now, while we are still on the UI thread
        _cfg = cfg;
        _monitor = new Monitor(cfg);

        _icon = new NotifyIcon
        {
            Text = "WSL Tray",
            Visible = true,
            Icon = SystemIcons.Application,
            ContextMenuStrip = BuildMenu(),
        };
        _icon.DoubleClick += (s, e) => OpenWindow();

        Hook(_monitor);
        _monitor.Start();
    }

    private void Hook(Monitor m)
    {
        m.Updated += OnUpdated;
        m.Failed += OnFailed;
    }

    /// True while the call is on the polling thread, in which case it is queued for the UI
    /// thread instead. Returns false once it is safe to touch the tray icon.
    private bool Marshal(Action again)
    {
        // A disposed control reports InvokeRequired == false, which would read as "go ahead,
        // you are on the UI thread" during shutdown and let a poll touch a disposed tray icon
        // and GDI handles from the polling thread.
        if (_ui.IsDisposed) return true;
        if (!_ui.InvokeRequired) return false;
        try { _ui.BeginInvoke(again); }
        catch (ObjectDisposedException) { /* shutting down */ }
        catch (InvalidOperationException) { /* handle already gone */ }
        return true;
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip { Font = Theme.Body };

        menu.Items.Add(new ToolStripMenuItem("Open", null, (s, e) => OpenWindow()) { Font = Theme.BodyBold });
        menu.Items.Add(new ToolStripSeparator());

        var autostart = new ToolStripMenuItem("Start with Windows") { CheckOnClick = true, Checked = SettingsPanel.IsAutostartOn() };
        autostart.CheckedChanged += (s, e) =>
        {
            try { SettingsPanel.SetAutostart(autostart.Checked); }
            catch (Exception ex) { MessageBox.Show("Autostart failed: " + ex.Message, "WSL Tray"); }
        };
        menu.Items.Add(autostart);

        // The Settings checkbox writes the same registry value, so a menu built once at
        // startup drifts out of sync with it and then reports the opposite of the truth.
        menu.Opening += (s, e) =>
        {
            var on = SettingsPanel.IsAutostartOn();
            if (autostart.Checked != on) autostart.Checked = on;
        };

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Shut down WSL VM", null, (s, e) => ShutdownWsl()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (s, e) => Exit()));
        return menu;
    }

    private void ShutdownWsl()
    {
        var running = _monitor.Latest.Containers.Count;
        var msg = "Stop the WSL VM now?\n\nEvery distro stops immediately" +
                  (running > 0 ? $", including {running} running container(s)." : ".");
        if (MessageBox.Show(msg, "Shut down WSL", MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.OK) return;

        if (!Ops.TryEnter())
        {
            MessageBox.Show("Another distro operation is still running.", "WSL Tray");
            return;
        }

        Task.Run(() =>
        {
            string err;
            try { err = Wsl.ShutdownVm(); }
            finally { Ops.Leave(); }

            _monitor.Invalidate();
            if (err.Length == 0) return;

            // Discarding this left the user believing the VM was down while every distro
            // kept running.
            void Report() => MessageBox.Show("Shutting down the WSL VM failed:\n\n" + err,
                "WSL Tray", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            if (!Marshal(Report)) Report();
        });
    }

    /// Picks up changed poll intervals. The monitor re-reads them from the shared Config on
    /// every tick, so restarting its loop is all it takes. Creating a new Monitor instead
    /// would orphan the open window's subscription and force the window to be closed and
    /// rebuilt under the user, right as they click Save.
    public void RestartMonitor() => _monitor.Start();

    public void ShowWindow() => OpenWindow();

    private void OpenWindow()
    {
        if (_window is { IsDisposed: false })
        {
            _window.Show();
            if (_window.WindowState == FormWindowState.Minimized) _window.WindowState = FormWindowState.Normal;
            _window.Activate();
            return;
        }
        _window = new MainForm(_monitor, _cfg);
        _window.Show();
    }

    private void OnFailed(string msg)
    {
        if (Marshal(() => OnFailed(msg))) return;
        try { _icon.Text = Trim("WSL Tray - error: " + msg); }
        catch (ObjectDisposedException) { }
    }

    private void OnUpdated(Snapshot s)
    {
        if (Marshal(() => OnUpdated(s))) return;
        try
        {
            _icon.Text = Trim(s.VmRunning
                ? $"WSL  CPU {s.VmCpuPercent:0} %  RAM {s.MemPercent:0} %  ({Fmt.Kb(s.MemUsedKb)})"
                : "WSL  VM stopped");

            // Redraw only on a visible change: each icon swap costs a GDI handle.
            var bucket = Math.Round(s.VmRunning ? s.VmCpuPercent : -1);
            if (Math.Abs(bucket - _lastDrawnCpu) < 1) return;
            _lastDrawnCpu = bucket;
            SetIcon(s.VmRunning ? s.VmCpuPercent : -1);
        }
        catch (ObjectDisposedException) { }
    }

    /// The tray icon doubles as the gauge: a filled bar whose height and color follow CPU.
    private void SetIcon(double cpuPercent)
    {
        const int size = 32;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            var track = Color.FromArgb(90, Theme.Dark ? Color.White : Color.Black);
            using (var pen = new Pen(track, 2.5f))
                g.DrawRectangle(pen, 5, 3, size - 11, size - 7);

            if (cpuPercent < 0)
            {
                using var dim = new Pen(track, 2.5f);
                g.DrawLine(dim, 9, 16, size - 9, 16); // VM stopped: a flat line
                SwapIcon(bmp);
                return;
            }

            var inner = new Rectangle(8, 6, size - 17, size - 13);
            var h = (int)Math.Round(inner.Height * Math.Clamp(cpuPercent, 0, 100) / 100.0);
            if (h > 0)
            {
                using var b = new SolidBrush(Theme.ForLoad(cpuPercent));
                g.FillRectangle(b, inner.X, inner.Bottom - h, inner.Width, h);
            }
        }
        SwapIcon(bmp);
    }

    private void SwapIcon(Bitmap bmp)
    {
        var handle = bmp.GetHicon();
        try
        {
            // Clone, because Icon.FromHandle does not own its handle: NotifyIcon keeps the
            // Icon and re-reads its handle later (after an Explorer restart, for one), and
            // destroying it here would leave the tray drawing from freed GDI memory. The
            // clone owns a handle of its own, so the raw one can go immediately - without
            // that the tool leaks a GDI handle per redraw and Windows kills it near 10k.
            using var borrowed = Icon.FromHandle(handle);
            var owned = (Icon)borrowed.Clone();

            _icon.Icon = owned;
            _currentIcon?.Dispose();
            _currentIcon = owned;
        }
        finally { DestroyIcon(handle); }
    }

    /// NotifyIcon.Text throws above 63 characters on older shells.
    private static string Trim(string s) => s.Length <= 63 ? s : s.Substring(0, 60) + "...";

    private void Exit()
    {
        Dispose();
        Application.Exit();
    }

    public void Dispose()
    {
        _monitor.Stop();
        _monitor.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        _currentIcon?.Dispose();
        if (_window is { IsDisposed: false }) { _window.Dispose(); _window = null; }
        _ui.Dispose();
        Instance = null;
    }
}
