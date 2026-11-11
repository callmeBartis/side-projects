# wsl-tray - architecture and decisions

## Files

| File | Job |
|---|---|
| `Program.cs` | entry point, single-instance mutex, `--window` / `--probe` / `--selftest` |
| `TrayApp.cs` | NotifyIcon, context menu, dynamically drawn gauge icon |
| `Monitor.cs` | polling loop, rate math, snapshot |
| `Ops.cs` | the process-wide gate: one long wsl.exe mutation at a time |
| `Wsl.cs` | every `wsl.exe` call and every parser, plus start/terminate/shutdown |
| `Limits.cs` | `.wslconfig` read/rewrite, value validation, apply-with-rollback, vhdx size |
| `Config.cs` | settings as JSON |
| `MainForm.cs` | window shell, Overview, distro start/stop/restart/shell |
| `DistrosPanel.cs` | install from the catalog, import, clone, export, unregister |
| `LimitsPanel.cs`, `SettingsPanel.cs` | the other two views |
| `Ui.cs` | theme, metric bar, flat button, list helpers |
| `SelfTest.cs` | assertions for parsers, validation, rewriter; opt-in VM integration test |

## Decisions (ADR-light)

**C# / WinForms on .NET 10.** Chosen by the user over Go. .NET 10 is LTS until Nov 2028;
.NET 8 goes end-of-life 10 Nov 2026. WinForms gives a native tray with zero dependencies.

**No NuGet packages at all.** `NotifyIcon`, `System.Text.Json` and GDI+ cover everything.

**Self-contained single file.** ~47 MB, but it runs on any Windows box without a .NET
runtime, which was an explicit requirement. Trimming stays off: WinForms is not trim-safe.

**One shell call per distro per poll.** Each `wsl.exe` invocation costs ~150 ms of process
startup, so the sample script emits everything at once behind `@section` markers.
Aggregation (`awk` sums) happens inside the VM so a machine with 300 processes still
returns two numbers, not 300 lines.

**Scripts travel as base64.** A shell script passed as a normal argument goes through
Windows argument parsing and then through `sh`, and quoting breaks in both.
`echo <b64> | base64 -d | sh` removes that entire class of bug.

**Line-based `.wslconfig` rewriting.** Serializing a parsed INI back out would drop
comments and reorder keys. The rewriter touches only the keys it owns inside `[wsl2]`
and writes a `.bak` first.

**Units are picked, not typed.** See the incident below. Free-text size fields are a
foot-gun that takes the whole VM down, so the UI offers a number and a unit toggle.

**Fixed window, hand-placed layout.** Every view is sized to fit exactly.
With no resizing there is no layout manager to fight, and the fixed canvas is what makes
the dense single-screen overview possible. The window is 1180x746: the Overview spends
that width on two columns (distros next to containers), while the three form views keep
a 760px measure, because a form does not get better from being stretched.

**Frameless, with the header bar as content.** `FormBorderStyle.None` plus a `TitleBar`
control docked at the top, so title, window buttons and body are one surface instead of
a system frame wrapped around a panel. Dragging forwards `WM_NCLBUTTONDOWN` with
`HTCAPTION` to the window manager rather than moving the form by hand, which keeps snap
and screen-edge behaviour. Corners are rounded through
`DwmSetWindowAttribute(DWMWA_WINDOW_CORNER_PREFERENCE)`; on anything older than Windows
11 the call is absent and the window simply has square corners.

**Yaru palette.** The Ubuntu desktop look: `#1E1E1E` window, `#303030` header, `#262626`
sidebar, cards at `#2C2C2C`, and Ubuntu orange `#E95420` as the only saturated colour.
Because orange and amber sit close together, the load bar's warn step starts at 80%
rather than 75%, and red is what actually reads as trouble.

**Own controls instead of the system ones.** `NumericUpDown`, `TextBox`, `ComboBox` and
`CheckBox` all draw part of themselves through the shell, which ignores every colour the
app sets - grey spinners, a hard border, a grey arrow, and a tick in the Windows accent
blue. `NumBox`, `Field`, `Picker`, `UnitToggle` and `Check` replace them. For a two-value
unit picker a toggle button is also simply less code than a dropdown.

