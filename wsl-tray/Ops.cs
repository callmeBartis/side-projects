namespace WslTray;

/// One long-running wsl.exe mutation at a time, for the whole process.
///
/// Start, Stop, Restart and Shell in the Overview, install, import, clone, export and
/// delete in Distros, and apply in Limits all mutate the same distros and the same files,
/// but each panel used to guard only its own buttons. That let a clone running in one tab
/// leave Start enabled in another - and Start boots the distro into the middle of its own
/// export, which is exactly the corruption the clone's confirmation dialog promises to
/// prevent. Two applies could likewise overlap and overwrite each other's .wslconfig backup.
///
/// Panels are rebuilt on every tab switch, so the gate cannot live in one of them: the
/// replacement panel has to be able to ask whether something is still running, and to be
/// told when it ends.
public static class Ops
{
    private static int _busy;

    public static bool Busy => Volatile.Read(ref _busy) != 0;

    /// Raised on both edges, on whichever thread caused it. Subscribers marshal themselves.
    public static event Action? Changed;

    public static bool TryEnter()
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0) return false;
        Changed?.Invoke();
        return true;
    }

    public static void Leave()
    {
        Volatile.Write(ref _busy, 0);
        Changed?.Invoke();
    }
}
