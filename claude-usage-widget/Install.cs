using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ClaudeUsageWidget;

/// Double-clicking the downloaded .exe installs it: copy to a fixed per-user folder,
/// autostart, Start menu entry, then hand over to that copy. No admin rights needed.
internal static class Install
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunName = "ClaudeUsageWidget";

    private static string Dir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "claude-usage-widget");
    public static string ExePath => Path.Combine(Dir, "claude-usage-widget.exe");
    private static string ShortcutPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Claude Usage Widget.lnk");

    public static bool RunningInstalled =>
        string.Equals(Environment.ProcessPath, ExePath, StringComparison.OrdinalIgnoreCase);

    /// A "dotnet build" apphost keeps its code in a .dll next to it, so a lone copy of it
    /// would not start. Those builds just run in place.
    public static bool IsDevBuild =>
        File.Exists(Path.ChangeExtension(Environment.ProcessPath!, ".dll"));

    /// Returns false when the installation failed and was reported.
    public static bool Run()
    {
        try
        {
            // An older version may be running from the install folder and lock its .exe.
            foreach (var p in Process.GetProcessesByName("claude-usage-widget"))
            {
                if (p.Id == Environment.ProcessId) continue;
                try { p.Kill(); p.WaitForExit(5000); } catch { /* already gone */ }
            }

            Directory.CreateDirectory(Dir);
            File.Copy(Environment.ProcessPath!, ExePath, overwrite: true);
            SetAutostart(true);
            CreateShortcut();
            Process.Start(new ProcessStartInfo(ExePath, "--welcome") { UseShellExecute = false });
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show("Installation fehlgeschlagen:\n\n" + ex.Message, "Claude Usage Widget",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
    }

    public static void Welcome()
    {
        var loggedIn = File.Exists(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", ".credentials.json"));
        MessageBox.Show(
            "Installiert. Das Widget sitzt unten rechts in der Taskleiste, links neben den Tray-Icons, " +
            "und startet ab jetzt mit Windows.\n\n" +
            (loggedIn ? "" : "ACHTUNG: Kein Claude-Code-Login gefunden. Claude Code installieren und einmal anmelden, " +
                             "sonst bleibt das Widget leer.\n\n") +
            "Klick auf das Widget öffnet das Menü. Dort lassen sich auch Push-Benachrichtigungen " +
            "aufs Handy einschalten (\"Handy-Benachrichtigungen\").",
            "Claude Usage Widget", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    public static void Uninstall()
    {
        if (MessageBox.Show("Claude Usage Widget komplett entfernen (inkl. Einstellungen)?", "Claude Usage Widget",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;

        SetAutostart(false);
        if (File.Exists(ShortcutPath)) File.Delete(ShortcutPath);

        // A running .exe cannot delete itself; a detached cmd waits until we have exited.
        Process.Start(new ProcessStartInfo("cmd.exe",
            $"/c ping -n 3 127.0.0.1 >nul & rmdir /s /q \"{Dir}\" & rmdir /s /q \"{Config.Dir}\"")
        { CreateNoWindow = true, UseShellExecute = false });
        Application.Exit();
    }

    public static bool IsAutostart() =>
        Registry.CurrentUser.OpenSubKey(RunKey)?.GetValue(RunName) != null;

    public static void SetAutostart(bool on)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) key.SetValue(RunName, $"\"{(IsDevBuild ? Environment.ProcessPath : ExePath)}\"");
        else key.DeleteValue(RunName, false);
    }

    // Same late-bound WScript.Shell route as wsl-tray: .NET has no shortcut API.
    private static void CreateShortcut()
    {
        var shell = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell fehlt");
        var host = Activator.CreateInstance(shell)!;
        try
        {
            var lnk = shell.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, host, [ShortcutPath])!;
            Set(lnk, "TargetPath", ExePath);
            Set(lnk, "WorkingDirectory", Dir);
            Set(lnk, "Description", "Claude session and weekly usage in the taskbar");
            lnk.GetType().InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
        }
        finally { Marshal.FinalReleaseComObject(host); }

        static void Set(object lnk, string prop, string value) =>
            lnk.GetType().InvokeMember(prop, BindingFlags.SetProperty, null, lnk, [value]);
    }
}
