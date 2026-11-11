# Changelog

## Unreleased

### Added
- Tray icon that doubles as a CPU gauge, with hover text for CPU, RAM and used memory.
- Overview of the shared WSL2 VM: CPU, memory, swap, in-VM disk, plus the Windows-side
  `vmmemWSL` figure.
- Per-distro usage (running state, CPU, process RSS, vhdx size on disk).
- Per-container usage from `docker stats`, on a slower cadence than the main poll.
- Start, Stop and Restart buttons per distro. After a stop, a keep-alive script in the
  Startup folder is detected and reported, because it silently restarts the distro.
- **Distros view: create, clone, back up and delete distros.** Install from the online
  catalog (`wsl --list --online`, read live), import a `.tar`/`.tar.gz`/`.tar.xz`/`.vhdx`,
  clone an existing distro, export one as a backup, and unregister one. Every action runs
  off the UI thread with an elapsed counter, because an install downloads and a clone
  copies the whole filesystem.
- **Shell button per distro.** Opens an interactive terminal on the selected distro in
  its Linux home, in Windows Terminal when it is installed. It works on a stopped distro
  too, since opening a shell is how a distro is started.
- `.wslconfig` editor for memory, processors and swap.
- **Autostart installs itself.** Enabling it copies the .exe to `%APPDATA%\wsl-tray` and
  points the Run entry at that copy, so the downloaded file can be moved or deleted
  afterwards. Starting a newer build by hand refreshes the copy. Disabling autostart
  removes only the Run entry; the copy stays.
- **Start menu entry.** A "show in Start menu" option in Settings writes a per-user
  shortcut to that same copy, so the tool is found by typing its name into Windows search.
- **App icon.** An Ubuntu-orange tile with a shell prompt, so the .exe, the Start menu
  entry and search results no longer show the blank default icon.
- Portable mode via a `config.json` next to the .exe.
- `--window`, `--probe` and `--selftest` command line modes, plus the opt-in destructive
  `--selftest --with-vm` which restarts the real VM to exercise the rollback path.

### Fixed
- **"No measurement" was drawn as a measured zero, again.** The new operation gate stops the
  poll from sampling while an install or clone runs, and a failed sample stops it too - but
  both paths then published zeros, so a running distro read `0.0 %  0 B` and the VM card
  claimed no memory and no disk next to a Windows figure saying it held gigabytes. The last
  known numbers are carried over instead. Same symptom as the `/proc` bug further down, a
  different cause, introduced by the fix for something else.
- **Every tab switch re-fetched the online catalog and wiped the status line.** Taking a
  control out of its parent destroys its handle, so the "load once" hook fired again on
  every visit - a process launch and a download each time, and the result of the install
  the user had just run was overwritten by "22 distributions available." The catalog is
  fetched once per run now, and a refill from that cache says nothing.
- The cached Overview and Distros views are disposed with the window; they are held outside
  `Controls`, so nothing else was going to, and the Distros view stayed subscribed to a
  static event after the window was gone.
- **Autostart installed a copy that could not start at all.** The installer copied whatever
  .exe was running, and a `dotnet build` .exe is only a launcher that needs `wsl-tray.dll`
  beside it. Copying it alone produced a boot copy that died every single time with
  `The application to execute does not exist: ...\wsl-tray.dll` - visible nowhere but the
  Windows event log, which is where it was eventually found, having failed at two boots.
  Only a self-contained build is installed now, the attempt is refused with a reason
  otherwise, and Settings says so if the installed copy looks like one of the broken ones.
- **Nothing stopped two destructive operations from running at once.** Each view guarded
  only its own buttons, so a clone in the Distros tab left Start live in the Overview - and
  starting a distro mid-export is exactly the corruption the clone's confirmation dialog
  promises to prevent. Two applies could likewise overlap, the second capturing the first's
  half-written `.wslconfig` as its rollback target and copying it over the only good backup.
  One process-wide gate now covers every long wsl.exe mutation, and the poll stands still
  while one runs instead of restarting a distro that was just stopped.
