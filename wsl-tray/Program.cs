using System.Runtime.InteropServices;

namespace WslTray;

internal static class Program
{
    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);
    private const int AttachParentProcess = -1;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            // WinExe has no console of its own; borrow the calling shell's.
            AttachConsole(AttachParentProcess);
            var code = SelfTest.Run();

            // --with-vm additionally restarts the real VM to prove the rollback path.
            if (args.Any(a => a.Equals("--with-vm", StringComparison.OrdinalIgnoreCase)))
                code |= SelfTest.RunVmIntegration();

            return code;
        }

        if (args.Any(a => a.Equals("--probe", StringComparison.OrdinalIgnoreCase)))
        {
            AttachConsole(AttachParentProcess);
            return Probe();
        }

        // Before the single-instance check, not after: with autostart on, the copy in
        // %APPDATA% is already running, so double-clicking a newer build would exit below
        // and every boot would go on starting the old one.
        SettingsPanel.RefreshInstalledCopy();

        // One tray icon is enough. Second launch just exits.
        using var mutex = new Mutex(true, @"Local\WslTray.SingleInstance", out var isFirst);
        if (!isFirst) return 0;

        ApplicationConfiguration.Initialize();

        if (!WslAvailable())
        {
            MessageBox.Show(
                "wsl.exe was not found on this machine.\n\nWSL Tray needs WSL2 installed.",
                "WSL Tray", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        var cfg = Config.Load();
        if (cfg.LoadError != null)
        {
            MessageBox.Show(
                "The config file could not be read:\n\n" + cfg.LoadError,
                "WSL Tray", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        using var tray = new TrayApp(cfg);

        // First run has no config yet: show the window, otherwise launching the .exe
        // looks like nothing happened at all. --window forces it open every time.
        if (!File.Exists(cfg.Path) || args.Any(a => a.Equals("--window", StringComparison.OrdinalIgnoreCase)))
        {
            tray.ShowWindow();
            // Writing the config is what makes this run the first one. Without it the window
            // opens at every launch forever, which with autostart on means at every boot.
            try { cfg.Save(); } catch { /* read-only location: the window just opens again */ }
        }

        Application.Run();
        return 0;
    }

    /// Takes two real samples against the live machine and prints them. Rates need two
    /// readings, so a single poll can never show a CPU figure.
    private static int Probe()
    {
        var cfg = Config.Load();
        var mon = new Monitor(cfg);
        mon.Poll();
        Thread.Sleep(1500);
        var s = mon.Poll();

        Console.WriteLine($"vm running   : {s.VmRunning}");
        Console.WriteLine($"cpu          : {s.VmCpuPercent:0.0} % of {s.Cpus} cores");
        Console.WriteLine($"memory       : {Fmt.Kb(s.MemUsedKb)} / {Fmt.Kb(s.MemTotalKb)}  ({s.MemPercent:0.0} %)");
        Console.WriteLine($"swap         : {Fmt.Kb(s.SwapUsedKb)} / {Fmt.Kb(s.SwapTotalKb)}");
        Console.WriteLine($"disk in vm   : {Fmt.Kb(s.DiskUsedKb)} / {Fmt.Kb(s.DiskTotalKb)}  ({s.DiskPercent:0.0} %)");
        Console.WriteLine($"host vmmem   : {Fmt.Bytes(s.HostVmmemBytes)}");

        Console.WriteLine("distros:");
        foreach (var d in s.Distros)
            Console.WriteLine($"  {d.Name,-16} {(d.Running ? "running" : "stopped"),-8} " +
                              $"cpu {d.CpuPercent,5:0.0} %  rss {Fmt.Kb(d.RssKb),9}  vhdx {Fmt.Bytes(d.VhdxBytes),9}");

        Console.WriteLine("containers:");
        if (s.Containers.Count == 0) Console.WriteLine("  (none)");
        foreach (var c in s.Containers)
            Console.WriteLine($"  [{c.Distro}] {c.Name,-22} cpu {c.CpuPercent,5:0.0} %  mem {Fmt.Bytes(c.MemBytes),9}");

        var online = Wsl.ListOnline();
        Console.WriteLine($"catalog       : {online.Count} installable" +
                          (online.Count > 0 ? "  (e.g. " + online[0].Name + ")" : "  (could not be read)"));

        var l = Limits.ReadVmLimits();
        Console.WriteLine("wslconfig     : memory=" + (l.Memory ?? "(default)") +
                          "  processors=" + (l.Processors ?? "(default)") +
                          "  swap=" + (l.Swap ?? "(default)"));
        var invalid = Limits.Validate(l);
        if (invalid.Length > 0) Console.WriteLine("WARNING       : " + invalid);

        mon.Dispose();
        return s.VmRunning ? 0 : 2;
    }

    private static bool WslAvailable()
    {
        try
        {
            Wsl.Run("wsl.exe", new[] { "--status" }, 10000, out var code);
            return code == 0 || code == 1; // 1 = installed but no distro yet
        }
        catch { return false; }
    }
}
