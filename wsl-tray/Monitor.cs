using System.Collections.Concurrent;
using System.Diagnostics;

namespace WslTray;

public static class Fmt
{
    public static string Bytes(long b)
    {
        if (b <= 0) return "0 B";
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        double v = b;
        int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return v.ToString(v < 10 && i > 0 ? "0.0" : "0") + " " + u[i];
    }

    public static string Kb(long kb) => Bytes(kb * 1024);
}

public sealed class DistroView
{
    public string Name = "";
    public bool Running;
    public double CpuPercent;      // share of the whole VM's CPU capacity
    public long RssKb;             // sum of this distro's processes, approximate
    public long VhdxBytes;
}

public sealed class Snapshot
{
    public DateTime Taken = DateTime.Now;
    public bool VmRunning;

    public double VmCpuPercent;
    public long MemTotalKb, MemUsedKb;
    public long SwapTotalKb, SwapUsedKb;
    public long DiskTotalKb, DiskUsedKb;
    public int Cpus;
    public long HostVmmemBytes;    // what Windows sees WSL holding

    public List<DistroView> Distros = new();
    public List<ContainerStat> Containers = new();

    public double MemPercent => MemTotalKb > 0 ? MemUsedKb * 100.0 / MemTotalKb : 0;
    public double SwapPercent => SwapTotalKb > 0 ? SwapUsedKb * 100.0 / SwapTotalKb : 0;
    public double DiskPercent => DiskTotalKb > 0 ? DiskUsedKb * 100.0 / DiskTotalKb : 0;
}

public sealed class Monitor : IDisposable
{
    private const int Hz = 100; // CLK_TCK, verified on the WSL kernel

    private readonly Config _cfg;
    private CancellationTokenSource? _cts;
    // Concurrent because Invalidate() runs on the UI thread (and on a task thread after
    // "Shut down WSL VM") while Poll() is walking the same dictionary on the loop thread.
    private readonly ConcurrentDictionary<string, DistroReading> _previous = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastDockerPoll = DateTime.MinValue;
    private List<ContainerStat> _lastContainers = new();

    public Snapshot Latest { get; private set; } = new();
    public event Action<Snapshot>? Updated;
    public event Action<string>? Failed;

    public Monitor(Config cfg) => _cfg = cfg;

    public void Start()
    {
        Stop();
        var cts = new CancellationTokenSource();
        _cts = cts;
        // The token is captured in a local. Reading the field inside the lambda races with
        // Stop() setting it to null, and that NullReferenceException would land in a task
        // nobody observes: the loop would simply never start and nothing would say so.
        _ = Task.Run(async () => { using (cts) await LoopAsync(cts.Token); });
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts = null;
    }

    public void Dispose() => Stop();

    /// Forces the next poll to re-read docker and drop stale rate baselines.
    /// Used after the user starts or stops a distro so the UI reacts at once.
    public void Invalidate(string? distro = null)
    {
        if (distro == null) _previous.Clear();
        else _previous.TryRemove(distro, out _);
        _lastDockerPoll = DateTime.MinValue;
        _lastContainers = new List<ContainerStat>();
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var snap = Poll();

                // A restart can happen while a poll is out (it takes seconds). The newer
                // loop owns the state now, so this snapshot is already stale and publishing
                // it would show older numbers than the ones already on screen.
                if (ct.IsCancellationRequested) return;

                Latest = snap;
                Updated?.Invoke(snap);
            }
            catch (Exception ex)
            {
                Failed?.Invoke(ex.Message);
            }

