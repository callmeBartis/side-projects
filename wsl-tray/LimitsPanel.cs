namespace WslTray;

/// VM limits only. WSL2 has no per-distro limits: every distro shares one utility VM,
/// so .wslconfig is the only cap that exists.
///
/// The size fields are a number plus a unit toggle rather than free text, because a
/// bare "10" in .wslconfig means ten BYTES and leaves the VM unable to boot at all.
public sealed class LimitsPanel : Panel
{
    private readonly Func<Snapshot> _snapshot;
    private readonly Action _onVmRestarted;

    private readonly Check _memOn = UiHelp.Check("limit memory");
    private readonly NumBox _memValue = UiHelp.Num(16, 1, 1024);
    private readonly UnitToggle _memUnit = new("GB", "MB");

    private readonly Check _procOn = UiHelp.Check("limit processors");
    private readonly NumBox _procValue = UiHelp.Num(Environment.ProcessorCount, 1, Environment.ProcessorCount);

    private readonly Check _swapOn = UiHelp.Check("set swap");
    private readonly NumBox _swapValue = UiHelp.Num(0, 0, 1024);
    private readonly UnitToggle _swapUnit = new("GB", "MB");

    private readonly Label _status = UiHelp.Lbl("", Theme.Body, Theme.Muted);
    private readonly FlatBtn _apply = new("Apply and restart VM", primary: true) { Width = 178 };
    private readonly FlatBtn _saveOnly = new("Save without restart") { Width = 168 };

    public LimitsPanel(int width, Func<Snapshot> snapshot, Action onVmRestarted)
    {
        _snapshot = snapshot;
        _onVmRestarted = onVmRestarted;
        BackColor = Theme.Bg;

        var head = UiHelp.Lbl("VM limits", Theme.Title);
        head.Location = new Point(0, 0);
        Controls.Add(head);

        var host = Limits.HostPhysicalMemoryBytes();
        var note = UiHelp.Lbl(
            "Applies to the single utility VM that ALL distros share - WSL2 has no per-distro limits.\n" +
            $"This machine has {Environment.ProcessorCount} logical cores" +
            (host > 0 ? $" and {Fmt.Bytes(host)} of RAM." : "."),
            Theme.Small, Theme.Muted);
        note.Location = new Point(0, 26);
        note.MaximumSize = new Size(width, 0);
        Controls.Add(note);

        var card = UiHelp.Card();
        card.Location = new Point(0, 70);
        card.Size = new Size(width, 224);

        Row(card, _memOn, 20, _memValue, _memUnit, "unchecked = WSL default (about half your RAM)");
        Row(card, _procOn, 58, _procValue, null, "unchecked = all cores");
        Row(card, _swapOn, 96, _swapValue, _swapUnit, "0 disables swap entirely");

        _apply.Location = new Point(18, 144);
        _apply.Click += (s, e) => Apply(restart: true);
        card.Controls.Add(_apply);

        _saveOnly.Location = new Point(206, 144);
        _saveOnly.Click += (s, e) => Apply(restart: false);
        card.Controls.Add(_saveOnly);

        _status.Location = new Point(18, 186);
        _status.MaximumSize = new Size(width - 36, 0);
        card.Controls.Add(_status);

        Controls.Add(card);

        var warnHead = UiHelp.Lbl("What happens when you apply", Theme.BodyBold);
        warnHead.Location = new Point(0, 314);
        Controls.Add(warnHead);

        var safety = UiHelp.Lbl(
            "The VM is shut down and started again, so every distro stops and containers with a\n" +
            "restart policy come back afterwards.\n\n" +
            "A value without a unit would make the VM unbootable, so the unit is a toggle and\n" +
            "every value is checked before writing. If the VM still refuses to boot, the previous\n" +
            ".wslconfig is restored automatically and the VM is brought back up.",
            Theme.Small, Theme.Muted);
        safety.Location = new Point(0, 340);
        safety.MaximumSize = new Size(width, 0);
        Controls.Add(safety);

        foreach (var (box, controls) in new (Check, Control[])[]
                 {
                     (_memOn, new Control[] { _memValue, _memUnit }),
                     (_procOn, new Control[] { _procValue }),
                     (_swapOn, new Control[] { _swapValue, _swapUnit }),
                 })
        {
            var cb = box;
            var targets = controls;
            cb.CheckedChanged += (s, e) => { foreach (var c in targets) c.Enabled = cb.Checked; };
        }

        // FlatBtn's own handler flips the unit first, so this sees the new one.
        _memUnit.Click += (s, e) => SwitchUnit(_memValue, _memUnit, 1);
        _swapUnit.Click += (s, e) => SwitchUnit(_swapValue, _swapUnit, 0);

        Ops.Changed += OnOpsChanged;

        Load_();
    }