- **The GB/MB toggle changed the limit by a factor of 1024.** Introduced by the previous
  fix in this same file: the toggle re-ranged the field without converting the number, so
  `16GB` became `16MB` on one click, and `1536MB` clamped to `1024GB`. It converts now.
- **`wsl.exe` calls could block forever despite their timeout.** The wait for the output
  streams had a 2 second budget whose result was thrown away, and the read that followed had
  none at all - so a helper process holding the pipe open froze the polling thread for good,
  with the tray showing its last reading and no error anywhere.
- **A failed `wsl --shutdown` left never-verified limits on disk.** The user was told the
  apply had failed while the new `.wslconfig` stayed live for the next reboot. It is put
  back now.
- **A duplicate key in `.wslconfig` silently won.** The rewriter replaced the first
  `memory=` and left a later one in place; WSL and the tool both read the last, so an
  applied 16GB came back as whatever the stale line said.
- **Swap had no upper bound**, so a unit slip could ask WSL to provision a terabyte.
- **Reading `.wslconfig` could kill the app**: unguarded file IO ran straight out of the
  Limits nav-button click, so a locked file took the tray down with no message.
- **A failed sample blanked the container list** for a whole docker interval. No answer is
  not the same as no containers, so the previous list stands and the next tick retries.
- **The polling loop could die or run twice.** Its cancellation token was read from a field
  the restart nulls, which could null-deref into a task nobody observes, and a restart did
  not wait for the old loop, so two of them wrote the same rate baselines.
- **Settings could be silently reset.** A config that failed to load for any reason other
  than bad content was quarantined; a later save then wrote defaults over the real file; and
  the save itself was not atomic, so a truncated write could end portable mode. All three
  are handled, and an out-of-range poll interval no longer kills the loop.
- **The first run never ended.** Nothing wrote the config, so the window opened at every
  launch forever - with autostart on, at every boot.
- **A newer build started by hand did nothing** when an instance was already running, which
  with autostart on is always. The boot copy is refreshed before the single-instance check
  now, renaming the running one aside as Windows requires.
- Smaller ones: the tray icon's GDI handle was destroyed while `NotifyIcon` still held it;
  the tray menu's autostart checkmark never resynced with the Settings checkbox; "Shut down
  WSL VM" discarded its error; `StartDistro` probed `/bin/true`, which some minimal images
  do not have, making a healthy boot look like a failure and rolling back a good config;
  `df` output was read by column index and could shift on long device names; the number
  field's arrows discarded text just typed into it; Tab landed on the wrappers around the
  inputs instead of the inputs; the title bar closed the window on a right-click; a disabled
  dropdown kept a bright chevron; refreshing the distro list disarmed the delete
  confirmation; a hidden window still rendered on every poll; and a failed install still
  remembered its folder as the new default.
- **Every reading was thrown away while the distro was busy.** The sample script ended in
  `awk ... /proc/[0-9]*/statm`, and a process dying between the shell expanding that glob
  and awk opening the file makes awk exit 2. As the last command its exit code became the
  script's, so the whole sample was discarded and the distro showed `cpu 0.0 %  rss 0 B`.
  Under a fork storm that hit 236 of 300 runs - the tool was least reliable exactly when
  the machine was busy. Both `/proc` globs now go through `cat`, which keeps going.
- **A memory limit could be written back smaller than it was.** `memory=1536MB` loaded
  into a field capped at 1024, clamped silently, and Save wrote the reduced value to
  `.wslconfig`. The field's ceiling now follows its unit.
- **Saving settings tore the window down and built a new one**, losing the confirmation
  and dropping the user back on the Overview. The monitor re-reads the intervals from the
  config on every tick, so its loop is simply restarted now.
- **The tray icon was updated from the polling thread.** `NotifyIcon` and its GDI icons
  belong to the UI thread; every poll touched them from the wrong one. They are marshalled
  now, like the window already was.
- **A failed apply could take the app down with it.** `ApplyAndRestart` shells out, and a
  throw from there was read back as `t.Result` on the UI thread, rethrowing unhandled.
