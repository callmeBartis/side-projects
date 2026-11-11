using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ClaudeUsageWidget;

internal static class Program
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--selftest"))
        {
            AttachConsole(-1);
            return SelfTest();
        }

        ApplicationConfiguration.Initialize();

        // Before the single-instance check: a newer download must replace the running copy.
        if (!Install.RunningInstalled && !Install.IsDevBuild)
            return Install.Run() ? 0 : 1;

        using var mutex = new Mutex(true, @"Local\ClaudeUsageWidget.SingleInstance", out var isFirst);
        if (!isFirst) return 0;

        var cfg = Config.Load();
        var app = new App(cfg, State.Load());
        app.Start();
        // After Start, so the widget is already in the taskbar the message points at.
        if (args.Contains("--welcome")) Install.Welcome();
        Application.Run();
        return 0;
    }

    private static int SelfTest()
    {
        int[] session = [50, 80, 95], weekly = [30, 40, 50, 60, 70, 80, 90, 100];
        var cases = new (double Pct, int[] T, int Last, int? Fire, int NewLast)[]
        {
            (10, session, 0, null, 0),
            (55, session, 0, 50, 50),
            (60, session, 50, null, 50),
            (99, session, 50, 95, 95),   // jumps straight to the highest crossed, no double ping
            (3, session, 95, null, 0),   // window reset rearms silently
            (59, weekly, 0, 50, 50),
            (100, weekly, 90, 100, 100),
        };
        var failed = 0;
        foreach (var c in cases)
        {
            var (fire, last) = Alerts.Check(c.Pct, c.T, c.Last);
            var ok = fire == c.Fire && last == c.NewLast;
            if (!ok) failed++;
            Console.WriteLine($"{(ok ? "ok  " : "FAIL")} {c.Pct}% last={c.Last} -> fire={fire?.ToString() ?? "-"} last={last}");
        }
        // The saved last values must survive a save/load round trip, or restarts show nothing.
        var saved = new State { Last = new Usage(new Limit(20, DateTimeOffset.Parse("2026-10-25T12:00:00Z")), new Limit(82, null)) };
        var back = Json.RoundTrip(saved);
        var trip = back.Last == saved.Last;
        if (!trip) failed++;
        Console.WriteLine($"{(trip ? "ok  " : "FAIL")} state round trip -> {back.Last}");

        Console.WriteLine(failed == 0 ? "selftest passed" : $"{failed} failed");
        return failed == 0 ? 0 : 1;
    }
}

internal sealed class App(Config cfg, State state)
{
    private readonly ToolStripMenuItem _sessionInfo = new() { Enabled = false };
    private readonly ToolStripMenuItem _weeklyInfo = new() { Enabled = false };
    private readonly ToolStripMenuItem _statusInfo = new() { Enabled = false };
    private Widget _widget = null!;
    private CancellationTokenSource _wake = new();

    private Usage? _last;
    private TimeSpan _backoff;
    private string _published = "";
    private DateTime _publishedAt;

    public void Start()
    {
        _widget = new Widget(BuildMenu());
        // Last known values first: a restart that hits the rate limit would otherwise
        // leave the widget empty for up to half an hour.
        _last = state.Last;
        if (_last != null)
        {
            _widget.Show(_last, stale: true);
            SetInfo(_last, $"Stand {state.LastFetched:HH:mm}");
        }
        _ = Loop();
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.AddRange([_sessionInfo, _weeklyInfo, _statusInfo, new ToolStripSeparator()]);
        menu.Items.Add("Jetzt aktualisieren", null, (_, _) => _wake.Cancel());
        menu.Items.Add("Nutzung auf claude.ai öffnen", null, (_, _) =>
            Process.Start(new ProcessStartInfo("https://claude.ai/settings/usage") { UseShellExecute = true }));
        var copy = new ToolStripMenuItem("ntfy-Topic kopieren", null, (_, _) => Clipboard.SetText(cfg.NtfyTopic)) { Enabled = cfg.NtfyEnabled };
        var ntfy = new ToolStripMenuItem("Handy-Benachrichtigungen (ntfy)") { CheckOnClick = true, Checked = cfg.NtfyEnabled };
        ntfy.CheckedChanged += (_, _) =>
        {
            SetNtfy(ntfy.Checked);
            copy.Enabled = ntfy.Checked;
        };
        menu.Items.AddRange([new ToolStripSeparator(), ntfy, copy, new ToolStripSeparator()]);
        menu.Items.Add("Einstellungen öffnen", null, (_, _) =>
            Process.Start(new ProcessStartInfo(Path.Combine(Config.Dir, "config.json")) { UseShellExecute = true }));

        var autostart = new ToolStripMenuItem("Mit Windows starten") { CheckOnClick = true, Checked = Install.IsAutostart() };
        autostart.CheckedChanged += (_, _) => Install.SetAutostart(autostart.Checked);
        menu.Items.Add(autostart);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Beenden", null, (_, _) => Application.Exit());
        menu.Items.Add("Deinstallieren", null, (_, _) => Install.Uninstall());
        return menu;
    }

