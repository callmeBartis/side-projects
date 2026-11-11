using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace WslTray;

/// One container's live usage, as reported by "docker stats".
/// Distro matters because each WSL distro runs its own independent dockerd - two distros
/// can each have a container called "postgres" and they are not the same container.
public sealed record ContainerStat(string Name, double CpuPercent, long MemBytes, long MemLimitBytes)
{
    public string Distro { get; init; } = "";
}

/// A raw, unprocessed reading from one distro. Rates (CPU%) need two of these.
public sealed class DistroReading
{
    public string Distro = "";
    public DateTime Taken;

    // VM-wide, identical across distros because they share one kernel.
    public long CpuTotalJiffies;
    public long CpuIdleJiffies;
    public long MemTotalKb;
    public long MemAvailableKb;
    public long SwapTotalKb;
    public long SwapFreeKb;
    public long DiskTotalKb;
    public long DiskUsedKb;
    public int Cpus;

    // Scoped to this distro's PID namespace.
    public long ProcRssKb;
    public long ProcJiffies;

    public List<ContainerStat> Containers = new();
    public bool DockerPresent;
}

public static class Wsl
{
    // Collected in one shot per distro: every extra wsl.exe call costs ~150ms of process startup.
    private const string SampleScript = """
        echo @stat; head -1 /proc/stat
        echo @cpus; nproc
        echo @mem; grep -E '^(MemTotal|MemAvailable|SwapTotal|SwapFree):' /proc/meminfo
        echo @df; df -Pk / | tail -1
        echo @jiffies; cat /proc/[0-9]*/stat 2>/dev/null | awk '{ sub(/^[0-9]+ \(.*\) /, ""); s += $12 + $13 } END { print s+0 }'
        echo @rss; cat /proc/[0-9]*/statm 2>/dev/null | awk '{ r += $2 } END { print r*4+0 }'
        """;
    // The two /proc globs go through cat on purpose. Handing the glob to awk directly makes
    // awk exit 2 as soon as one process dies between the shell expanding the glob and awk
    // opening the file - and since that awk is the script's last command, its exit code
    // becomes the script's, so Sample() throws the whole reading away. Under a fork storm
    // in the distro that hit 236 of 300 runs, which is exactly when someone opens a monitor.
    // cat reports the missing file on stderr and keeps going; awk only ever reads stdin.

    private const string DockerScript = """
        command -v docker >/dev/null 2>&1 || { echo @nodocker; exit 0; }
        docker stats --no-stream --format '{{.Name}}|{{.CPUPerc}}|{{.MemUsage}}' 2>/dev/null || echo @nodocker
        """;

