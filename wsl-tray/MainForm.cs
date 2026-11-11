using System.Runtime.InteropServices;

namespace WslTray;

public sealed class MainForm : Form
{
    // Fixed window: every view is laid out to fit exactly, so there is nothing to resize.
    private const int ClientW = 1180;
    private const int HeaderH = 46;
    private const int ClientH = 700 + HeaderH;
    private const int SidebarW = 178;
    private const int Pad = 22;
    private const int ContentW = ClientW - SidebarW - Pad * 2;

    // The Overview spends the width on two columns; a form does not get better from
    // being stretched, so the other views keep a readable measure.
    private const int FormW = 760;
    private const int ColW = (ContentW - 18) / 2;
    private const int ColRight = ColW + 18;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;

    private readonly Monitor _monitor;
    private readonly Config _cfg;

    private readonly Panel _content = new() { Dock = DockStyle.Fill, BackColor = Theme.Bg, Padding = new Padding(Pad) };
    private readonly List<NavItem> _navButtons = new();

    private readonly MetricBar _cpu = new();
    private readonly MetricBar _ram = new();
    private readonly MetricBar _swap = new();
    private readonly MetricBar _disk = new();
    private readonly Label _summary = UiHelp.Lbl("", Theme.Small, Theme.Muted);
    private readonly Label _stamp = UiHelp.Lbl("", Theme.Small, Theme.Muted);

    private readonly ListView _distroList = UiHelp.List(("DISTRO", 132), ("STATUS", 58), ("CPU", 64), ("RAM (PROC)", 100), ("VHDX", 84), ("", 0));
    private readonly ListView _containerList = UiHelp.List(("CONTAINER", 210), ("CPU", 76), ("RAM", 100), ("", 0));

    private readonly FlatBtn _start = new("Start") { Width = 92 };
    private readonly FlatBtn _stop = new("Stop") { Width = 92 };
    private readonly FlatBtn _restart = new("Restart") { Width = 92 };
    private readonly FlatBtn _shell = new("Shell") { Width = 92 };
    private readonly Label _distroStatus = UiHelp.Lbl("", Theme.Body, Theme.Muted);

    private Panel? _overview;
    private Panel? _distros;
    /// Keeps the selected distro across the in-place list refresh.
    private string _selected = "";

    private const string SelectHint = "select a distro to start, stop, restart or open a terminal on it";

    public MainForm(Monitor monitor, Config cfg)
    {
        _monitor = monitor;
        _cfg = cfg;

        Text = "WSL Tray";
        ClientSize = new Size(ClientW, ClientH);
        // No system frame at all: the header bar below is part of the window content,
        // so the title, the buttons and the body are one surface.
        FormBorderStyle = FormBorderStyle.None;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Bg;
        ForeColor = Theme.Text;
        Font = Theme.Body;

        BuildLayout();
        ShowPanel(BuildOverview(), 0);

        _monitor.Updated += OnUpdated;
        Ops.Changed += OnOpsChanged;
        // Hidden is the normal state for a tray app, and a hidden window still got a full
        // render on every poll. Repaint once when it comes back instead.
        VisibleChanged += (s, e) => { if (Visible) Render(_monitor.Latest); };
        FormClosing += (s, e) =>
        {
            // The tray owns the lifetime; closing the window just hides it.
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
        };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Windows 11 still rounds the corners of a borderless window if asked directly;
        // without this a frameless form has hard 90 degree edges.
        var pref = DwmwcpRound;
        try { DwmSetWindowAttribute(Handle, DwmwaWindowCornerPreference, ref pref, sizeof(int)); }
        catch (DllNotFoundException) { /* pre-Windows 11: square corners, nothing breaks */ }
        catch (EntryPointNotFoundException) { }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _monitor.Updated -= OnUpdated;
        Ops.Changed -= OnOpsChanged;

        // The cached views are kept out of Controls, so the form does not dispose them for
        // us and DistrosPanel would stay subscribed to the static Ops event after the window
        // is gone.
        _overview?.Dispose();
        _distros?.Dispose();
        _overview = _distros = null;

        base.OnFormClosed(e);
    }