- **The rate baselines were a plain Dictionary** mutated from the UI thread while the
  polling thread walked it. It is a `ConcurrentDictionary` now.
- **VM CPU showed 0 % for one poll** after a start, stop or apply, because the figure was
  only ever taken from the first running distro even when that one had no baseline yet.
- **A valid config could be quarantined by a transient IO error.** Only content that
  actually fails to parse is moved aside now; anything else leaves the file alone.
- **A second install or clone could start while the first was running**, because the busy
  flag lived on a panel that is rebuilt on every tab switch.
- Deleting or exporting a distro no longer persists the folder field as the new default.
- **Autostart broke silently when the .exe moved.** The Run entry held whatever path the
  .exe had when the box was ticked, so deleting or moving the file left an entry that
  failed at every boot with nothing on screen. It now runs from a copy the tool owns, and
  an existing entry pointing somewhere else is migrated on the next start.
- **A memory value without a unit made the VM unbootable.** `memory=10` is ten bytes to
  WSL, and every distro then failed with
  `Wsl/Service/CreateInstance/CreateVm/E_UNEXPECTED`. Size fields are now a number plus
  a unit toggle, values are validated before writing (no bare numbers, minimum 512MB,
  never above physical RAM, never more processors than the machine has cores), and
  applying now verifies the VM restarts - restoring the previous config automatically
  if it does not.
- The distro hint text no longer stays on screen after a distro is selected.
- **A shell could not be opened at all.** Starting `wt.exe` or `wsl.exe` through
  `ShellExecuteEx` (`UseShellExecute = true`) failed with "access denied" from this
  process, while the plain `CreateProcess` path every other command uses works. Both
  launches now go through `CreateProcess`.
- **Containers from more than one distro were lost.** Each WSL distro runs its own
  dockerd, but the poll assigned the container list per distro instead of accumulating
  it, so only the last distro polled survived. Containers now carry the distro they came
  from, and the name is qualified as `<distro> / <container>` as soon as more than one
  distro reports containers.

### Changed
- **Frameless window with its own header bar.** There is no system title bar or border
  any more: the title, the minimise and close buttons and the content are one surface,
  the way a GNOME application looks. Dragging is handed to the window manager so snapping
  still works, and Windows 11 rounds the corners via `DWMWA_WINDOW_CORNER_PREFERENCE`.
- **Ubuntu desktop theme (Yaru).** Near-black greys, a lighter header bar, a distinct
  sidebar tone, and Ubuntu orange (`#E95420`) as the single accent. Readings are set in
  Ubuntu Mono where it is installed, so digits do not jitter between polls.
- **Overview in two columns.** The window is 1180x746 now, with distros and containers
  side by side instead of stacked, and the distro action buttons under the distro list.
  The other views keep a readable 760px measure rather than stretching with the window.
- **Distro state is a dot, not a word.** Green for running, red for stopped: in a list a
  colour reads faster than text. The word stays in the item, so the value is still there.
- **Text fields and dropdowns in the app's own chrome.** Both are now rounded surfaces
  with a border that turns to the accent on focus, at the same height as a button so a
  form row lines up. The dropdown keeps a real ComboBox underneath for keyboard handling
  and the popup, but its shell-drawn border and grey arrow are hidden behind ours.
- Fixed window size. Every view is laid out to fit exactly, so there is nothing to
  resize and nothing to scroll.
- Reworked the visual design: neutral greys with a single precise blue accent, rounded
  cards, sidebar entries with an accent rail instead of button chrome, taller list rows
  and owner-drawn headers and selection.
- Replaced the system `NumericUpDown`, `TextBox`, `ComboBox` and `CheckBox` with own
  controls. All of them drew chrome the app cannot colour - grey spinners, a hard system
  border, a grey dropdown arrow, and a tick in the Windows accent blue that fought the
  orange. The unit picker is a two-state toggle button, since a dropdown for GB/MB was
  overkill anyway.

### Removed
- Alert rules and Windows notifications.
- Live systemd/cgroup limits per distro. WSL2 has no per-distro limits; `.wslconfig`
  covers what is actually enforceable.