`Picker` keeps a real `ComboBox` underneath rather than reimplementing a popup: keyboard
handling, the list window and its scrolling are all Windows'. What is hidden is only the
chrome - the combo is inset by 2px inside a clip panel so its own border is cut off on
all four sides, and the arrow button is covered by the chevron we draw. Making the combo
*wider* to clip the arrow away also works, but the popup is never narrower than the
combo, so the list then hangs over the field by the same amount.

**No terminal of our own.** An embedded console would mean ConPTY plus a VT parser and a
renderer, roughly a thousand lines that would never match Windows Terminal. The Shell
button starts a real terminal around `wsl.exe` instead: `wt.exe -w 0 nt` opens a tab in
the existing window when Windows Terminal is installed, otherwise `wsl.exe` gets its own
console because this process has none to inherit. `--cd ~` keeps the shell out of
`/mnt/c`.

**Deleting is gated by typing the name, not by a dialog.** `wsl --unregister` erases the
vhdx with no recycle bin, and an OK button next to a dropdown is one mis-click away from
that. The Delete button only enables on an exact match of the selected distro's name, and
it lives in the app's own theme instead of a system message box.

**Cloning is export plus import**, with the intermediate tar written next to the new
distro rather than into `%TEMP%`: a clone can be tens of GB and `%TEMP%` sits on the
system drive. The source is terminated first, otherwise the copy is taken while files
are being written.

**Autostart and the Start menu own their own copy of the .exe.** A Run entry or shortcut
holding the path the file had when the box was ticked is a trap: move the .exe, delete
the download, unzip a new build somewhere else, and the entry fails with nothing on
screen to say so. Either option therefore copies the .exe next to the config and points
at that copy, and a start from anywhere else refreshes it when size or timestamp differ
(`File.Copy` keeps the write time, so no hashing of 49 MB per start). Turning an option
off removes only its entry and leaves the copy, by request: a file the user deletes
themselves beats one that vanishes while something else may still point at it.

**The shortcut is written through `WScript.Shell`.** .NET has no API for `.lnk` files.
The shell's scripting object via late binding (`Type.InvokeMember`) is a dozen lines and
needs no COM interface declarations; `IShellLink` interop would be four times that. It
goes into the per-user Programs folder, which is what Windows search indexes, so no
admin rights are needed.

**The icon is generated, not drawn by hand.** `tools/make-icon.ps1` renders an
Ubuntu-orange tile with a stroked `>_` prompt at 256/64/48/32/24/16 px and packs the PNGs
into `app.ico`. Strokes instead of a font keep it crisp at 16 px. Re-run it after
changing the design:
`powershell -File tools\make-icon.ps1 -Out app.ico`.

**One operation gate for the whole process** (`Ops.cs`). Start, Stop, Restart and Shell in
the Overview, install, import, clone, export and delete in Distros, apply in Limits and
"Shut down WSL VM" in the tray all mutate the same distros. Each view used to guard only
its own buttons, which is not a guard at all: a clone stops a distro and then spends
minutes reading its disk, and the Overview in another tab happily offered Start. The gate
is static because panels are rebuilt on every tab switch, it raises an event on both edges
so whichever panel is alive can re-enable itself, and the poll loop honours it too - while
a mutation runs, sampling would restart the very distro that was just stopped.

**Only a self-contained build may be installed as the boot copy.** See the incident below.
A `dotnet build` .exe is an apphost that loads `wsl-tray.dll` from its own folder; copying
it alone yields an .exe that cannot start. The check is the presence of that sibling .dll.

**Apply verifies, then rolls back.** Writing config and restarting is not enough: the
tool starts a distro afterwards and, if that fails, restores the previous file and
brings the VM back. An unbootable VM is not a state to leave a user in.

## Incident that shaped this (2026-09-10)

The first version had a free-text Memory field. `10` was entered, written verbatim, and
WSL read it as **10 bytes**. Every start then failed with
`Wsl/Service/CreateInstance/CreateVm/E_UNEXPECTED` and the whole Docker environment was
down until `.wslconfig` was restored by hand.

Three defences were added, all covered by tests: unit toggles, `Limits.Validate`, and
`Limits.ApplyAndRestart` with automatic rollback.

Note for later: `processors` was **not** part of that failure. `nproc` inside WSL reports
the *limited* core count, not the host's - this machine has 32 logical cores while WSL
showed 10, because `processors=10` was set.

## Second incident: the autostart copy that never ran (2026-09-12 to 2026-09-15)