    private static void Row(Panel card, Check box, int y, Control value, Control? unit, string hint)
    {
        box.Location = new Point(18, y + 6);
        card.Controls.Add(box);

        value.Location = new Point(168, y);
        card.Controls.Add(value);

        int x = 168 + value.Width + 8;
        if (unit != null)
        {
            unit.Location = new Point(x, y);
            card.Controls.Add(unit);
            x += unit.Width + 14;
        }
        else x += 14;

        var h = UiHelp.Lbl(hint, Theme.Small, Theme.Muted);
        h.Location = new Point(x, y + 6);
        card.Controls.Add(h);
    }

    private void Load_()
    {
        var l = Limits.ReadVmLimits();
        var warnings = new List<string>();
        if (l.ReadError != null) warnings.Add("the existing .wslconfig could not be read (" + l.ReadError + ")");

        ApplySize(l.Memory, _memOn, _memValue, _memUnit, 1, "memory", warnings);
        ApplySize(l.Swap, _swapOn, _swapValue, _swapUnit, 0, "swap", warnings);

        if (l.Processors != null && int.TryParse(l.Processors, out var p) && p >= 1)
        {
            _procOn.Checked = true;
            _procValue.Value = p;
            if (p > Environment.ProcessorCount)
                warnings.Add($"processors={p} is above the {Environment.ProcessorCount} cores this machine has");
        }
        else
        {
            _procOn.Checked = false;
            if (l.Processors != null) warnings.Add($"processors={l.Processors} is not a valid number");
        }

        foreach (var c in new Control[] { _memValue, _memUnit }) c.Enabled = _memOn.Checked;
        foreach (var c in new Control[] { _swapValue, _swapUnit }) c.Enabled = _swapOn.Checked;
        _procValue.Enabled = _procOn.Checked;

        if (warnings.Count > 0)
        {
            _status.ForeColor = Theme.Warn;
            _status.Text = "Current .wslconfig has problems: " + string.Join("; ", warnings) +
                           ". Applying from here will fix them.";
        }
        else
        {
            _status.ForeColor = Theme.Muted;
            _status.Text = File.Exists(Limits.WslConfigPath)
                ? "Loaded from " + Limits.WslConfigPath
                : "No .wslconfig yet - applying creates one.";
        }
    }

    /// The ceiling belongs to the unit. 1024 is a sane cap in GB and far too low in MB,
    /// where an existing "memory=1536MB" would clamp to 1024 and be written back reduced
    /// without a word said.
    internal static void FitRange(NumBox value, UnitToggle unit, int min) =>
        value.SetRange(min, unit.Unit == "GB" ? 1024 : 1024 * 1024);

    /// Switching the unit converts the number with it. Re-ranging alone was worse than the
    /// bug it replaced: 1536 MB became 1024 GB because the new ceiling clamped it, and 16 GB
    /// became 16 MB because the number stayed put - both written to .wslconfig without a word.
    /// Converting can round, but the changed number is on screen before anything is saved.
    internal static void SwitchUnit(NumBox value, UnitToggle unit, int min)
    {
        var toMb = unit.Unit == "MB";
        long converted = value.Value == 0
            ? 0
            : toMb ? (long)value.Value * 1024
                   : Math.Max(1, (long)Math.Round(value.Value / 1024.0));

        FitRange(value, unit, min);
        value.Value = (int)Math.Clamp(converted, min, toMb ? 1024 * 1024 : 1024);
    }

    internal static void ApplySize(string? raw, Check box, NumBox value, UnitToggle unit,
        int min, string keyName, List<string> warnings)
    {
        if (raw == null) { box.Checked = false; return; }

        var bytes = Limits.ParseSizeValue(raw);
        if (bytes == null)
        {
            box.Checked = false;
            warnings.Add($"{keyName}={raw} has no valid unit (WSL reads a bare number as bytes)");
            return;
        }

        box.Checked = true;
        if (bytes == 0)
        {
            unit.Unit = "GB";
            FitRange(value, unit, min);
            value.Value = 0;
            return;
        }

        var gb = bytes.Value / (1024d * 1024 * 1024);
        var wholeGb = gb >= 1 && Math.Abs(gb - Math.Round(gb)) < 0.001;

        // The unit and its range go first: setting the number into the old range is what
        // silently truncated it.
        unit.Unit = wholeGb ? "GB" : "MB";
        FitRange(value, unit, min);
        value.Value = wholeGb
            ? (int)Math.Round(gb)
            : (int)Math.Round(bytes.Value / (1024d * 1024));
    }

