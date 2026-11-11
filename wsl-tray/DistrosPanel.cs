namespace WslTray;

/// Create, clone, back up and delete distros. Everything here is a wsl.exe call that
/// can run for minutes (an install downloads, a clone copies the whole filesystem), so
/// every action runs off the UI thread with an elapsed counter - a silent window for
/// five minutes looks like a hang.
public sealed class DistrosPanel : Panel
{
    private readonly Config _cfg;
    private readonly Action _onChanged;

    private readonly Picker _catalog = new(330);
    private readonly Field _newName = new(200, "optional");
    private readonly Field _folder = new(330);
    private readonly FlatBtn _install = new("Install", primary: true) { Width = 150 };

    private readonly Field _file = new(330, "a .tar, .tar.gz or .vhdx");
    private readonly Field _importName = new(200, "new distro name");
    private readonly FlatBtn _import = new("Import", primary: true) { Width = 150 };

    private readonly Picker _existing = new(220);
    private readonly FlatBtn _export = new("Export...") { Width = 110 };
    private readonly Field _cloneName = new(200, "new distro name");
    private readonly FlatBtn _clone = new("Clone") { Width = 110 };
    private readonly Field _deleteConfirm = new(200, "type the name");
    private readonly FlatBtn _delete = new("Delete") { Width = 110, Primary = true, Tone = Theme.Crit };

    private readonly Label _status = UiHelp.Lbl("", Theme.Body, Theme.Muted);
    private readonly System.Windows.Forms.Timer _tick = new() { Interval = 1000 };

    /// Set while this panel repopulates the distro list, so the ComboBox's own churn is not
    /// mistaken for the user picking something else and does not disarm the delete field.
    private bool _reloading;
    private bool _catalogLoaded;
    private string _busyText = "";
    private DateTime _startedAt;

