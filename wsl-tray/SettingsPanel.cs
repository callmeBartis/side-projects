using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace WslTray;

public sealed class SettingsPanel : Panel
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "WslTray";

    private readonly Config _cfg;
    private readonly Label _status = UiHelp.Lbl("", Theme.Body, Theme.Muted);

    public SettingsPanel(int width, Config cfg)
    {
        _cfg = cfg;
        BackColor = Theme.Bg;

        var head = UiHelp.Lbl("Settings", Theme.Title);
        head.Location = new Point(0, 0);
        Controls.Add(head);

        var card = UiHelp.Card();
        card.Location = new Point(0, 30);
        card.Size = new Size(width, 240);

        var poll = UiHelp.Num(cfg.PollSeconds, 1, 300);
        Field(card, "Poll interval", poll, 20, "seconds between CPU/RAM samples");

        var dockerPoll = UiHelp.Num(cfg.DockerPollSeconds, 1, 600);
        Field(card, "Docker interval", dockerPoll, 58, "docker stats costs ~1s, keep this higher");

        var autostart = UiHelp.Check("start with Windows");
        autostart.Checked = IsAutostartOn();
        autostart.Location = new Point(168, 98);
        autostart.CheckedChanged += (s, e) => Toggle(
            () => SetAutostart(autostart.Checked),
            autostart.Checked
                ? "Autostart enabled. A copy boots from %APPDATA%\\wsl-tray, so this .exe can be moved or deleted."
                : "Autostart disabled. The copy stays in %APPDATA%\\wsl-tray.",
            "Autostart failed: ");
        card.Controls.Add(autostart);

        var startMenu = UiHelp.Check("show in Start menu");
        startMenu.Checked = IsInStartMenu();
        startMenu.Location = new Point(168, 130);
        startMenu.CheckedChanged += (s, e) => Toggle(
            () => SetStartMenu(startMenu.Checked),
            startMenu.Checked
                ? "Added to the Start menu. Windows search finds it as \"WSL Tray\" within a few seconds."
                : "Removed from the Start menu.",
            "Start menu entry failed: ");
        card.Controls.Add(startMenu);

        var save = new FlatBtn("Save", primary: true) { Location = new Point(168, 164), Width = 96 };
        save.Click += (s, e) =>
        {
            _cfg.PollSeconds = poll.Value;
            _cfg.DockerPollSeconds = Math.Max(dockerPoll.Value, poll.Value);
            try
            {
                _cfg.Save();
                TrayApp.Instance?.RestartMonitor();
                _status.ForeColor = Theme.Ok;
                _status.Text = "Saved.";
            }
            catch (Exception ex)
            {
                _status.ForeColor = Theme.Crit;
                _status.Text = "Save failed: " + ex.Message;
            }
        };
        card.Controls.Add(save);

        _status.Location = new Point(18, 206);
        _status.MaximumSize = new Size(width - 36, 0);
        card.Controls.Add(_status);

        Controls.Add(card);

        var whereHead = UiHelp.Lbl("Config", Theme.BodyBold);
        whereHead.Location = new Point(0, 290);
        Controls.Add(whereHead);

        // A boot copy that cannot start fails where nobody is looking, so say it here.
        if ((IsAutostartOn() || IsInStartMenu()) && InstalledCopyLooksBroken())
        {
            _status.ForeColor = Theme.Crit;
            _status.Text = "The installed copy in %APPDATA%\\wsl-tray is not a self-contained build and " +
                           "will not start. Run the published single-file .exe once to replace it.";
        }

        var where = UiHelp.Lbl(
            cfg.Path + "\n\n" +
            "Put a config.json next to the .exe to run fully portable - the file next to the\n" +
            "executable wins, so the tool works from a USB stick or a copy on another PC.\n\n" +
            "Autostart and the Start menu entry both run\n" + InstalledExePath + ",\n" +
            "a copy this tool owns, so the file you downloaded is free to move or delete.\n" +
            "Starting a newer build by hand refreshes that copy. Turning both options off\n" +
            "leaves it in place; delete the folder by hand to get rid of it.",
            Theme.Small, Theme.Muted);
        where.Location = new Point(0, 316);
        where.MaximumSize = new Size(width, 0);
        Controls.Add(where);
    }

    /// Both checkboxes do the same dance: act, then report to the same status line.
    private void Toggle(Action act, string done, string failedPrefix)
    {
        try
        {
            act();
            _status.ForeColor = Theme.Ok;
            _status.Text = done;
        }
        catch (Exception ex)
        {
            _status.ForeColor = Theme.Crit;
            _status.Text = failedPrefix + ex.Message;
        }
    }

    private static void Field(Panel card, string label, Control input, int y, string hint)
    {
        var l = UiHelp.Lbl(label, Theme.BodyBold);
        l.Location = new Point(18, y + 6);
        card.Controls.Add(l);

        input.Location = new Point(168, y);
        card.Controls.Add(input);

        var h = UiHelp.Lbl(hint, Theme.Small, Theme.Muted);
        h.Location = new Point(168 + input.Width + 14, y + 6);
        card.Controls.Add(h);
    }

    public static bool IsAutostartOn()
    {
        try
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(RunValue) != null;
        }
        catch { return false; }
    }

    private static string InstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "wsl-tray");

    /// Where an enabled autostart boots from. A Run entry pointing at wherever the .exe
    /// happened to sit when it was ticked breaks the moment that file is moved, deleted
    /// or replaced - and it breaks silently, at boot, with nothing on screen to say so.
    public static string InstalledExePath => Path.Combine(InstallDir, "wsl-tray.exe");

    private static bool RunningFromInstalledCopy =>
        string.Equals(Environment.ProcessPath, InstalledExePath, StringComparison.OrdinalIgnoreCase);

    public static void SetAutostart(bool on)
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                      ?? Registry.CurrentUser.CreateSubKey(RunKey);

        // The copy stays either way: the Start menu entry may still point at it, and a
        // 49 MB file the user can delete themselves is better than one that vanishes.
        if (!on) { k.DeleteValue(RunValue, throwOnMissingValue: false); return; }

        k.SetValue(RunValue, "\"" + InstallCopy() + "\"");
    }

    private static string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), "WSL Tray.lnk");

    public static bool IsInStartMenu() => File.Exists(ShortcutPath);

    /// A .lnk in the per-user Programs folder is what makes the tool findable by typing
    /// its name: that folder is what the search index reads. No admin rights involved.
    public static void SetStartMenu(bool on)
    {
        if (!on)
        {
            if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath);
            return;
        }

        var target = InstallCopy();

        // .NET has no shortcut API. The shell's own scripting object is the shortest way
        // to a real .lnk, and late binding keeps it free of COM interface declarations.
        var shell = Type.GetTypeFromProgID("WScript.Shell")
                    ?? throw new InvalidOperationException("WScript.Shell is not registered on this machine");
        var host = Activator.CreateInstance(shell)!;
        try
        {
            var lnk = shell.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, host,
                new object[] { ShortcutPath })!;
            Set(lnk, "TargetPath", target);
            Set(lnk, "WorkingDirectory", Path.GetDirectoryName(target)!);
            Set(lnk, "Description", "WSL2 usage, limits and distros");
            lnk.GetType().InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
        }
        finally { Marshal.FinalReleaseComObject(host); }

        static void Set(object lnk, string property, string value) =>
            lnk.GetType().InvokeMember(property, BindingFlags.SetProperty, null, lnk, new object[] { value });
    }

    /// Copies the running .exe next to the config, so the downloaded file can be deleted
    /// or moved afterwards. Returns the path the entries should point at.
    /// True when the running .exe cannot stand on its own. A "dotnet build" apphost keeps its
    /// code in wsl-tray.dll beside it; only a published single-file build carries everything
    /// inside. Copying the apphost alone produces a copy that dies at every boot with
    /// "The application to execute does not exist: ...\wsl-tray.dll" - silently, because
    /// nothing is on screen at boot to catch it.
    internal static bool NeedsItsFolder(string exe) => File.Exists(Path.ChangeExtension(exe, ".dll"));

    /// A copy far too small to be the single-file build is one of those broken apphosts.
    public static bool InstalledCopyLooksBroken()
    {
        try
        {
            var f = new FileInfo(InstalledExePath);
            return f.Exists && f.Length < 5L * 1024 * 1024;
        }
        catch { return false; }
    }

    private static string InstallCopy()
    {
        var self = Environment.ProcessPath;
        if (self == null) throw new InvalidOperationException("cannot determine this .exe's own path");
        if (RunningFromInstalledCopy) return self;

        if (NeedsItsFolder(self))
            throw new InvalidOperationException(
                "this build is not self-contained - it needs wsl-tray.dll next to it, so a lone copy " +
                "of the .exe cannot start. Use the published single-file build (dotnet publish).");

        Directory.CreateDirectory(InstallDir);
        if (!SameBuild(self, InstalledExePath))
        {
            MoveAsideIfLocked();
            File.Copy(self, InstalledExePath, overwrite: true);
        }
        return InstalledExePath;
    }

    /// With autostart on, the copy being replaced is usually the one running from boot.
    /// Windows refuses to overwrite a running .exe but will happily rename one, so the old
    /// build steps aside and is swept up on a later start.
    private static void MoveAsideIfLocked()
    {
        if (!File.Exists(InstalledExePath)) return;
        try { File.Delete(InstalledExePath); return; } catch { /* in use */ }
        try { File.Move(InstalledExePath, InstalledExePath + "." + DateTime.UtcNow.Ticks + ".old"); }
        catch { /* leave it; the copy below will fail and be reported */ }
    }

    private static void SweepOldCopies()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(InstallDir, "*.old"))
                try { File.Delete(f); } catch { /* still running, next time */ }
        }
        catch { /* no install dir yet */ }
    }

    /// File.Copy keeps the write time, so size plus timestamp identifies the same build
    /// without hashing 49 MB on every start.
    private static bool SameBuild(string a, string b)
    {
        var x = new FileInfo(a);
        var y = new FileInfo(b);
        return x.Exists && y.Exists && x.Length == y.Length && x.LastWriteTimeUtc == y.LastWriteTimeUtc;
    }

    /// Keeps the installed copy at whatever version was last started by hand, and repairs
    /// entries left behind by an older build. Without this, running a new build would
    /// leave every boot and every Start menu click on the old one.
    public static void RefreshInstalledCopy()
    {
        try
        {
            SweepOldCopies();

            var autostart = IsAutostartOn();
            var startMenu = IsInStartMenu();
            if (!autostart && !startMenu) return; // nothing points at the copy

            if (!RunningFromInstalledCopy) InstallCopy();

            // Rewritten even when this process is the copy: an entry left by an older build
            // can still point at a download that has since been deleted, and that is the
            // failure this whole mechanism exists to prevent.
            if (autostart) SetAutostart(true);
            if (startMenu) SetStartMenu(true);
        }
        catch { /* an entry on an older build is not worth blocking startup */ }
    }
}