Autostart copies the running .exe to `%APPDATA%\wsl-tray` and boots that copy. During
testing the build under `bin/Release` was started by hand, so that .exe became the boot
copy - and a `dotnet build` .exe is 166 KB of launcher that loads `wsl-tray.dll` from the
folder next to it. The copy was made without its folder.

It failed at every boot, and left nothing behind but an event log entry:

```
Application: C:\Users\Bartis\AppData\Roaming\wsl-tray\wsl-tray.exe
Message: The application to execute does not exist: '...\wsl-tray.dll'
```

Two boots went that way before anyone looked. What made it invisible is the same thing
that made the first incident expensive: the failure happens where nobody is watching. The
lesson is not "publish before testing" - it is that an installer must refuse input it
cannot make work, and must say so where the user is looking. `InstallCopy` now rejects a
build with a sibling `.dll`, and Settings shows a red line if the installed copy is too
small to be the real thing.

## Gotchas found while building this

- **`wsl.exe` outputs UTF-16LE.** Without `WSL_UTF8=1` in the environment, every parser
  sees NUL-separated bytes. The raw output of `wsl -l -v` starts `32,0,32,0,78,0`.
- **Never run `wsl -d <name>` against a stopped distro.** It *starts* it. Polling goes
  through `wsl -l -q --running` first, or the monitor boots every distro on the machine.
- **`wsl -l -v` has a localized table header.** `wsl -l -q` returns names only.
- **CPU is not observable from Windows.** `Get-Process vmmemWSL` returns an empty
  `TotalProcessorTime`, and `\Process(vmmemWSL)\% Processor Time` does not exist.