    public DistrosPanel(int width, Config cfg, Action onChanged)
    {
        _cfg = cfg;
        _onChanged = onChanged;
        BackColor = Theme.Bg;

        var head = UiHelp.Lbl("Distros", Theme.Title);
        head.Location = new Point(0, 0);
        Controls.Add(head);

        // Beside the title, not under it: the window is a fixed size and the three cards
        // below need every pixel.
        var note = UiHelp.Lbl(
            "a new distro joins the same utility VM and shares its limits and its network",
            Theme.Small, Theme.Muted);
        note.Location = new Point(92, 7);
        Controls.Add(note);

        _folder.Text = cfg.DistroPath.Trim().Length > 0 ? cfg.DistroPath.Trim() : DefaultFolder();

        Controls.Add(BuildInstall(width, 30));
        Controls.Add(BuildImport(width, 246));
        Controls.Add(BuildExisting(width, 424));


        _status.Location = new Point(2, 634);
        _status.MaximumSize = new Size(width - 4, 0);
        Controls.Add(_status);

        _tick.Tick += (s, e) =>
        {
            var t = DateTime.UtcNow - _startedAt;
            _status.Text = $"{_busyText}... {(int)t.TotalMinutes}:{t.Seconds:00}";
        };

        _deleteConfirm.TextChanged += (s, e) => SetEnabled();
        _existing.SelectedIndexChanged += (s, e) =>
        {
            if (_reloading) return;
            _deleteConfirm.Clear();
            SetEnabled();
        };

        Ops.Changed += OnOpsChanged;
        SetEnabled();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        // This fires again on every tab switch: taking a control out of its parent destroys
        // its handle, and putting it back creates a new one. The catalog must therefore not
        // be fetched here unconditionally - that is a process launch and an online download
        // per visit, and it overwrote the status line of whatever had just finished.
        // The installed distros are cheap and can change behind our back, so they reload.
        if (_catalogCache != null) FillCatalog(_catalogCache, announce: false);
        else LoadCatalog();

        LoadExisting();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Ops.Changed -= OnOpsChanged;
            _tick.Dispose();
        }
        base.Dispose(disposing);
    }

    private void OnOpsChanged()
    {
        if (!IsHandleCreated || IsDisposed) return;
        try { BeginInvoke(SetEnabled); }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    // --- layout ---------------------------------------------------------------------

    // Every input, dropdown and button is 34 high, so a row is one y value for all of
    // them and only the captions need centring.
    private const int R1 = 44, R2 = 82, R3 = 120;

    private Card BuildInstall(int width, int top)
    {
        var card = Section(width, top, 204, "Install from the online catalog");

        Put(card, Cap("Distribution"), 18, R1 + 9);
        Put(card, _catalog, 150, R1);

        Put(card, Cap("Name"), 18, R2 + 9);
        Put(card, _newName, 150, R2);
        Put(card, UiHelp.Lbl("defaults to the catalog name", Theme.Small, Theme.Muted),
            360, R2 + 9);

        Put(card, Cap("Folder"), 18, R3 + 9);
        Put(card, _folder, 150, R3);
        var browse = new FlatBtn("Browse...") { Width = 90 };
        browse.Click += (s, e) => PickFolder();
        Put(card, browse, 490, R3);

        _install.Click += (s, e) => Install();
        Put(card, _install, 18, 158);
        Put(card, UiHelp.Lbl("the distro is not launched, so open a shell to create its user account",
            Theme.Small, Theme.Muted), 180, 167);

        return card;
    }

    private Card BuildImport(int width, int top)
    {
        var card = Section(width, top, 166, "Import a tar or vhdx");

        Put(card, Cap("File"), 18, R1 + 9);
        Put(card, _file, 150, R1);
        var pick = new FlatBtn("Browse...") { Width = 90 };
        pick.Click += (s, e) => PickFile();
        Put(card, pick, 490, R1);

        Put(card, Cap("Name"), 18, R2 + 9);
        Put(card, _importName, 150, R2);
        Put(card, UiHelp.Lbl("created inside the folder above", Theme.Small, Theme.Muted),
            360, R2 + 9);

        _import.Click += (s, e) => Import();
        Put(card, _import, 18, R3);

        return card;
    }

    private Card BuildExisting(int width, int top)
    {
        var card = Section(width, top, 202, "Clone, back up or delete an existing distro");

        Put(card, Cap("Distro"), 18, R1 + 9);
        Put(card, _existing, 150, R1);
        _export.Click += (s, e) => Export();
        Put(card, _export, 390, R1);

        Put(card, Cap("Clone to"), 18, R2 + 9);
        Put(card, _cloneName, 150, R2);
        _clone.Click += (s, e) => Clone();
        Put(card, _clone, 390, R2);

        Put(card, Cap("Delete"), 18, R3 + 9);
        Put(card, _deleteConfirm, 150, R3);
        _delete.Click += (s, e) => Delete();
        Put(card, _delete, 390, R3);

        Put(card, UiHelp.Lbl(
            "Cloning and exporting stop the distro first, otherwise the copy is taken while files are\n" +
            "being written. Delete unregisters it and erases its disk - type the name to unlock it.",
            Theme.Small, Theme.Muted), 18, 160);

        return card;
    }

    private static Card Section(int width, int top, int height, string title)
    {
        var card = UiHelp.Card();
        card.Location = new Point(0, top);
        card.Size = new Size(width, height);

        var t = UiHelp.Lbl(title, Theme.BodyBold);
        t.Location = new Point(18, 12);
        card.Controls.Add(t);
        return card;
    }

    private static void Put(Control card, Control c, int x, int y)
    {
        c.Location = new Point(x, y);
        card.Controls.Add(c);
    }

    private static Label Cap(string text) => UiHelp.Lbl(text, Theme.BodyBold);

    // --- data -----------------------------------------------------------------------

    private static string DefaultFolder()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(local, "WSL");
    }

    /// The published catalog changes a few times a year, so it is fetched once per run.
    private static List<Wsl.OnlineDistro>? _catalogCache;

    private void LoadCatalog()
    {
        _status.ForeColor = Theme.Muted;
        _status.Text = "loading the catalog...";

        Task.Run(Wsl.ListOnline).ContinueWith(t =>
        {
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke(() =>
            {
                var list = t.IsFaulted ? new List<Wsl.OnlineDistro>() : t.Result;
                if (list.Count > 0) _catalogCache = list;
                FillCatalog(list, announce: true);
            });
        });
    }

    private void FillCatalog(List<Wsl.OnlineDistro> list, bool announce)
    {
        _catalog.Items.Clear();
        foreach (var d in list) _catalog.Items.Add(d);
        if (_catalog.Items.Count > 0) _catalog.SelectedIndex = 0;

        _catalogLoaded = _catalog.Items.Count > 0;

        // Only the fetch reports. A refill from the cache must leave the status line alone,
        // or remounting the panel wipes the result of the operation the user came back for.
        if (!announce) { SetEnabled(); return; }

        _status.ForeColor = _catalogLoaded ? Theme.Muted : Theme.Warn;
        _status.Text = _catalogLoaded
            ? $"{_catalog.Items.Count} distributions available."
            : "The online catalog could not be read. Import still works.";
        SetEnabled();
    }

    private void LoadExisting()
    {
        Task.Run(Wsl.ListDistros).ContinueWith(t =>
        {
            if (IsDisposed || !IsHandleCreated) return;
            BeginInvoke(() =>
            {
                var keep = _existing.SelectedItem as string;
                _reloading = true;
                try
                {
                    _existing.Items.Clear();
                    foreach (var n in t.IsFaulted ? new List<string>() : t.Result) _existing.Items.Add(n);

                    if (keep != null && _existing.Items.Contains(keep)) _existing.SelectedItem = keep;
                    else if (_existing.Items.Count > 0) _existing.SelectedIndex = 0;
                }
                finally { _reloading = false; }

                SetEnabled();
            });
        });
    }

    private string Selected => _existing.SelectedItem as string ?? "";

    private bool Exists(string name) => _existing.Items.Cast<string>()
        .Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));

    private string FolderFor(string name) => Path.Combine(_folder.Text.Trim(), name);

    private void SetEnabled()
    {
        var busy = Ops.Busy;
        _install.Enabled = !busy && _catalogLoaded;
        _import.Enabled = !busy;
        _export.Enabled = !busy && Selected.Length > 0;
        _clone.Enabled = !busy && Selected.Length > 0;
        // The typed name is the only guard between a click and an erased disk.
        _delete.Enabled = !busy && Selected.Length > 0 &&
                          _deleteConfirm.Text.Trim().Equals(Selected, StringComparison.Ordinal);
        _catalog.Enabled = !busy;
        _existing.Enabled = !busy;
    }

    // --- actions --------------------------------------------------------------------

    private void PickFolder()
    {
        using var dlg = new FolderBrowserDialog { SelectedPath = _folder.Text.Trim(), UseDescriptionForTitle = true };
        dlg.Description = "Where new distros are stored";
        if (dlg.ShowDialog(this) == DialogResult.OK) _folder.Text = dlg.SelectedPath;
    }

    private void PickFile()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Pick a distro image",
            Filter = "Distro images (*.tar;*.tar.gz;*.tgz;*.tar.xz;*.vhdx)|*.tar;*.tar.gz;*.tgz;*.tar.xz;*.vhdx|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        _file.Text = dlg.FileName;
        if (_importName.Text.Trim().Length == 0)
        {
            // "Ubuntu-24.04-2026-09-10.tar.gz" -> "Ubuntu-24.04-2026-09-10"
            var stem = Path.GetFileNameWithoutExtension(dlg.FileName);
            if (stem.EndsWith(".tar", StringComparison.OrdinalIgnoreCase)) stem = stem[..^4];
            if (Wsl.IsDistroName(stem)) _importName.Text = stem;
        }
    }

    private void Install()
    {
        if (_catalog.SelectedItem is not Wsl.OnlineDistro d) return;

        var custom = _newName.Text.Trim();
        var target = custom.Length > 0 ? custom : d.Name;
        if (!CheckNewName(target)) return;

        // Read here, on the UI thread: the lambda runs minutes later on a worker, where
        // touching Field.Text is both a cross-thread read and a chance to pick up an edit
        // the user made after the validation above.
        var dir = FolderFor(target);

        Run("Installing " + target, () => Wsl.Install(d.Name, custom.Length > 0 ? custom : null, dir),
            () =>
            {
                _newName.Clear();
                _status.Text += "  Open a shell on it to create the user account.";
            }, usesFolder: true);
    }

    private void Import()
    {
        var file = _file.Text.Trim();
        if (file.Length == 0 || !File.Exists(file)) { Warn("Pick a file that exists."); return; }

        var name = _importName.Text.Trim();
        if (!CheckNewName(name)) return;

        var dir = FolderFor(name);
        Run("Importing " + name, () =>
        {
            Directory.CreateDirectory(dir);
            return Wsl.Import(name, dir, file);
        }, () => { _file.Clear(); _importName.Clear(); }, usesFolder: true);
    }

    private void Export()
    {
        var source = Selected;
        if (source.Length == 0) return;

        using var dlg = new SaveFileDialog
        {
            Title = "Export " + source,
            FileName = $"{source}-{DateTime.Now:yyyy-MM-dd}.tar.gz",
            Filter = "gzip tar (*.tar.gz)|*.tar.gz|tar (*.tar)|*.tar|xz tar (*.tar.xz)|*.tar.xz|vhdx (*.vhdx)|*.vhdx",
        };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;

        if (!ConfirmStop(source, "Exporting")) return;

        var file = dlg.FileName;
        Run("Exporting " + source, () =>
        {
            var err = Wsl.Terminate(source);
            return err.Length > 0 ? err : Wsl.Export(source, file);
        });
    }

    private void Clone()
    {
        var source = Selected;
        var target = _cloneName.Text.Trim();
        if (source.Length == 0 || !CheckNewName(target)) return;
        if (!ConfirmStop(source, "Cloning")) return;

        var dir = FolderFor(target);
        Run($"Cloning {source} to {target}", () =>
        {
            var err = Wsl.Terminate(source);
            if (err.Length > 0) return err;

            Directory.CreateDirectory(dir);

            // The tar lands next to the new distro instead of in %TEMP%: a clone can be
            // tens of GB and %TEMP% sits on the system drive.
            var tmp = Path.Combine(dir, target + ".clone.tar");
            try
            {
                err = Wsl.Export(source, tmp);
                return err.Length > 0 ? err : Wsl.Import(target, dir, tmp);
            }
            finally { try { File.Delete(tmp); } catch { /* leftover tar is harmless */ } }
        }, () => _cloneName.Clear(), usesFolder: true);
    }

    private void Delete()
    {
        var name = Selected;
        if (name.Length == 0 || _deleteConfirm.Text.Trim() != name) return;

        Run("Deleting " + name, () => Wsl.Unregister(name), () => _deleteConfirm.Clear());
    }

    private bool CheckNewName(string name)
    {
        if (name.Length == 0) { Warn("Enter a name."); return false; }
        if (!Wsl.IsDistroName(name))
        {
            Warn("A name may only hold letters, digits, dot, dash and underscore.");
            return false;
        }
        if (Exists(name)) { Warn($"A distro called {name} already exists."); return false; }
        if (_folder.Text.Trim().Length == 0) { Warn("Set a folder for new distros."); return false; }
        return true;
    }

    /// Export and clone copy the filesystem, so the distro has to stop first or the copy
    /// is taken mid-write. Stopping kills running containers, which is worth a question.
    private bool ConfirmStop(string distro, string what)
    {
        return MessageBox.Show(
            $"{what} {distro} stops it first, so a copy is not taken while files are being written.\n\n" +
            "Running containers stop with it. Those with a restart policy come back once the distro " +
            "is started again.\n\nContinue?",
            what + " " + distro + "?", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button1) == DialogResult.OK;
    }

    private void Warn(string text)
    {
        _status.ForeColor = Theme.Crit;
        _status.Text = text;
    }

    private void Run(string what, Func<string> action, Action? onOk = null, bool usesFolder = false)
    {
        // Process-wide: the Overview's Start button would otherwise boot a distro straight
        // into the middle of its own export.
        if (!Ops.TryEnter())
        {
            Warn("Another distro operation is still running.");
            return;
        }

        SetEnabled();
        _busyText = what;
        _startedAt = DateTime.UtcNow;
        _tick.Start();
        Cursor = Cursors.WaitCursor;
        _status.ForeColor = Theme.Muted;
        _status.Text = what + "...";

        Task.Run(action).ContinueWith(t =>
        {
            // Released before the disposed check: if this panel is gone the gate still has
            // to open, or every distro action stays dead for the rest of the session.
            Ops.Leave();

            if (IsDisposed) return;
            BeginInvoke(() =>
            {
                _tick.Stop();
                Cursor = Cursors.Default;

                var err = t.IsFaulted ? t.Exception?.GetBaseException().Message ?? "failed" : t.Result;
                if (err.Length == 0)
                {
                    _status.ForeColor = Theme.Ok;
                    _status.Text = what + ": done.";
                    onOk?.Invoke();

                    // Only an action that actually created something in that folder, and
                    // succeeded at it, gets to remember it. A path that just failed is not a
                    // default worth keeping.
                    if (usesFolder)
                    {
                        _cfg.DistroPath = _folder.Text.Trim();
                        try { _cfg.Save(); } catch { /* a config that will not write is not worth an error here */ }
                    }
                }
                else
                {
                    _status.ForeColor = Theme.Crit;
                    _status.Text = what + " failed: " + err.Split('\n')[0].Trim();
                }

                LoadExisting();
                _onChanged();
                SetEnabled();
            });
        });
    }
}