    /// Off by default: nothing leaves the PC until the user opts in. The topic is created
    /// on first opt-in; ntfy.sh needs no signup, a topic exists once something is sent to it.
    private void SetNtfy(bool on)
    {
        cfg.NtfyEnabled = on;
        if (on && cfg.NtfyTopic == "") cfg.NtfyTopic = Config.NewTopic();
        cfg.Save();
        if (!on) return;

        Clipboard.SetText(cfg.NtfyTopic);
        // Send the known values right away so the phone has data at once, without an extra
        // API call: the usage endpoint's rate limit is tight and shared with Claude Code.
        _publishedAt = default;
        if (_last != null) _ = NotifyNow(_last);
        MessageBox.Show(
            "Handy-Benachrichtigungen sind an.\n\n" +
            "1. App \"ntfy\" installieren (iPhone oder Android).\n" +
            "2. Dort auf \"+\" tippen und dieses Topic abonnieren (liegt schon in der Zwischenablage):\n\n" +
            cfg.NtfyTopic + "\n\n" +
            "Pushes kommen bei Session 50/80/95 % und Woche ab 30 % alle 10 % (änderbar unter Einstellungen).\n\n" +
            "Das Topic wirkt wie ein Passwort: Wer es kennt, kann mitlesen.",
            "Claude Usage Widget", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    // Runs on the UI thread; every await is real async I/O, so the taskbar never waits on us.
    private async Task Loop()
    {
        // Restarts within the poll interval reuse the saved values instead of calling the
        // API again; a burst of restarts was enough to trip the rate limit.
        var wait = state.LastFetched + TimeSpan.FromSeconds(cfg.PollSeconds) - DateTime.Now;
        if (wait > TimeSpan.Zero) await Sleep(wait);

        while (true)
        {
            var delay = TimeSpan.FromSeconds(cfg.PollSeconds);
            try
            {
                _last = await UsageApi.FetchAsync();
                _backoff = TimeSpan.Zero;
                state.Last = _last;
                state.LastFetched = DateTime.Now;
                state.Save();
                _widget.Show(_last, stale: false);
                SetInfo(_last, $"Stand {DateTime.Now:HH:mm}");
                await Notify(_last);
            }
            catch (Exception ex)
            {
                if (ex is UsageException { RetryAfter: { } ra })
                {
                    // The API sends "Retry-After: 0" with its 429s; taking that literally
                    // retried instantly and kept the rate limit alive forever.
                    _backoff = _backoff == TimeSpan.Zero ? TimeSpan.FromMinutes(5) : _backoff * 2;
                    if (_backoff > TimeSpan.FromMinutes(30)) _backoff = TimeSpan.FromMinutes(30);
                    delay = ra > _backoff ? ra : _backoff;
                }
                var msg = ex is UsageException ? ex.Message : "Offline";
                _widget.Show(_last, stale: true);
                var age = _last != null ? $", Stand {state.LastFetched:HH:mm}" : "";
                _statusInfo.Text = $"{msg}{age}, nächster Versuch {DateTime.Now + delay:HH:mm}";
            }

            await Sleep(delay);
        }
    }

    /// "Jetzt aktualisieren" cancels the sleep.
    private async Task Sleep(TimeSpan delay)
    {
        try { await Task.Delay(delay, _wake.Token); }
        catch (TaskCanceledException) { _wake = new(); }
    }

    private async Task NotifyNow(Usage u)
    {
        try { await Notify(u); }
        catch (Exception ex) { _statusInfo.Text = "ntfy nicht erreichbar: " + ex.Message; }
    }

    private void SetInfo(Usage u, string status)
    {
        _sessionInfo.Text = $"Session {u.Session.Percent:0} %   Reset {UsageApi.FormatReset(u.Session.ResetsAt)}";
        _weeklyInfo.Text = $"Woche {u.Weekly.Percent:0} %   Reset {UsageApi.FormatReset(u.Weekly.ResetsAt)}";
        _statusInfo.Text = status;
    }

    private async Task Notify(Usage u)
    {
        if (!cfg.NtfyEnabled) return;
        var (sFire, sLast) = Alerts.Check(u.Session.Percent, cfg.SessionAlerts, state.SessionNotified);
        var (wFire, wLast) = Alerts.Check(u.Weekly.Percent, cfg.WeeklyAlerts, state.WeeklyNotified);

        // State is saved only after a successful send, so a failed push is retried next poll.
        if (sFire is { } s)
            await Ntfy.SendAsync(cfg.NtfyTopic, $"Claude Session {s} %",
                $"Jetzt {u.Session.Percent:0} %, Reset {UsageApi.FormatReset(u.Session.ResetsAt)}",
                s >= 95 ? 5 : s >= 80 ? 4 : 3, s >= 95 ? "rotating_light" : "hourglass");
        if (wFire is { } w)
            await Ntfy.SendAsync(cfg.NtfyTopic, $"Claude Woche {w} %",
                $"Jetzt {u.Weekly.Percent:0} %, Reset {UsageApi.FormatReset(u.Weekly.ResetsAt)}",
                w >= 90 ? 5 : w >= 70 ? 4 : 3, "calendar");
        if (sLast != state.SessionNotified || wLast != state.WeeklyNotified)
        {
            state.SessionNotified = sLast;
            state.WeeklyNotified = wLast;
            state.Save();
        }

        // Status topic for the iPhone shortcut/widget. Throttled because the free ntfy.sh
        // tier caps daily messages; the hourly resend keeps it inside ntfy's 12 h cache.
        var text = $"Session {u.Session.Percent:0} % (Reset {UsageApi.FormatReset(u.Session.ResetsAt)})\n" +
                   $"Woche {u.Weekly.Percent:0} % (Reset {UsageApi.FormatReset(u.Weekly.ResetsAt)})";
        var since = DateTime.Now - _publishedAt;
        if ((text != _published && since.TotalMinutes >= cfg.StatusMinutes) || since.TotalMinutes >= 60)
        {
            await Ntfy.SendAsync(cfg.StatusTopic, "Claude Usage", text + $"\nStand {DateTime.Now:HH:mm}", 1);
            _published = text;
            _publishedAt = DateTime.Now;
        }
    }
}