- **`.wslconfig` treats `\` as an escape character.** A path written with single
  backslashes is silently discarded rather than rejected - this cost one wrong test
  result before the integration test worked.
- **`Icon.FromHandle` does not own its handle.** Redrawing the tray icon without
  `DestroyIcon` leaks one GDI handle per redraw; Windows kills the process near 10k.
- **`Anchor.Right` does nothing inside an `AutoScroll` panel** - the layout width is
  virtual. `UiHelp.StretchToWidth` sets widths from a resize handler instead.
- **ListView column headers are shell-drawn** and stay white in dark mode unless the
  header is owner-drawn. That also applies to the strip *past* the last column: any
  leftover width, even 30px, shows up as a bright block in the header, so the trailing
  column has to swallow every remaining pixel.
- **Half the WinForms control set cannot go dark.** ListView headers and selection,
  NumericUpDown spinners and ComboBox arrows are all shell-drawn. Owner-draw fixes the
  list; the input controls had to be rebuilt.
- **`ShowFocusCues` did not suppress the leftover focus ring** on a nav entry driven by
  `BM_CLICK`. The fix is to move focus with the active entry rather than to hide the ring.
- **A Card keeps `BackColor` at the window tone** and paints its surface in `OnPaint`,
  so a child asking `Parent.BackColor` clears to the wrong colour and shows up as a box
  behind its own label. `Draw.GroundOf` resolves the real ground instead.
- **Every distro has its own dockerd.** They share the kernel and the *network*
  namespace, but not the mount namespace, so each has its own `/var/run/docker.sock`,
  its own containers and its own images. Container names are therefore only unique
  within a distro. The shared network namespace also means two distros publishing the
  same port collide (microsoft/WSL#4304).
- **`wsl -l -o` ignores `--quiet`.** It always prints a localized banner and a table
  header, so nothing in it can be matched by text. What survives translation is the
  shape: one blank line separates the banner from the table.
- **`ShellExecuteEx` is refused from this process.** `UseShellExecute = true` on either
  `wt.exe` or `wsl.exe` returns "access denied", while `CreateProcess` on the same
  binaries works. Found by clicking the real button, not by reading the code.
- **`wsl --export` does not infer the format** from the file extension, so a `.tar.gz`
  written without `--format tar.gz` is a plain tar with a name that lies.
- **`awk file1 file2 ...` exits non-zero if any one file cannot be opened**, and under
  `/proc/[0-9]*/` that happens constantly: processes die between the shell expanding the
  glob and awk opening the file. Put such an awk last in a script and its exit code
  becomes the script's. Piping through `cat` (which reports the miss on stderr and
  carries on) is what makes a `/proc` sweep survive a busy machine.
- **A NumericUpDown-style range is a silent truncator.** A value loaded from a config
  into a field whose maximum is too low clamps without a word and gets written back
  smaller. The range has to follow the unit, before the value is set.
- **`NotifyIcon` belongs to the thread that created it.** A background poller setting
  `.Text` or `.Icon` mostly appears to work, which is what makes it worth finding on
  purpose. An invisible `Control` created on the UI thread is the way back.
- **`Process.WaitForExit(int)` does not drain redirected output.** The parameterless
  overload does; the timeout one returns as soon as the process is gone, so a grandchild
  still holding the pipe leaves `ReadToEndAsync` unfinished. Reading `.Result` after that
  blocks with no timeout at all, which turns a bounded call into a permanent freeze.
- **`Icon.FromHandle` does not own its handle, and `NotifyIcon` keeps the Icon.** Destroying
  the handle after assigning it leaves the tray holding freed GDI memory, which shows up
  after an Explorer restart rather than immediately. Clone the icon, then destroy the handle.
- **A disposed `Control` reports `InvokeRequired == false`.** A marshalling guard built on
  it reads that as "you are already on the UI thread" during shutdown, which is precisely
  when it is not.
- **`CB_SETCURSEL` and friends raise `SelectedIndexChanged`** while a list is being
  repopulated, so a background refresh looks exactly like the user picking something else.
- **GNU `df` wraps a long device name onto two lines**, and `tail -1` then returns the
  numbers with every column shifted by one. `df -P` is the POSIX one-line format.
- **Removing a control from its parent destroys its handle**, and putting it back creates a
  new one - so `OnHandleCreated` fires on every tab switch, not once. Anything expensive or
  anything that writes to the UI belongs behind an explicit "already done" check.
- **wsl.exe serializes.** While an export or import runs, even `wsl -l -q` can block, so the
  whole poll stalls and the Overview stands still on its last numbers until the operation
  finishes. That is why those numbers have to be the last *real* ones rather than zeros.
- **A keep-alive script defeats Stop.** If something holds a process open in the distro,
  it restarts within seconds. The Overview says so after a stop.

## Verified on this machine

- `--selftest`: 60 checks pass, including that a bare `10` is rejected, that a rewrite
  preserves comments and the `[experimental]` section, and that an invalid apply does
  not touch the file.
- `--selftest --with-vm`: a valid apply restarts the VM and leaves it running; an
  unbootable VM is reported as a failure, the rollback runs, and the config is restored
  byte for byte.
- Buttons driven through the real UI: Stop left Ubuntu `Stopped` and showed the
  keep-alive warning; Restart went `restarting Ubuntu...` then `restart done` with the
  distro running.
- `--probe`: 10 cores (limited from 32), 1.0 GB / 23 GB, Ubuntu at 866 MB RSS with a
  15 GB vhdx, three containers, and the online catalog read live (22 entries, matching
  the 22 rows `wsl -l -o` prints).
- **The Distros view driven end to end through the real UI** (2026-09-10), using a
  234-byte hand-built rootfs tar so nothing had to be downloaded: import created
  `wsltray-a`, clone created `wsltray-b` via terminate + export + import and confirmed
  the modal, the Delete button stayed disabled while a wrong name was typed and enabled
  on the exact one, both were unregistered, and nothing was left behind - no clone tar,
  no folders, Ubuntu untouched and still running.
- **A real catalog install, 2026-09-20.** Debian was installed through the UI end to end:
  picked from the live catalog, downloaded into the default folder, registered, and shown
  in the Overview next to Ubuntu - green dot with figures for the running one, red dot and
  "-" for the stopped one, which is the whole point of not drawing an unmeasured distro as
  zero. It was then removed through the Delete field (disabled on a wrong name, enabled on
  the exact one) and left nothing behind. This was the last headline feature that had only
  ever been verified by the shape of its arguments.
- The Shell button, clicked through the real UI: an interactive `bash` appeared inside
  Ubuntu (2 before, 3 after) and the status line reported `Ubuntu: terminal opened.`
- The same end-to-end run again after `TextBox`/`ComboBox` were replaced by `Field` and
  `Picker`, to prove the swap changed only the chrome: import, clone, both delete gates
  and both deletes still behave identically.
- Both status dots: a second distro was imported and left stopped, so the list showed a
  green and a red dot side by side, then it was unregistered again.
- **Autostart survives deleting the .exe** (2026-09-10). A copy was placed in `C:\tmp\dl`
  to stand in for a download and started from there; the stale Run entry (still pointing
  at the build folder) was migrated on that start. Toggling the box off removed the entry
  and the copy, toggling it on wrote both back. `C:\tmp\dl` was then deleted and the
  registered path started fine - `started from: C:\Users\...\AppData\Roaming\wsl-tray\wsl-tray.exe`.
  A newer build started by hand moved the copy's timestamp from `21:47:08` to `21:47:45`.
- **`--selftest --with-vm`, re-run 2026-09-20** against the current build, after the apply
  path had been changed in the second round. All seven checks pass: a valid apply leaves the
  VM running, an unbootable one is reported as a failure, the rollback runs and restores the
  file byte for byte (`sha256` identical before and after the whole run), and the machine is
  left working. Note the wording in the second half: the config being rolled back *to* is
  itself the unbootable one the test planted, so "did not recover after rolling back" is the
  honest answer there, not a false alarm.
  Containers with a restart policy came back on their own; three test databases created with
  `--restart no` stayed down, as WSL and Docker intend and as the confirmation dialog says.
- **Third bug hunt, 2026-09-16.** Aimed at the second round's own changes, which is where
  two of the three findings came from. A screenshot taken while a clone was running shows
  the Overview holding real values (CPU 0.5 %, 1.1 GB / 12 GB, Ubuntu at 1016 MB) with every
  button correctly disabled, where the unfixed build would have shown zeros throughout. The
  status line now survives a tab switch: `'Cloning wsltray-a to wsltray-b: done.'` before and
  after. Import, clone, both delete gates and the gate itself were re-run end to end, and the
  boot copy was refreshed and started again to confirm the install path still holds.
- **Second bug hunt, 2026-09-12 to 2026-09-16.** `--selftest` is at 90 checks. The
  operation gate was proven through the real UI: with a clone running on the Distros tab,
  the Overview reported `Start enabled: False`, and switching tabs mid-clone no longer
  leaves the panel dead - the result still arrived. The broken autostart copy was found in
  the Windows event log (two boot failures), repaired by running the published build
  (166 KB replaced by 46.9 MB), and the repaired copy was then started to prove it runs.
  Import, clone, both delete gates, the Shell button and Settings Save were all re-run
  afterwards and behave as before.
- **Bug hunt, 2026-09-12.** Two fixes were proven by running the build from before the
  fix against the build after it, on the same machine under the same load:
  the old build reported `Ubuntu  cpu 0.0 %  rss 0 B` in three consecutive probes during
  a fork storm in the distro while the new one reported `cpu 6.4 %  rss 959 MB`; and
  clicking Save in Settings changed the window handle on the old build (`4133088` ->
  `4198624`, confirmation lost) while the new one kept `136910076` and showed "Saved.".
  The `/proc` glob race itself was measured directly: 236 of 300 awk runs failed under
  churn, 0 of 300 after piping through `cat`. The memory-unit truncation is covered by
  `--selftest` (81 checks).
- **Start menu entry, clicked through the real UI** (2026-09-11): `WSL Tray.lnk` was
  written with its target on the `%APPDATA%` copy, and `Get-StartApps` - the index that
  Windows search reads - listed "WSL Tray" seconds later. Turning autostart off then
  removed the Run value and left the copy in place.

## Open / not built

- **Per-distro CPU is approximate** when several distros run at once.
- **Container accumulation across distros is still untested against reality.** Two and
  three distros have now been listed at once, including a real Debian, but none of the
  extra ones ran docker, so the qualified `<distro> / <container>` naming is still only
  verified by construction.
- **No live progress during an install or a clone**, just an elapsed counter. `wsl.exe`
  writes its download progress as a carriage-return bar that would have to be parsed out
  of a redirected stream.
- **Top processes per distro** was scoped out; the data is one `ps` call away.
- **x64 only.** An ARM64 publish needs a second RID.
- **Dark mode is read once at startup.** Switching the Windows theme while the app runs
  does not restyle it; restart the app.
- **Removed by request on 2026-09-10:** alert rules, Windows notifications, and the
  systemd/cgroup live limits. Alerts and notifications went together because rules
  without notifications had no way to reach the user.
