using System.Text;

namespace WslTray;

/// Runnable with: wsl-tray.exe --selftest
/// Covers the parsers, the value validation and the .wslconfig rewriter - the parts
/// where a silent bug either shows wrong numbers or leaves the VM unable to boot.
public static class SelfTest
{
    private static int _failed;

    public static int Run()
    {
        var log = new StringBuilder();
        void Check(string what, bool ok, string detail = "")
        {
            if (!ok) _failed++;
            log.AppendLine((ok ? "PASS  " : "FAIL  ") + what + (detail.Length > 0 ? "  -> " + detail : ""));
        }

        // --- sample parser, against real output captured from this machine -------------
        const string sample = """
            @stat
            cpu  2026 0 1745 1084170 209 0 552 0 0 0
            @cpus
            10
            @mem
            MemTotal:       24609920 kB
            MemAvailable:   23560188 kB
            SwapTotal:             0 kB
            SwapFree:              0 kB
            @df
            /dev/sdc       1055762868 12726276 989333120   2% /
            @jiffies
            2299
            @rss
            827572
            @docker
            portainer|0.00%|20.01MiB / 23.47GiB
            discord-mcp|0.13%|289MiB / 23.47GiB
            """;

        var r = Wsl.ParseSample(sample);
        Check("cpu total jiffies", r.CpuTotalJiffies == 2026 + 1745 + 1084170 + 209 + 552,
            r.CpuTotalJiffies.ToString());
        Check("cpu idle+iowait", r.CpuIdleJiffies == 1084170 + 209, r.CpuIdleJiffies.ToString());
        Check("cpus", r.Cpus == 10, r.Cpus.ToString());
        Check("mem total", r.MemTotalKb == 24609920, r.MemTotalKb.ToString());
        Check("mem available", r.MemAvailableKb == 23560188, r.MemAvailableKb.ToString());
        Check("swap total zero", r.SwapTotalKb == 0, r.SwapTotalKb.ToString());
        Check("disk total", r.DiskTotalKb == 1055762868, r.DiskTotalKb.ToString());
        Check("disk used", r.DiskUsedKb == 12726276, r.DiskUsedKb.ToString());
        Check("proc jiffies", r.ProcJiffies == 2299, r.ProcJiffies.ToString());
        Check("proc rss", r.ProcRssKb == 827572, r.ProcRssKb.ToString());
        Check("containers parsed", r.Containers.Count == 2, r.Containers.Count.ToString());
        Check("container mem", r.Containers.Count > 0 && r.Containers[0].MemBytes == (long)(20.01 * 1024 * 1024),
            r.Containers.Count > 0 ? r.Containers[0].MemBytes.ToString() : "-");
        Check("container cpu", r.Containers.Count > 1 && Math.Abs(r.Containers[1].CpuPercent - 0.13) < 0.001,
            r.Containers.Count > 1 ? r.Containers[1].CpuPercent.ToString() : "-");

        var nodocker = Wsl.ParseSample("@stat\ncpu 1 0 1 1 0\n@docker\n@nodocker\n");
        Check("nodocker marker", !nodocker.DockerPresent && nodocker.Containers.Count == 0);

        // --- size / percent parsing ---------------------------------------------------
        Check("size GiB", Wsl.ParseSize("1.5GiB") == (long)(1.5 * 1024 * 1024 * 1024));
        Check("size plain B", Wsl.ParseSize("0B") == 0);
        Check("size garbage", Wsl.ParseSize("n/a") == 0);
        Check("percent", Math.Abs(Wsl.ParsePercent(" 12.34% ") - 12.34) < 0.0001);
        Check("percent garbage", Wsl.ParsePercent("--") == 0);

        // --- distro list (UTF-16 leftovers must not survive) ---------------------------
        var list = Wsl.ParseDistroList("Ubuntu\r\n\0Debian\r\n\r\n");
        Check("distro list", list.Count == 2 && list[0] == "Ubuntu" && list[1] == "Debian",
            string.Join(",", list));

        // --- .wslconfig VALUE VALIDATION ----------------------------------------------
        // This is the bug that killed a real VM: "memory=10" is ten BYTES to WSL, and
        // the VM then fails with Wsl/Service/CreateInstance/CreateVm/E_UNEXPECTED.
        Check("bare number rejected", Limits.ParseSizeValue("10") == null, "10");
        Check("bare number rejected (large)", Limits.ParseSizeValue("16384") == null);
        Check("zero accepted", Limits.ParseSizeValue("0") == 0);
        Check("GB parsed", Limits.ParseSizeValue("16GB") == 16L * 1024 * 1024 * 1024);
        Check("MB parsed", Limits.ParseSizeValue("512MB") == 512L * 1024 * 1024);
        Check("KB parsed", Limits.ParseSizeValue("1024KB") == 1024L * 1024);
        Check("lowercase unit", Limits.ParseSizeValue("8gb") == 8L * 1024 * 1024 * 1024);
        Check("space before unit", Limits.ParseSizeValue("8 GB") == 8L * 1024 * 1024 * 1024);
        Check("nonsense unit rejected", Limits.ParseSizeValue("8XB") == null);
        Check("empty rejected", Limits.ParseSizeValue("") == null);

        Check("validate: bare memory rejected",
            Limits.Validate(new Limits.VmLimits { Memory = "10" }).Length > 0);
        Check("validate: tiny memory rejected",
            Limits.Validate(new Limits.VmLimits { Memory = "16MB" }).Length > 0);
        Check("validate: sane memory accepted",
            Limits.Validate(new Limits.VmLimits { Memory = "2GB" }).Length == 0,
            Limits.Validate(new Limits.VmLimits { Memory = "2GB" }));
        Check("validate: processors over core count rejected",
            Limits.Validate(new Limits.VmLimits { Processors = (Environment.ProcessorCount + 2).ToString() }).Length > 0);
        Check("validate: processors zero rejected",
            Limits.Validate(new Limits.VmLimits { Processors = "0" }).Length > 0);
        Check("validate: processors non-numeric rejected",
            Limits.Validate(new Limits.VmLimits { Processors = "many" }).Length > 0);
        Check("validate: all cores accepted",
            Limits.Validate(new Limits.VmLimits { Processors = Environment.ProcessorCount.ToString() }).Length == 0);
        Check("validate: swap 0 accepted",
            Limits.Validate(new Limits.VmLimits { Swap = "0" }).Length == 0);
        Check("validate: bare swap rejected",
            Limits.Validate(new Limits.VmLimits { Swap = "8" }).Length > 0);
        Check("validate: memory above physical RAM rejected",
            Limits.Validate(new Limits.VmLimits { Memory = "1024GB" }).Length > 0);
        Check("validate: nothing set is fine",
            Limits.Validate(new Limits.VmLimits()).Length == 0);

        Check("host RAM readable", Limits.HostPhysicalMemoryBytes() > 0,
            Fmt.Bytes(Limits.HostPhysicalMemoryBytes()));

        // --- .wslconfig rewrite: must preserve comments and foreign sections ----------
        var tmp = Path.Combine(Path.GetTempPath(), "wsl-tray-selftest-" + Guid.NewGuid().ToString("N") + ".ini");
        var realPath = Limits.WslConfigPath;
        try
        {
            Limits.WslConfigPath = tmp;
            File.WriteAllText(tmp, string.Join("\n", new[]
            {
                "# hand written comment",
                "[wsl2]",
                "memory=24GB",
                "processors=10",
                "swap=0",
                "vmIdleTimeout=-1",
                "",
                "[experimental]",
                "autoMemoryReclaim=dropCache",
                "sparseVhd=true",
            }));

            Limits.WriteVmLimits(new Limits.VmLimits { Memory = "16GB", Processors = "6", Swap = "4GB" });
            var after = File.ReadAllText(tmp);

            Check("rewrite: memory changed", after.Contains("memory=16GB"));
            Check("rewrite: processors changed", after.Contains("processors=6"));
            Check("rewrite: swap changed", after.Contains("swap=4GB"));
            Check("rewrite: comment kept", after.Contains("# hand written comment"));
            Check("rewrite: unrelated wsl2 key kept", after.Contains("vmIdleTimeout=-1"));
            Check("rewrite: experimental section kept", after.Contains("[experimental]"));
            Check("rewrite: experimental keys kept",
                after.Contains("autoMemoryReclaim=dropCache") && after.Contains("sparseVhd=true"));
            Check("rewrite: no duplicate memory key",
                after.Split('\n').Count(l => l.Trim().StartsWith("memory=")) == 1);
            Check("rewrite: backup written", File.Exists(tmp + ".bak"));

            var read = Limits.ReadVmLimits();
            Check("read back memory", read.Memory == "16GB", read.Memory ?? "null");
            Check("read back processors", read.Processors == "6", read.Processors ?? "null");

            // Removing a cap must delete the key, not write an empty value.
            Limits.WriteVmLimits(new Limits.VmLimits { Memory = null, Processors = "8", Swap = null });
            var after2 = File.ReadAllText(tmp);
            Check("rewrite: null removes key", !after2.Contains("memory="));
            Check("rewrite: null keeps others", after2.Contains("processors=8"));
            Check("rewrite: experimental still there", after2.Contains("sparseVhd=true"));

            // A file with no [wsl2] section at all must gain one.
            File.WriteAllText(tmp, "[experimental]\nsparseVhd=true\n");
            Limits.WriteVmLimits(new Limits.VmLimits { Memory = "8GB" });
            var after3 = File.ReadAllText(tmp);
            Check("rewrite: creates wsl2 section", after3.Contains("[wsl2]") && after3.Contains("memory=8GB"));
            Check("rewrite: kept experimental when creating", after3.Contains("sparseVhd=true"));

            var lines3 = after3.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            var wsl2Idx = lines3.FindIndex(x => x == "[wsl2]");
            var memIdx = lines3.FindIndex(x => x.StartsWith("memory="));
            var nextSection = lines3.FindIndex(wsl2Idx + 1, x => x.StartsWith("["));
            Check("rewrite: key inside its section",
                wsl2Idx >= 0 && memIdx > wsl2Idx && (nextSection < 0 || memIdx < nextSection),
                $"wsl2@{wsl2Idx} mem@{memIdx} next@{nextSection}");

            // A hand-edited file with the same key twice: WSL reads the last one, so a
            // leftover duplicate silently overrode the value that was just written.
            File.WriteAllText(tmp, "[wsl2]\nmemory=8GB\nmemory=4GB\n");
            Limits.WriteVmLimits(new Limits.VmLimits { Memory = "16GB" });
            var dup = File.ReadAllText(tmp);
            Check("rewrite: duplicate key is dropped", !dup.Contains("memory=4GB"),
                dup.Replace("\r", "").Replace("\n", " | "));
            Check("rewrite: the written value is what reads back",
                Limits.ReadVmLimits().Memory == "16GB", Limits.ReadVmLimits().Memory ?? "null");

            // The rewriter must never be handed values that validation rejects. The baseline
            // is read here rather than reused from further up, so adding a test above this
            // one cannot quietly turn it into a comparison against the wrong file.
            var beforeRefusal = File.ReadAllText(tmp);
            Check("apply refuses invalid values without touching the file",
                Limits.ApplyAndRestart(new Limits.VmLimits { Memory = "10" }, null) is { Ok: false, RolledBack: false });
            Check("apply refusal left file alone", File.ReadAllText(tmp) == beforeRefusal);
        }
        finally
        {
            Limits.WslConfigPath = realPath;
            try { File.Delete(tmp); File.Delete(tmp + ".bak"); } catch { }
        }

        // --- online catalog parser ------------------------------------------------------
        // Real "wsl -l -o" output, German locale: neither the banner nor the header row
        // can be matched by text, so only the blank line before the table is load bearing.
        const string online = """
            Im Folgenden finden Sie eine Liste gueltiger Distributionen, die installiert werden koennen.
            Mit "wsl.exe --install <Distro>" installieren.

            NAME                            FRIENDLY NAME
            Ubuntu                          Ubuntu
            Ubuntu-24.04                    Ubuntu 24.04 LTS
            OracleLinux_9_5                 Oracle Linux 9.5
            archlinux                       Arch Linux
            """;

        var cat = Wsl.ParseOnlineList(online);
        Check("catalog: row count", cat.Count == 4, cat.Count.ToString());
        Check("catalog: banner skipped", cat.All(d => d.Name != "Im" && d.Name != "Mit"),
            string.Join(",", cat.Select(d => d.Name)));
        Check("catalog: header skipped", cat.All(d => d.Name != "NAME"));
        Check("catalog: name with dot", cat.Any(d => d.Name == "Ubuntu-24.04"));
        Check("catalog: name with underscore", cat.Any(d => d.Name == "OracleLinux_9_5"));
        Check("catalog: friendly name kept",
            cat.FirstOrDefault(d => d.Name == "Ubuntu-24.04")?.Friendly == "Ubuntu 24.04 LTS",
            cat.FirstOrDefault(d => d.Name == "Ubuntu-24.04")?.Friendly ?? "null");
        Check("catalog: garbage in, nothing out", Wsl.ParseOnlineList("").Count == 0);

        Check("name check rejects a path", !Wsl.IsDistroName(@"C:\evil"));
        Check("name check rejects a space", !Wsl.IsDistroName("my distro"));
        Check("name check accepts a real one", Wsl.IsDistroName("Ubuntu-24.04"));

        // wsl.exe does not infer the export format, so a wrong guess writes a file whose
        // name lies about its content.
        Check("format: tar.gz", Wsl.FormatFor(@"C:\b\Ubuntu-2026-09-10.tar.gz") == "tar.gz");
        Check("format: tar.xz", Wsl.FormatFor("x.tar.xz") == "tar.xz");
        Check("format: vhdx", Wsl.FormatFor("x.vhdx") == "vhd");
        Check("format: plain tar default", Wsl.FormatFor("x.tar") == "tar" && Wsl.FormatFor("x") == "tar");

        // --- limits form: the unit decides the field's ceiling ---------------------------
        // A real "memory=1536MB" used to load into a 1..1024 field, clamp to 1024 and be
        // written straight back to .wslconfig as a smaller limit, without a word said.
        {
            var box = UiHelp.Check("mem");
            var num = UiHelp.Num(16, 1, 1024);
            var toggle = new UnitToggle("GB", "MB");
            var warn = new List<string>();

            LimitsPanel.ApplySize("1536MB", box, num, toggle, 1, "memory", warn);
            Check("limits: 1536MB keeps its value", num.Value == 1536, num.Value.ToString());
            Check("limits: 1536MB keeps its unit", toggle.Unit == "MB", toggle.Unit);

            LimitsPanel.ApplySize("12GB", box, num, toggle, 1, "memory", warn);
            Check("limits: 12GB round-trips", num.Value == 12 && toggle.Unit == "GB",
                num.Value + toggle.Unit);

            LimitsPanel.ApplySize("0", box, num, toggle, 0, "swap", warn);
            Check("limits: swap 0 stays 0", num.Value == 0, num.Value.ToString());

            LimitsPanel.ApplySize("10", box, num, toggle, 1, "memory", warn);
            Check("limits: a bare number is refused, not loaded", !box.Checked && warn.Count == 1,
                string.Join(";", warn));

            // Flipping the unit has to convert the number. Re-ranging alone was worse than
            // the bug it replaced: 1536MB clamped to 1024GB, and 16GB stayed "16" as MB.
            // PerformClick mirrors the real order - the toggle flips, then the panel converts.
            LimitsPanel.ApplySize("1536MB", box, num, toggle, 1, "memory", warn);
            toggle.PerformClick();
            LimitsPanel.SwitchUnit(num, toggle, 1);
            Check("unit switch: 1536MB becomes 2GB, not 1024GB",
                num.Value == 2 && toggle.Unit == "GB", num.Value + toggle.Unit);

            toggle.PerformClick();
            LimitsPanel.SwitchUnit(num, toggle, 1);
            Check("unit switch: and back to 2048MB, not 2MB",
                num.Value == 2048 && toggle.Unit == "MB", num.Value + toggle.Unit);

            LimitsPanel.ApplySize("16GB", box, num, toggle, 1, "memory", warn);
            toggle.PerformClick();
            LimitsPanel.SwitchUnit(num, toggle, 1);
            Check("unit switch: 16GB becomes 16384MB",
                num.Value == 16384 && toggle.Unit == "MB", num.Value + toggle.Unit);
        }

        // Swap had no magnitude check at all, so a unit slip could ask WSL for a terabyte.
        var hostRam = Limits.HostPhysicalMemoryBytes();
        if (hostRam > 0)
        {
            Check("validate: swap far above RAM is refused",
                Limits.Validate(new Limits.VmLimits { Swap = "1048576MB" }).Length > 0);
            Check("validate: an ordinary swap still passes",
                Limits.Validate(new Limits.VmLimits { Swap = "8GB" }).Length == 0,
                Limits.Validate(new Limits.VmLimits { Swap = "8GB" }));
        }

        // --- the autostart copy has to be able to start -----------------------------------
        // A "dotnet build" apphost keeps its code in a sibling .dll. Installing that lone
        // .exe as the boot copy produced one that died at every boot with "The application
        // to execute does not exist", visible only in the Windows event log.
        var guardDir = Path.Combine(Path.GetTempPath(), "wsltray-selftest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(guardDir);
        try
        {
            var exe = Path.Combine(guardDir, "wsl-tray.exe");
            File.WriteAllText(exe, "x");
            Check("install guard: a self-contained .exe is installable", !SettingsPanel.NeedsItsFolder(exe));

            File.WriteAllText(Path.Combine(guardDir, "wsl-tray.dll"), "x");
            Check("install guard: an apphost with its dll beside it is refused",
                SettingsPanel.NeedsItsFolder(exe));
        }
        finally { try { Directory.Delete(guardDir, true); } catch { } }

        log.AppendLine();
        log.AppendLine(_failed == 0 ? "ALL PASSED" : _failed + " CHECK(S) FAILED");
        Console.WriteLine(log.ToString());
        return _failed == 0 ? 0 : 1;
    }

    /// Opt-in, destructive: wsl-tray.exe --selftest --with-vm
    /// Restarts the real VM several times (so every container stops and restarts) to prove
    /// the rollback path actually works. Never runs unless asked for explicitly.
    public static int RunVmIntegration()
    {
        var log = new StringBuilder();
        int failed = 0;
        void Check(string what, bool ok, string detail = "")
        {
            if (!ok) failed++;
            log.AppendLine((ok ? "PASS  " : "FAIL  ") + what + (detail.Length > 0 ? "  -> " + detail : ""));
        }

        var path = Limits.WslConfigPath;
        var original = File.Exists(path) ? File.ReadAllText(path) : null;
        var distro = Wsl.ListDistros().FirstOrDefault();
        if (distro == null) { Console.WriteLine("no distro installed, skipping"); return 0; }

        Console.WriteLine("Restarting the real VM a few times, this takes a minute...");

        try
        {
            // 1. A valid apply must leave the VM running.
            var current = Limits.ReadVmLimits();
            var ok = Limits.ApplyAndRestart(current, distro);
            Check("valid apply succeeds", ok.Ok, ok.Message);
            Check("valid apply did not roll back", !ok.RolledBack);
            Check("vm is running after valid apply", Wsl.ListRunning().Contains(distro));

            // 2. Force an unbootable VM through a key the rewriter must preserve
            //    (a custom kernel path that does not exist), then apply valid limits.
            //    The VM cannot start, so the rollback has to restore the file byte for byte.
            var lines = File.ReadAllLines(path).ToList();
            var idx = lines.FindIndex(l => l.Trim().Equals("[wsl2]", StringComparison.OrdinalIgnoreCase));
            if (idx < 0) { lines.Insert(0, "[wsl2]"); idx = 0; }
            // Doubled backslashes on purpose: .wslconfig treats "\x" as an escape sequence,
            // so a single backslash makes WSL discard the key instead of failing on it.
            lines.Insert(idx + 1, @"kernel=C:\\wsl-tray-selftest-no-such-kernel");
            File.WriteAllLines(path, lines);
            var beforeApply = File.ReadAllText(path);

            var bad = Limits.ApplyAndRestart(new Limits.VmLimits { Memory = "8GB" }, distro);
            Check("unbootable vm is reported as failure", !bad.Ok, bad.Message);
            Check("rollback was performed", bad.RolledBack, bad.Message);
            Check("config restored byte for byte", File.ReadAllText(path) == beforeApply);
        }
        finally
        {
            if (original != null) File.WriteAllText(path, original);
            else try { File.Delete(path); } catch { }

            Wsl.ShutdownVm();
            var err = Wsl.StartDistro(distro);
            Check("machine left in a working state", err.Length == 0 && Wsl.ListRunning().Contains(distro), err);
        }

        log.AppendLine();
        log.AppendLine(failed == 0 ? "VM INTEGRATION PASSED" : failed + " VM CHECK(S) FAILED");
        Console.WriteLine(log.ToString());
        return failed == 0 ? 0 : 1;
    }
}