            try { await Task.Delay(TimeSpan.FromSeconds(_cfg.PollSeconds), ct); }
            catch (TaskCanceledException) { return; }
        }
    }

    /// Last poll's numbers for one distro. A skipped or failed sample means "no measurement",
    /// and drawing that as a measured zero is how a plainly running distro ends up reading
    /// "0.0 %  0 B" - the very symptom the /proc fix removed, back through another door.
    private static void CarryOver(Snapshot last, DistroView view)
    {
        var prev = last.Distros.FirstOrDefault(d =>
            string.Equals(d.Name, view.Name, StringComparison.OrdinalIgnoreCase));
        if (prev == null) return;
        view.CpuPercent = prev.CpuPercent;
        view.RssKb = prev.RssKb;
    }

    public Snapshot Poll()
    {
        var snap = new Snapshot();
        var last = Latest;
        var all = Wsl.ListDistros();

        // Critical: only sample RUNNING distros. Any "wsl -d <name> ..." against a
        // stopped distro boots it, so polling all of them would start every distro
        // on the machine and defeat the whole point of this tool.
        var running = Wsl.ListRunning();
        snap.VmRunning = running.Count > 0;

        bool pollDocker = (DateTime.UtcNow - _lastDockerPoll).TotalSeconds >= _cfg.DockerPollSeconds;
        bool vmFilled = false;
        // Separate from vmFilled: the totals come from the first running distro, but CPU
        // needs two readings. If that distro was just started or invalidated it has no
        // baseline, and the figure has to come from the next distro that does have one.
        bool cpuFilled = false;

        // Every distro runs its own dockerd, so containers are collected from all of them.
        // Assigning instead of accumulating would show only the last distro polled.
        var freshContainers = pollDocker ? new List<ContainerStat>() : null;
        bool sampleFailed = false;

        foreach (var name in all)
        {
            var view = new DistroView { Name = name, Running = running.Contains(name) };
            view.VhdxBytes = Limits.VhdxSizeBytes(name);

            if (!view.Running) { _previous.TryRemove(name, out _); snap.Distros.Add(view); continue; }

            // "wsl -d <name>" starts a stopped distro, and the running set above is a
            // snapshot: a stop or a clone that finished since then would be undone by this
            // poll. While any mutation is in flight the numbers simply stand still.
            if (Ops.Busy) { CarryOver(last, view); snap.Distros.Add(view); continue; }

            var reading = Wsl.Sample(name, pollDocker);
            if (reading == null) { sampleFailed = true; CarryOver(last, view); snap.Distros.Add(view); continue; }

            if (_previous.TryGetValue(name, out var prev))
            {
                var elapsed = (reading.Taken - prev.Taken).TotalSeconds;
                if (elapsed > 0.2)
                {
                    // VM-wide CPU from /proc/stat: identical in every distro (shared kernel),
                    // so the first running distro provides it.
                    if (!cpuFilled)
                    {
                        var totalDelta = reading.CpuTotalJiffies - prev.CpuTotalJiffies;
                        var idleDelta = reading.CpuIdleJiffies - prev.CpuIdleJiffies;
                        if (totalDelta > 0)
                        {
                            snap.VmCpuPercent = Clamp((totalDelta - idleDelta) * 100.0 / totalDelta);
                            cpuFilled = true;
                        }
                    }

                    var procDelta = reading.ProcJiffies - prev.ProcJiffies;
                    var capacity = elapsed * Hz * Math.Max(reading.Cpus, 1);
                    if (capacity > 0 && procDelta >= 0)
                        view.CpuPercent = Clamp(procDelta * 100.0 / capacity);
                }
            }

            if (!vmFilled)
            {
                snap.MemTotalKb = reading.MemTotalKb;
                snap.MemUsedKb = Math.Max(0, reading.MemTotalKb - reading.MemAvailableKb);
                snap.SwapTotalKb = reading.SwapTotalKb;
                snap.SwapUsedKb = Math.Max(0, reading.SwapTotalKb - reading.SwapFreeKb);
                snap.DiskTotalKb = reading.DiskTotalKb;
                snap.DiskUsedKb = reading.DiskUsedKb;
                snap.Cpus = reading.Cpus;
                vmFilled = true;
            }

            view.RssKb = reading.ProcRssKb;
            if (pollDocker && reading.DockerPresent)
                freshContainers!.AddRange(reading.Containers.Select(c => c with { Distro = name }));

            _previous[name] = reading;
            snap.Distros.Add(view);
        }

        // Nothing was sampled this time round, so the VM-wide figures are unknown rather than
        // zero. Left at zero the card would claim a running VM with no memory and no disk,
        // right next to the Windows-side figure saying it holds gigabytes.
        if (!vmFilled && snap.VmRunning && last.MemTotalKb > 0)
        {
            snap.MemTotalKb = last.MemTotalKb;
            snap.MemUsedKb = last.MemUsedKb;
            snap.SwapTotalKb = last.SwapTotalKb;
            snap.SwapUsedKb = last.SwapUsedKb;
            snap.DiskTotalKb = last.DiskTotalKb;
            snap.DiskUsedKb = last.DiskUsedKb;
            snap.Cpus = last.Cpus;
        }
        if (!cpuFilled && snap.VmRunning) snap.VmCpuPercent = last.VmCpuPercent;

        // A failed sample means "no answer", not "no containers". Publishing the empty list
        // would blank the panel for a whole docker interval, so the previous one stands and
        // the clock is not advanced either, which makes the next tick retry.
        if (pollDocker && !sampleFailed)
        {
            _lastContainers = freshContainers!;
            _lastDockerPoll = DateTime.UtcNow;
        }

        snap.Containers = snap.VmRunning ? _lastContainers : new List<ContainerStat>();
        // A stopped VM has no vmmem process, and looking for one costs a full snapshot of
        // every process on the host to be told what we already know.
        snap.HostVmmemBytes = snap.VmRunning ? HostVmmemBytes() : 0;
        return snap;
    }

    private static double Clamp(double v) => v < 0 ? 0 : v > 100 ? 100 : v;

    /// Windows' own view of WSL memory. Usually higher than /proc/meminfo says is in use,
    /// because WSL hands pages back to Windows lazily.
    public static long HostVmmemBytes()
    {
        long total = 0;
        foreach (var pname in new[] { "vmmemWSL", "vmmem" })
        {
            foreach (var p in Process.GetProcessesByName(pname))
            {
                using (p) total += p.WorkingSet64;
            }
            if (total > 0) break; // vmmemWSL is the WSL one; only fall back if absent
        }
        return total;
    }
}