    /// Runs a program and captures stdout. WSL_UTF8 matters: without it wsl.exe emits UTF-16LE
    /// and every parser downstream sees NUL-separated garbage.
    public static string Run(string exe, string[] args, int timeoutMs, out int exitCode)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["WSL_UTF8"] = "1";

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("could not start " + exe);
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
            exitCode = -1;
            return "";
        }
        // The bool matters. WaitForExit(int) returns as soon as the process is gone, but a
        // grandchild that inherited the pipe can hold it open, and .Result would then block
        // with no timeout at all - on the polling thread, forever, with nothing on screen.
        var drained = Task.WaitAll(new Task[] { stdout, stderr }, 2000);
        exitCode = p.ExitCode;
        if (!drained) { exitCode = -1; return ""; }

        // wsl.exe reports fatal errors on stdout, but not always - on failure the caller
        // needs whatever text exists, so both streams are returned then.
        if (exitCode != 0 && stderr.Result.Trim().Length > 0)
            return (stdout.Result + "\n" + stderr.Result).Trim();

        return stdout.Result;
    }

    /// Shell scripts travel as base64 so no layer of quoting (Windows arg parsing, then sh) can mangle them.
    public static string RunScript(string distro, string script, int timeoutMs, out int exitCode)
    {
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(script.Replace("\r\n", "\n")));
        return Run("wsl.exe",
            new[] { "-d", distro, "-u", "root", "--exec", "/bin/sh", "-c", "echo " + b64 + " | base64 -d | /bin/sh" },
            timeoutMs, out exitCode);
    }

    /// "wsl -l -q" instead of "-l -v": no localized table header to parse, just names.
    public static List<string> ListDistros()
    {
        var outp = Run("wsl.exe", new[] { "-l", "-q" }, 10000, out var code);
        return code != 0 ? new List<string>() : ParseDistroList(outp);
    }

    public static HashSet<string> ListRunning()
    {
        var outp = Run("wsl.exe", new[] { "-l", "-q", "--running" }, 10000, out var code);
        return code != 0
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(ParseDistroList(outp), StringComparer.OrdinalIgnoreCase);
    }

    public static List<string> ParseDistroList(string raw) =>
        raw.Replace("\0", "")
           .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
           .Select(l => l.Trim())
           .Where(l => l.Length > 0)
           .ToList();

    /// Stops one distro. The shared VM keeps running as long as another distro does.
    public static string Terminate(string distro)
    {
        var outp = Run("wsl.exe", new[] { "--terminate", distro }, 60000, out var code);
        return code == 0 ? "" : Fail(outp, code);
    }

    /// Starting a distro means running any command in it - there is no "wsl --start".
    /// The probe is /bin/sh, not /bin/true: a rootfs without /bin/true (NixOS, some minimal
    /// images) would boot fine and still report a non-zero exit, which the limits apply path
    /// reads as "the VM refuses these values" and rolls a perfectly good config back.
    public static string StartDistro(string distro)
    {
        var outp = Run("wsl.exe", new[] { "-d", distro, "--exec", "/bin/sh", "-c", "exit 0" },
            120000, out var code);
        return code == 0 ? "" : Fail(outp, code);
    }

    /// Installing downloads hundreds of MB and exporting copies a whole filesystem, so
    /// these get minutes instead of the seconds a status query needs.
    private const int LongMs = 45 * 60 * 1000;

    /// Installs from the online catalog. --no-launch keeps the first-run account setup
    /// out of a window we do not control; the user finishes it in the shell afterwards.
    public static string Install(string catalogName, string? newName, string? location)
    {
        var args = new List<string> { "--install", catalogName, "--no-launch" };
        if (!string.IsNullOrWhiteSpace(newName)) { args.Add("--name"); args.Add(newName); }
        if (!string.IsNullOrWhiteSpace(location)) { args.Add("--location"); args.Add(location); }

        var outp = Run("wsl.exe", args.ToArray(), LongMs, out var code);
        return code == 0 ? "" : Fail(outp, code);
    }

    public static string Import(string name, string location, string file)
    {
        var args = new List<string> { "--import", name, location, file };
        // A VHDX is copied in place rather than unpacked, and wsl.exe will not guess.
        if (file.EndsWith(".vhdx", StringComparison.OrdinalIgnoreCase) ||
            file.EndsWith(".vhd", StringComparison.OrdinalIgnoreCase))
            args.Add("--vhd");

        var outp = Run("wsl.exe", args.ToArray(), LongMs, out var code);
        return code == 0 ? "" : Fail(outp, code);
    }

    public static string Export(string distro, string file)
    {
        var outp = Run("wsl.exe", new[] { "--export", distro, file, "--format", FormatFor(file) },
            LongMs, out var code);
        return code == 0 ? "" : Fail(outp, code);
    }

    /// wsl.exe does not infer the format from the extension, so a .tar.gz written as
    /// plain tar would carry a lying name.
    public static string FormatFor(string file)
    {
        var f = file.ToLowerInvariant();
        if (f.EndsWith(".tar.gz") || f.EndsWith(".tgz")) return "tar.gz";
        if (f.EndsWith(".tar.xz")) return "tar.xz";
        if (f.EndsWith(".vhdx") || f.EndsWith(".vhd")) return "vhd";
        return "tar";
    }

    /// Deletes the distro and its whole disk. There is no undo and no recycle bin.
    public static string Unregister(string distro)
    {
        var outp = Run("wsl.exe", new[] { "--unregister", distro }, LongMs, out var code);
        return code == 0 ? "" : Fail(outp, code);
    }

    /// Opens an interactive shell. wsl.exe has no terminal of its own - it inherits the
    /// console of whoever started it, and this process is a WinExe with none. So a
    /// terminal has to be started around it: Windows Terminal if present, otherwise
    /// Windows gives the child its own console because the parent has none.
    public static string OpenShell(string distro)
    {
        // --cd ~ lands in the Linux home. Without it the shell inherits this app's
        // working directory and starts somewhere under /mnt/c.
        var wt = Terminal("wt.exe", "-w", "0", "nt", "--title", distro, "wsl.exe", "-d", distro, "--cd", "~");
        if (wt == null) return "";

        // No Windows Terminal: wsl.exe gets a console of its own, because this process
        // has none to inherit.
        var direct = Terminal("wsl.exe", "-d", distro, "--cd", "~");
        return direct == null ? "" : direct.Message;
    }

    /// UseShellExecute is deliberately false: ShellExecuteEx refuses both wt.exe and
    /// wsl.exe from this process with "access denied", while plain CreateProcess - the
    /// same call every other command here uses - works.
    private static Exception? Terminal(string exe, params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                // Nothing is redirected and no window is suppressed, so the child gets
                // its own console window. Redirecting anything would take that away.
                CreateNoWindow = false,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.Environment["WSL_UTF8"] = "1";

            using var p = Process.Start(psi);
            if (p == null) return new InvalidOperationException("could not start " + exe);

            // Started is not the same as succeeded. wt.exe hands the tab to an existing
            // window and exits immediately, so an exit code is available and a non-zero one
            // means no terminal appeared - the caller still has a fallback to try. A process
            // still alive after the wait is the terminal itself.
            if (p.WaitForExit(600) && p.ExitCode != 0)
                return new InvalidOperationException(exe + " exited with code " + p.ExitCode);

            return null;
        }
        catch (Exception ex) { return ex; }
    }

    /// One installable entry from the online catalog.
    public sealed record OnlineDistro(string Name, string Friendly)
    {
        public override string ToString() =>
            Friendly.Length > 0 && !Friendly.Equals(Name, StringComparison.OrdinalIgnoreCase)
                ? $"{Friendly}  ({Name})"
                : Name;
    }

    public static List<OnlineDistro> ListOnline()
    {
        var outp = Run("wsl.exe", new[] { "-l", "-o" }, 60000, out var code);
        return code != 0 ? new List<OnlineDistro>() : ParseOnlineList(outp);
    }

    /// "wsl -l -o" ignores --quiet and always prints a localized banner plus a table
    /// header, so neither can be matched by text. What holds in every language is the
    /// shape: one blank line separates the banner from the table.
    public static List<OnlineDistro> ParseOnlineList(string raw)
    {
        var lines = raw.Replace("\0", "")
                       .Split('\n')
                       .Select(l => l.Trim())
                       .ToList();

        int start = 0;
        for (int i = 0; i + 1 < lines.Count; i++)
            if (lines[i].Length == 0 && lines[i + 1].Length > 0) { start = i + 1; break; }

        if (start < lines.Count && lines[start].StartsWith("NAME", StringComparison.OrdinalIgnoreCase))
            start++;

        var result = new List<OnlineDistro>();
        for (int i = start; i < lines.Count; i++)
        {
            var l = lines[i];
            // Prose, not a row: catalog entries never end a sentence or quote a command.
            if (l.Length == 0 || l.EndsWith('.') || l.EndsWith(':') || l.Contains('"')) continue;

            var sp = l.IndexOf(' ');
            var name = sp < 0 ? l : l.Substring(0, sp);
            if (!IsDistroName(name)) continue;

            result.Add(new OnlineDistro(name, sp < 0 ? "" : l.Substring(sp).Trim()));
        }
        return result;
    }

    public static bool IsDistroName(string s) =>
        s.Length is > 0 and < 64 &&
        char.IsLetterOrDigit(s[0]) &&
        s.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.');

    /// Shuts down the whole VM and every distro in it.
    public static string ShutdownVm()
    {
        var outp = Run("wsl.exe", new[] { "--shutdown" }, 60000, out var code);
        return code == 0 ? "" : Fail(outp, code);
    }

    private static string Fail(string outp, int code)
    {
        var text = outp.Replace("\0", "").Trim();
        return text.Length > 0 ? text : "exit code " + code;
    }

    public static DistroReading? Sample(string distro, bool withDocker)
    {
        var script = withDocker ? SampleScript + "\necho @docker\n" + DockerScript : SampleScript;
        var outp = RunScript(distro, script, 20000, out var code);
        if (code != 0 || outp.Length == 0) return null;
        var r = ParseSample(outp);
        r.Distro = distro;
        r.Taken = DateTime.UtcNow;
        return r;
    }

    public static DistroReading ParseSample(string raw)
    {
        var r = new DistroReading { Taken = DateTime.UtcNow };
        string section = "";
        foreach (var line in raw.Replace("\0", "").Split('\n'))
        {
            var l = line.Trim();
            if (l.Length == 0) continue;
            if (l.StartsWith('@')) { section = l; continue; }

            switch (section)
            {
                case "@stat":
                    // cpu user nice system idle iowait irq softirq steal guest guest_nice
                    var f = l.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (f.Length >= 5 && f[0] == "cpu")
                    {
                        long total = 0;
                        for (int i = 1; i < f.Length; i++)
                            if (long.TryParse(f[i], out var v)) total += v;
                        r.CpuTotalJiffies = total;
                        // idle + iowait: both are time the VM was not doing our work
                        r.CpuIdleJiffies = Num(f, 4) + Num(f, 5);
                    }
                    break;

                case "@cpus":
                    if (int.TryParse(l, out var n)) r.Cpus = n;
                    break;

                case "@mem":
                    var parts = l.Split(new[] { ':', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && long.TryParse(parts[1], out var kb))
                    {
                        switch (parts[0])
                        {
                            case "MemTotal": r.MemTotalKb = kb; break;
                            case "MemAvailable": r.MemAvailableKb = kb; break;
                            case "SwapTotal": r.SwapTotalKb = kb; break;
                            case "SwapFree": r.SwapFreeKb = kb; break;
                        }
                    }
                    break;

                case "@df":
                    var d = l.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (d.Length >= 4) { r.DiskTotalKb = Num(d, 1); r.DiskUsedKb = Num(d, 2); }
                    break;

                case "@jiffies":
                    if (long.TryParse(l, out var j)) r.ProcJiffies = j;
                    break;

                case "@rss":
                    if (long.TryParse(l, out var rss)) r.ProcRssKb = rss;
                    break;

                case "@docker":
                    if (l == "@nodocker") break;
                    var c = ParseDockerStatLine(l);
                    if (c != null) { r.Containers.Add(c); r.DockerPresent = true; }
                    break;
            }
        }
        return r;
    }

    private static long Num(string[] a, int i) => i < a.Length && long.TryParse(a[i], out var v) ? v : 0;

    /// Format: "name|12.34%|1.234GiB / 23.47GiB"
    public static ContainerStat? ParseDockerStatLine(string line)
    {
        var p = line.Split('|');
        if (p.Length < 3) return null;
        var mem = p[2].Split('/', StringSplitOptions.TrimEntries);
        return new ContainerStat(
            p[0].Trim(),
            ParsePercent(p[1]),
            mem.Length > 0 ? ParseSize(mem[0]) : 0,
            mem.Length > 1 ? ParseSize(mem[1]) : 0);
    }

    public static double ParsePercent(string s)
    {
        s = s.Trim().TrimEnd('%');
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    /// docker reports "1.234GiB", "289MiB", "20.01MiB", occasionally plain "0B".
    public static long ParseSize(string s)
    {
        s = s.Trim();
        int i = 0;
        while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.' || s[i] == ',')) i++;
        if (i == 0) return 0;
        if (!double.TryParse(s.Substring(0, i).Replace(',', '.'), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var num)) return 0;

        var unit = s.Substring(i).Trim().ToLowerInvariant();
        double mult = unit switch
        {
            "b" or "" => 1,
            "kb" or "kib" => 1024d,
            "mb" or "mib" => 1024d * 1024,
            "gb" or "gib" => 1024d * 1024 * 1024,
            "tb" or "tib" => 1024d * 1024 * 1024 * 1024,
            _ => 1,
        };
        return (long)(num * mult);
    }
}