    private Limits.VmLimits Collect() => new()
    {
        Memory = _memOn.Checked ? _memValue.Value + _memUnit.Unit : null,
        Processors = _procOn.Checked ? _procValue.Value.ToString() : null,
        // "0" is the documented way to turn swap off; it needs no unit.
        Swap = _swapOn.Checked
            ? (_swapValue.Value == 0 ? "0" : _swapValue.Value + _swapUnit.Unit)
            : null,
    };

    /// Takes the process-wide operation gate, or explains why it could not.
    private bool Busy(string message)
    {
        if (Ops.TryEnter()) return true;
        _status.ForeColor = Theme.Warn;
        _status.Text = message;
        return false;
    }

    private void Apply(bool restart)
    {
        var limits = Collect();

        var error = Limits.Validate(limits);
        if (error.Length > 0)
        {
            _status.ForeColor = Theme.Crit;
            _status.Text = error;
            return;
        }

        if (!restart)
        {
            if (!Busy("Another operation is still running.")) return;
            try
            {
                Limits.WriteVmLimits(limits);
                _status.ForeColor = Theme.Ok;
                // Said plainly: this path deliberately skips the boot check, so it also has
                // no rollback if the VM turns out not to like the file.
                _status.Text = "Saved, but not verified - no VM was started with it. " +
                               "Takes effect after the next VM restart.";
            }
            catch (Exception ex)
            {
                _status.ForeColor = Theme.Crit;
                _status.Text = "Failed: " + ex.Message;
            }
            finally { Ops.Leave(); }
            return;
        }

        var containers = _snapshot().Containers.Count;
        var msg = "This writes .wslconfig, shuts the VM down and starts it again.\n\n" +
                  "Every distro stops immediately" +
                  (containers > 0 ? $", including {containers} running container(s)." : ".") +
                  "\nContainers with a restart policy come back once WSL is up.\n\n" +
                  "If the VM will not boot with these values, the previous config is restored " +
                  "automatically.\n\nContinue?";
        if (MessageBox.Show(msg, "Restart the WSL VM?", MessageBoxButtons.OKCancel,
                MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.OK)
            return;

        // Two applies at once would have the second capture the first one's half-written
        // file as its "previous" and copy it over the only good backup.
        if (!Busy("Another operation is still running.")) return;

        SetBusy(true, "applying and restarting the VM...");
        var distro = _snapshot().Distros.FirstOrDefault(d => d.Running)?.Name
                     ?? _snapshot().Distros.FirstOrDefault()?.Name;

        Task.Run(() => Limits.ApplyAndRestart(limits, distro)).ContinueWith(t =>
        {
            Ops.Leave();
            if (IsDisposed) return;
            BeginInvoke(() =>
            {
                SetBusy(false, "");

                // ApplyAndRestart shells out, and Process.Start can throw. Reading t.Result
                // on a faulted task would rethrow here, on the UI thread, and take the app
                // with it - exactly the moment the user needs to be told what happened.
                if (t.IsFaulted)
                {
                    _status.ForeColor = Theme.Crit;
                    _status.Text = "Failed: " + (t.Exception?.GetBaseException().Message ?? "unknown error");
                    _onVmRestarted();
                    return;
                }

                var r = t.Result;
                _status.ForeColor = r.Ok ? Theme.Ok : Theme.Crit;
                _status.Text = r.Message;
                if (r.RolledBack) Load_();
                _onVmRestarted();
            });
        });
    }

    private void UpdateButtons()
    {
        _apply.Enabled = !Ops.Busy;
        _saveOnly.Enabled = !Ops.Busy;
    }

    private void OnOpsChanged()
    {
        if (!IsHandleCreated || IsDisposed) return;
        try { BeginInvoke(UpdateButtons); }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Ops.Changed -= OnOpsChanged;
        base.Dispose(disposing);
    }

    private void SetBusy(bool busy, string text)
    {
        UpdateButtons();
        if (busy)
        {
            _status.ForeColor = Theme.Muted;
            _status.Text = text;
        }
        Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
    }
}
