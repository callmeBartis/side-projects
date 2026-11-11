using System.Runtime.InteropServices;
using System.Text;

namespace WslTray;

/// .wslconfig caps the ONE shared utility VM that every distro runs inside.
/// WSL2 has no per-distro limits, so this is the only cap that exists.
public static class Limits
{
    /// Settable so the self-test can point it at a temp file instead of the real config.
    public static string WslConfigPath { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".wslconfig");

    public sealed class VmLimits
    {
        public string? Memory;      // "24GB", "8192MB", null = WSL default
        public string? Processors;  // "10"
        public string? Swap;        // "0", "8GB"

        /// Set when the file exists but could not be read. All three values are then
        /// unknown rather than unset, which is a different thing to show the user.
        public string? ReadError;
    }

    public static VmLimits ReadVmLimits()
    {
        var l = new VmLimits();
        if (!File.Exists(WslConfigPath)) return l;

        string[] all;
        // This runs from a nav-button click. An unreadable file (locked by a sync tool, or
        // deleted between the check above and here) would otherwise throw straight out of
        // the Click handler and take the whole tray app down.
        try { all = File.ReadAllLines(WslConfigPath); }
        catch (Exception ex) { l.ReadError = ex.Message; return l; }

        string section = "";
        foreach (var raw in all)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';')) continue;
            if (line.StartsWith('[')) { section = line.Trim('[', ']').Trim().ToLowerInvariant(); continue; }
            if (section != "wsl2") continue;

            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line.Substring(0, eq).Trim().ToLowerInvariant();
            var val = line.Substring(eq + 1).Trim();
            switch (key)
            {
                case "memory": l.Memory = val; break;
                case "processors": l.Processors = val; break;
                case "swap": l.Swap = val; break;
            }
        }
        return l;
    }

    /// Rewrites only the touched keys inside [wsl2]. Comments, key order and other
    /// sections ([experimental] and friends) survive untouched - a naive full rewrite
    /// would silently drop settings the user tuned by hand.
    public static void WriteVmLimits(VmLimits limits)
    {
        var pending = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["memory"] = limits.Memory,
            ["processors"] = limits.Processors,
            ["swap"] = limits.Swap,
        };

        var lines = File.Exists(WslConfigPath)
            ? new List<string>(File.ReadAllLines(WslConfigPath))
            : new List<string>();

        if (File.Exists(WslConfigPath))
            File.Copy(WslConfigPath, WslConfigPath + ".bak", overwrite: true);

        var output = new List<string>();
        var handled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string section = "";
        int wsl2SectionEnd = -1;

        foreach (var raw in lines)
        {
            var line = raw.Trim();

            if (line.StartsWith('['))
            {
                // Leaving [wsl2]: remember where to append keys that were not present.
                if (section == "wsl2") wsl2SectionEnd = output.Count;
                section = line.Trim('[', ']').Trim().ToLowerInvariant();
                output.Add(raw);
                continue;
            }

            if (section == "wsl2" && !line.StartsWith('#') && !line.StartsWith(';'))
            {
                var eq = line.IndexOf('=');
                if (eq > 0)
                {
                    var key = line.Substring(0, eq).Trim();
                    if (pending.TryGetValue(key, out var newVal))
                    {
                        // A second occurrence of a key we own is dropped rather than copied:
                        // WSL reads the last one, so leaving it would silently override the
                        // value just written and hand the user back the old number.
                        if (!handled.Add(key)) continue;

                        // null means "remove this cap and let WSL use its default"
                        if (newVal != null) output.Add(key + "=" + newVal);
                        continue;
                    }
                }
            }
            output.Add(raw);
        }

        if (section == "wsl2") wsl2SectionEnd = output.Count;

        var missing = pending.Where(kv => kv.Value != null && !handled.Contains(kv.Key))
                             .Select(kv => kv.Key + "=" + kv.Value)
                             .ToList();
        if (missing.Count > 0)
        {
            if (wsl2SectionEnd < 0)
            {
                if (output.Count > 0) output.Add("");
                output.Add("[wsl2]");
                output.AddRange(missing);
            }
            else
            {
                output.InsertRange(wsl2SectionEnd, missing);
            }
        }

        File.WriteAllLines(WslConfigPath, output, new UTF8Encoding(false));
    }

    // ---- validation -----------------------------------------------------------------
    //
    // A bad value here does not fail loudly: WSL simply refuses to create the VM with
    // Wsl/Service/CreateInstance/CreateVm/E_UNEXPECTED and every distro stays dead.
    // "memory=10" (no unit) is read as 10 bytes and does exactly that.

    /// Returns an error message, or "" when the value set is safe to write.
    /// Puts the file back the way it was. The .bak written by WriteVmLimits is the fallback
    /// if even this fails, which is why the failure is not fatal here.
    private static void Restore(string? previous)
    {
        try
        {
            if (previous == null) File.Delete(WslConfigPath);
            else File.WriteAllText(WslConfigPath, previous);
        }
        catch { /* .bak still holds it */ }
    }

    public static string Validate(VmLimits l)
    {
        if (l.Memory != null)
        {
            var bytes = ParseSizeValue(l.Memory);
            if (bytes == null) return "Memory must look like '16GB' or '512MB' - a bare number is read as bytes and the VM will not start.";
            if (bytes < 512L * 1024 * 1024) return "Memory below 512MB will not boot a usable VM.";
            var host = HostPhysicalMemoryBytes();
            if (host > 0 && bytes > host)
                return $"Memory ({Fmt.Bytes(bytes.Value)}) exceeds the physical RAM in this machine ({Fmt.Bytes(host)}).";
        }

        if (l.Swap != null)
        {
            var bytes = ParseSizeValue(l.Swap);
            if (bytes == null) return "Swap must look like '8GB', '512MB' or plain '0'.";

            // Swap is a file on disk, so it may exceed RAM - but not by an amount that fills
            // the system drive. Four times physical RAM is generous and still catches a unit
            // slip that would otherwise ask WSL for a terabyte.
            var host = HostPhysicalMemoryBytes();
            if (host > 0 && bytes > host * 4)
                return $"Swap ({Fmt.Bytes(bytes.Value)}) is more than four times the physical RAM " +
                       $"({Fmt.Bytes(host)}) and would fill the drive.";
        }

        if (l.Processors != null)
        {
            if (!int.TryParse(l.Processors, out var p)) return "Processors must be a whole number.";
            if (p < 1) return "Processors must be at least 1.";
            if (p > Environment.ProcessorCount)
                return $"Processors ({p}) exceeds the {Environment.ProcessorCount} logical cores in this machine.";
        }

        return "";
    }

    /// Accepts "0", "8GB", "512MB", "1024KB". A bare non-zero number is rejected on
    /// purpose: WSL would read it as bytes, which is never what anyone means.
    public static long? ParseSizeValue(string raw)
    {
        var s = raw.Trim();
        if (s.Length == 0) return null;
        if (s == "0") return 0;

        int i = 0;
        while (i < s.Length && char.IsDigit(s[i])) i++;
        if (i == 0) return null;
        if (!long.TryParse(s.Substring(0, i), out var num)) return null;

        var unit = s.Substring(i).Trim().ToUpperInvariant();
        return unit switch
        {
            "KB" => num * 1024,
            "MB" => num * 1024 * 1024,
            "GB" => num * 1024 * 1024 * 1024,
            _ => null,   // includes the empty unit
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);

    public static long HostPhysicalMemoryBytes()
    {
        try
        {
            var m = new MemoryStatusEx();
            m.dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>();
            return GlobalMemoryStatusEx(ref m) ? (long)m.ullTotalPhys : 0;
        }
        catch { return 0; }
    }

    // ---- apply with rollback --------------------------------------------------------

    public sealed record ApplyResult(bool Ok, string Message, bool RolledBack);

    /// Writes the limits, restarts the VM and proves it comes back up. If the VM refuses
    /// to start, the previous .wslconfig is restored automatically - an unbootable VM is
    /// not something to leave a user sitting in.
    public static ApplyResult ApplyAndRestart(VmLimits limits, string? distroToStart)
    {
        var error = Validate(limits);
        if (!error.Equals("")) return new ApplyResult(false, error, false);

        string? previous = File.Exists(WslConfigPath) ? File.ReadAllText(WslConfigPath) : null;

        try { WriteVmLimits(limits); }
        catch (Exception ex) { return new ApplyResult(false, "Could not write .wslconfig: " + ex.Message, false); }

        var shutdownError = Wsl.ShutdownVm();
        if (shutdownError.Length > 0)
        {
            // The new file is already written and nothing has booted with it. Leaving it
            // there would apply never-verified values at the next reboot, while the user
            // was just told the operation failed.
            Restore(previous);
            return new ApplyResult(false,
                "wsl --shutdown failed, so the previous config was put back: " + shutdownError, true);
        }

        var distro = distroToStart ?? Wsl.ListDistros().FirstOrDefault();
        if (distro == null)
            return new ApplyResult(true, "Saved. No distro installed to verify against.", false);

        var startError = Wsl.StartDistro(distro);
        if (startError.Length == 0)
            return new ApplyResult(true, "Applied. VM restarted and is running.", false);

        // The VM will not boot with these values: put the old file back and prove that works.
        if (previous == null)
        {
            try { File.Delete(WslConfigPath); } catch { }
        }
        else
        {
            try { File.WriteAllText(WslConfigPath, previous); }
            catch (Exception ex)
            {
                return new ApplyResult(false,
                    "VM did not start AND the old config could not be restored: " + ex.Message +
                    "\nA copy is in " + WslConfigPath + ".bak", false);
            }
        }

        Wsl.ShutdownVm();
        var recovered = Wsl.StartDistro(distro);
        var detail = Shorten(startError);

        return new ApplyResult(false,
            recovered.Length == 0
                ? "VM refused to start with those values, so the previous config was restored and the VM is running again.\n\n" + detail
                : "VM refused to start and did not recover after rolling back.\n\n" + detail,
            true);
    }

    private static string Shorten(string s)
    {
        var t = s.Replace("\0", "").Trim();
        return t.Length <= 300 ? t : t.Substring(0, 300) + "...";
    }

    /// The distro's ext4.vhdx on the Windows side - the number that actually eats your SSD.
    /// WSL never shrinks it on its own, so it usually exceeds the used space inside.
    public static long VhdxSizeBytes(string distro)
    {
        try
        {
            using var lxss = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Lxss");
            if (lxss == null) return 0;

            foreach (var sub in lxss.GetSubKeyNames())
            {
                using var key = lxss.OpenSubKey(sub);
                if (key?.GetValue("DistributionName") as string != distro) continue;

                var basePath = key.GetValue("BasePath") as string;
                if (string.IsNullOrEmpty(basePath)) return 0;
                if (basePath.StartsWith(@"\\?\")) basePath = basePath.Substring(4);

                var vhdx = Path.Combine(basePath, "ext4.vhdx");
                return File.Exists(vhdx) ? new FileInfo(vhdx).Length : 0;
            }
        }
        catch { /* registry layout differs on Store-installed distros; size is optional info */ }
        return 0;
    }
}