    private void OnOpsChanged()
    {
        if (!IsHandleCreated || IsDisposed) return;
        try { BeginInvoke(UpdateDistroButtons); }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    private void BuildLayout()
    {
        var sidebar = new Panel { Dock = DockStyle.Left, Width = SidebarW, BackColor = Theme.Sidebar };
        sidebar.Paint += (s, e) =>
        {
            using var pen = new Pen(Theme.Border);
            e.Graphics.DrawLine(pen, sidebar.Width - 1, 0, sidebar.Width - 1, sidebar.Height);
        };

        // No app name here: the header bar already carries it.
        var items = new (string label, Func<Control> build)[]
        {
            ("Overview", BuildOverview),
            // Kept alive like the Overview: rebuilding it on every tab switch re-ran the
            // online catalog fetch, and threw away the status line and elapsed counter of
            // whatever install or clone was still running.
            ("Distros", () => _distros ??= new DistrosPanel(FormW, _cfg, () => _monitor.Invalidate())),
            ("Limits", () => new LimitsPanel(FormW, () => _monitor.Latest, () => _monitor.Invalidate())),
            ("Settings", () => new SettingsPanel(FormW, _cfg)),
        };

        int y = 18;
        for (int i = 0; i < items.Length; i++)
        {
            var idx = i;
            var b = new NavItem(items[i].label) { Location = new Point(12, y), Width = SidebarW - 25 };
            b.Click += (s, e) => ShowPanel(items[idx].build(), idx);
            sidebar.Controls.Add(b);
            _navButtons.Add(b);
            y += 42;
        }

        var version = UiHelp.Lbl("no per-distro limits\nexist in WSL2", Theme.Small, Theme.Muted);
        version.Location = new Point(20, ClientH - HeaderH - 58);
        sidebar.Controls.Add(version);

        Controls.Add(_content);
        Controls.Add(sidebar);
        Controls.Add(new TitleBar("WSL Tray"));
    }

    private void ShowPanel(Control panel, int navIndex)
    {
        for (int i = 0; i < _navButtons.Count; i++)
        {
            _navButtons[i].Active = i == navIndex;
            _navButtons[i].Invalidate();
        }
        // Focus follows the active entry, otherwise a focus ring is left behind on the
        // previous one and reads as a second active tab.
        if (navIndex >= 0 && navIndex < _navButtons.Count) _navButtons[navIndex].Focus();

        _content.SuspendLayout();
        foreach (Control c in _content.Controls.Cast<Control>().ToList())
        {
            _content.Controls.Remove(c);
            if (!ReferenceEquals(c, _overview) && !ReferenceEquals(c, _distros)) c.Dispose();
        }
        panel.Dock = DockStyle.Fill;
        _content.Controls.Add(panel);
        _content.ResumeLayout();
    }

    private Panel BuildOverview()
    {
        if (_overview != null) return _overview;

        var root = new Panel { BackColor = Theme.Bg };

        var head = UiHelp.Lbl("Virtual machine", Theme.Title);
        head.Location = new Point(0, 0);
        root.Controls.Add(head);

        _stamp.Location = new Point(ContentW - 60, 5);
        root.Controls.Add(_stamp);

        var card = UiHelp.Card();
        card.Location = new Point(0, 30);
        card.Size = new Size(ContentW, 250);

        int by = 18;
        foreach (var bar in new[] { _cpu, _ram, _swap, _disk })
        {
            bar.Location = new Point(18, by);
            bar.Width = ContentW - 36;
            bar.BackColor = Theme.Surface;
            card.Controls.Add(bar);
            by += 48;
        }

        _summary.Location = new Point(18, by + 6);
        _summary.MaximumSize = new Size(ContentW - 36, 0);
        card.Controls.Add(_summary);
        root.Controls.Add(card);

        var distroHead = UiHelp.Lbl("Distros", Theme.Title);
        distroHead.Location = new Point(0, 298);
        root.Controls.Add(distroHead);

        var contHead = UiHelp.Lbl("Containers", Theme.Title);
        contHead.Location = new Point(ColRight, 298);
        root.Controls.Add(contHead);

        _containerList.Location = new Point(ColRight, 326);
        _containerList.Size = new Size(ColW, 220);
        root.Controls.Add(_containerList);

        _distroList.Location = new Point(0, 326);
        _distroList.Size = new Size(ColW, 220);
        _distroList.SelectedIndexChanged += (s, e) =>
        {
            if (_distroList.SelectedItems.Count > 0) _selected = _distroList.SelectedItems[0].Text;
            UpdateDistroButtons();
        };
        root.Controls.Add(_distroList);

        _start.Location = new Point(0, 560);
        _start.Click += (s, e) => RunDistroAction("Start", d => Wsl.StartDistro(d));
        root.Controls.Add(_start);

        _stop.Location = new Point(100, 560);
        _stop.Click += (s, e) => RunDistroAction("Stop", d => Wsl.Terminate(d));
        root.Controls.Add(_stop);

        _restart.Location = new Point(200, 560);
        _restart.Click += (s, e) => RunDistroAction("Restart", d =>
        {
            var err = Wsl.Terminate(d);
            return err.Length > 0 ? err : Wsl.StartDistro(d);
        });
        root.Controls.Add(_restart);

        _shell.Location = new Point(300, 560);
        _shell.Click += (s, e) => OpenShell();
        root.Controls.Add(_shell);

        _distroStatus.Location = new Point(404, 568);
        _distroStatus.MaximumSize = new Size(ContentW - 404, 0);
        root.Controls.Add(_distroStatus);

        UiHelp.FitLastColumn(_distroList);
        UiHelp.FitLastColumn(_containerList);
        UpdateDistroButtons();

        _overview = root;
        Render(_monitor.Latest);
        return root;
    }

    /// Runs a distro action off the UI thread: terminate can take seconds and start
    /// can take much longer on a cold VM.
    private void RunDistroAction(string label, Func<string, string> action)
    {
        if (_selected.Length == 0) return;
        var distro = _selected;

        // Process-wide, not per-panel: a clone running on the Distros tab has already
        // stopped this distro, and starting it now would corrupt that clone.
        if (!Ops.TryEnter())
        {
            _distroStatus.ForeColor = Theme.Warn;
            _distroStatus.Text = "another distro operation is still running.";
            return;
        }

        UpdateDistroButtons();
        _distroStatus.ForeColor = Theme.Muted;
        _distroStatus.Text = label.ToLowerInvariant() + "ing " + distro + "...";
        Cursor = Cursors.WaitCursor;

        Task.Run(() => action(distro)).ContinueWith(t =>
        {
            // Released before the disposed check: the window may be gone, the gate must not
            // stay shut for the rest of the session.
            Ops.Leave();

            if (IsDisposed) return;
            BeginInvoke(() =>
            {
                Cursor = Cursors.Default;

                var err = t.IsFaulted ? t.Exception?.GetBaseException().Message ?? "failed" : t.Result;
                if (err.Length == 0)
                {
                    _distroStatus.ForeColor = Theme.Ok;
                    _distroStatus.Text = distro + ": " + label.ToLowerInvariant() + " done." +
                        (label == "Stop" && KeepAliveScriptPresent()
                            // A keep-alive loop restarts the distro within seconds, which makes
                            // Stop look broken unless we say so.
                            ? "  A keep-alive script is installed and will start it again shortly."
                            : "");
                }
                else
                {
                    _distroStatus.ForeColor = Theme.Crit;
                    _distroStatus.Text = distro + ": " + err.Split('\n')[0];
                }

                // Rate baselines and the container list are stale after this.
                _monitor.Invalidate(distro);
                UpdateDistroButtons();
            });
        });
    }

    /// Opens a terminal on the selected distro. Returns at once, so it needs none of the
    /// busy machinery - but on a stopped distro the shell boots it, which the poll must see.
    private void OpenShell()
    {
        if (_selected.Length == 0 || Ops.Busy) return;

        var err = Wsl.OpenShell(_selected);
        _distroStatus.ForeColor = err.Length == 0 ? Theme.Ok : Theme.Crit;
        _distroStatus.Text = err.Length == 0
            ? _selected + ": terminal opened."
            : "shell failed: " + err.Split('\n')[0];

        _monitor.Invalidate(_selected);
    }

    /// A user-installed keep-alive (a script holding a process open so WSL does not
    /// idle-terminate the distro) silently undoes Stop. Detect the common location.
    private static bool KeepAliveScriptPresent()
    {
        try
        {
            var startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
            return Directory.Exists(startup) &&
                   Directory.EnumerateFiles(startup)
                            .Any(f => Path.GetFileName(f).Contains("keepalive", StringComparison.OrdinalIgnoreCase)
                                   || Path.GetFileName(f).Contains("keep-alive", StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    private void UpdateDistroButtons()
    {
        var d = _monitor.Latest.Distros.FirstOrDefault(x =>
            string.Equals(x.Name, _selected, StringComparison.OrdinalIgnoreCase));

        var has = d != null && !Ops.Busy;
        _start.Enabled = has && !d!.Running;
        _stop.Enabled = has && d!.Running;
        _restart.Enabled = has && d!.Running;
        // A shell on a stopped distro is fine: opening it is how you start one.
        _shell.Enabled = has;

        if (Ops.Busy) return;

        if (_selected.Length == 0)
        {
            _distroStatus.ForeColor = Theme.Muted;
            _distroStatus.Text = SelectHint;
        }
        else if (_distroStatus.Text == SelectHint)
        {
            // Selecting a row answers the hint; clear it instead of leaving it standing.
            _distroStatus.Text = "";
        }
    }

    private void OnUpdated(Snapshot snap)
    {
        if (!IsHandleCreated || IsDisposed) return;
        try { BeginInvoke(() => Render(snap)); } catch (ObjectDisposedException) { }
    }

    private void Render(Snapshot s)
    {
        // Closing the window only hides it, so without this a tray app that spends its life
        // hidden still rebuilt both lists and four bars on every single poll.
        if (IsDisposed || !Visible) return;

        if (!s.VmRunning)
        {
            _cpu.Set("CPU", 0, "VM stopped");
            _ram.Set("Memory", 0, "-");
            _swap.Set("Swap", 0, "-");
            _disk.Set("Disk (in VM)", 0, "-");
            _summary.Text = "No distro is running, so the VM is shut down and holds no memory.";
        }
        else
        {
            _cpu.Set("CPU", s.VmCpuPercent, $"{s.VmCpuPercent:0.0} %   of {s.Cpus} cores");
            _ram.Set("Memory", s.MemPercent, $"{Fmt.Kb(s.MemUsedKb)} / {Fmt.Kb(s.MemTotalKb)}");
            _swap.Set("Swap", s.SwapPercent,
                s.SwapTotalKb == 0 ? "disabled" : $"{Fmt.Kb(s.SwapUsedKb)} / {Fmt.Kb(s.SwapTotalKb)}");
            _disk.Set("Disk (in VM)", s.DiskPercent, $"{Fmt.Kb(s.DiskUsedKb)} / {Fmt.Kb(s.DiskTotalKb)}");
            _summary.Text =
                $"Windows sees {Fmt.Bytes(s.HostVmmemBytes)} held by vmmemWSL. " +
                "WSL releases memory back to Windows lazily, so this stays above the used figure.";
        }

        _stamp.Text = s.Taken.ToString("HH:mm:ss");

        SyncList(_distroList, s.Distros.Select(d => (d.Name, new[]
        {
            d.Running ? "running" : "stopped",
            d.Running ? $"{d.CpuPercent:0.0} %" : "-",
            d.Running ? Fmt.Kb(d.RssKb) : "-",
            d.VhdxBytes > 0 ? Fmt.Bytes(d.VhdxBytes) : "-",
        })).ToList(), _selected);

        if (s.Containers.Count == 0)
            SetPlaceholder(_containerList, s.VmRunning ? "no containers (or docker not running)" : "VM is stopped");
        else
        {
            // Container names are only unique within one distro, so qualify them as soon
            // as containers arrive from more than one.
            var multiDistro = s.Containers
                .Select(c => c.Distro)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() > 1;

            SyncList(_containerList, s.Containers.Select(c => (
                multiDistro ? c.Distro + " / " + c.Name : c.Name,
                new[]
                {
                    $"{c.CpuPercent:0.0} %",
                    Fmt.Bytes(c.MemBytes),
                })).ToList(), null);
        }

        if (!Ops.Busy) UpdateDistroButtons();
    }

    /// Updates rows in place. Clear+Add on every poll makes the list flicker and
    /// throws away the user's selection.
    private static void SyncList(ListView lv, List<(string key, string[] cells)> rows, string? keepSelected)
    {
        lv.BeginUpdate();
        try
        {
            if (lv.Items.Count == 1 && lv.Items[0].SubItems.Count == 1) lv.Items.Clear(); // placeholder
            while (lv.Items.Count > rows.Count) lv.Items.RemoveAt(lv.Items.Count - 1);

            for (int i = 0; i < rows.Count; i++)
            {
                var (key, cells) = rows[i];
                if (i < lv.Items.Count)
                {
                    var it = lv.Items[i];
                    if (it.Text != key) it.Text = key;
                    for (int c = 0; c < cells.Length; c++)
                    {
                        while (it.SubItems.Count <= c + 1) it.SubItems.Add("");
                        if (it.SubItems[c + 1].Text != cells[c]) it.SubItems[c + 1].Text = cells[c];
                    }
                }
                else
                {
                    var it = new ListViewItem(key);
                    foreach (var c in cells) it.SubItems.Add(c);
                    lv.Items.Add(it);
                }
            }

            if (keepSelected != null && lv.SelectedItems.Count == 0)
            {
                foreach (ListViewItem it in lv.Items)
                    if (string.Equals(it.Text, keepSelected, StringComparison.OrdinalIgnoreCase))
                    {
                        it.Selected = true;
                        break;
                    }
            }
        }
        finally { lv.EndUpdate(); }
    }

    private static void SetPlaceholder(ListView lv, string text)
    {
        if (lv.Items.Count == 1 && lv.Items[0].Text == text) return;
        lv.Items.Clear();
        lv.Items.Add(new ListViewItem(text));
    }
}
