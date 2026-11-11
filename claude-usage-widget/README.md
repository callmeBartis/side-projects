# claude-usage-widget

Shows Claude plan usage (5-hour session and 7-day weekly limit) inside the Windows 11
taskbar, pushes threshold alerts to the iPhone via ntfy.sh, and publishes a status
message an iOS shortcut or Scriptable widget can read.

## Install

Requirements: Windows 11 and [Claude Code](https://claude.com/claude-code) installed and
logged in with a Claude subscription (Pro/Max) on the same Windows user.

1. Download `claude-usage-widget.exe` and double-click it.
2. The .exe is not code-signed, so SmartScreen may say "Windows protected your PC":
   click **More info**, then **Run anyway**.
3. It installs itself (no admin rights) to `%LOCALAPPDATA%\Programs\claude-usage-widget`,
   adds autostart and a Start menu entry, and shows a short welcome message. The
   downloaded file can be deleted afterwards.
4. Optional phone alerts: click the widget, tick **Handy-Benachrichtigungen (ntfy)**.
   That creates your personal topic, copies it and explains the phone setup. Off by
   default, so nothing is sent anywhere until you turn it on.

Updating: double-click a newer .exe, it replaces the running copy.
Uninstalling: click the widget, then **Deinstallieren**.

## How it works

- Every 5 min it reads the OAuth token Claude Code keeps in `~/.claude/.credentials.json`
  and calls `GET https://api.anthropic.com/api/oauth/usage` (header
  `anthropic-beta: oauth-2025-04-20`). This is the **undocumented** endpoint behind
  Claude Code's `/usage`; it may change or disappear without notice.
- It never refreshes the token itself. Refreshing rotates the refresh token and would log
  Claude Code out. If Claude Code has not run for ~8 h the widget greys out until it does.
- The taskbar widget is a layered child window of `Shell_TrayWnd`, placed left of
  `TrayNotifyWnd` (the trick TrafficMonitor uses; Windows 11 has no public API for this).
- Alerts go to the ntfy topic `<topic>`, status to `<topic>-status`, throttled to one
  message per 10 min on change plus one per hour (free ntfy.sh tier, 12 h message cache).

## Build

```powershell
dotnet publish -c Release -o publish   # -> publish\claude-usage-widget.exe (~47 MB, self-contained)
tools\make-icon.ps1 -Out app.ico       # only when changing the icon
```

Self-test of the alert logic: `claude-usage-widget.exe --selftest`

A `dotnet build` output (exe + dll) runs in place without installing itself; only the
published single-file .exe self-installs.

Click the widget for the menu: refresh, open claude.ai usage page, copy ntfy topic,
open settings, start with Windows, exit, uninstall.

## Config

`%APPDATA%\claude-usage-widget\config.json` (created on first start; restart after editing):

| Key | Default | Meaning |
| --- | --- | --- |
| `ntfyEnabled` | `false` | Phone alerts and status via ntfy.sh, toggled from the menu |
| `ntfyTopic` | empty | Random name created on first opt-in. Public on ntfy.sh, the random name is the only protection. |
| `sessionAlerts` | `[50, 80, 95]` | Session % that trigger a push |
| `weeklyAlerts` | `[30, 40, ..., 100]` | Weekly % that trigger a push |
| `pollSeconds` | `300` | API poll interval |
| `statusMinutes` | `10` | Min. minutes between status messages |

`state.json` next to it remembers which alerts were sent, so restarts do not re-ping.
An alert rearms when the percentage drops below it (window reset).

## iPhone

1. **Push:** turn on phone alerts in the widget menu, then in the ntfy app subscribe to
   `<topic>` on ntfy.sh.
2. **Shortcut:** "Get Contents of URL"
   `https://ntfy.sh/<topic>-status/json?poll=1&since=latest`, then
   "Get Dictionary Value" `message`, then "Show Result".
3. **Home screen widget:** Scriptable app, new script with `ios/scriptable-widget.js`,
   set `TOPIC`, add a small Scriptable widget and pick the script. iOS decides the
   refresh rate (roughly every 15+ min).

## Gotchas

- The widget shares an input queue with explorer (cross-process child window). Never
  block the UI thread, it would freeze the taskbar.
- Without the Windows 8+ `supportedOS` entry in `app.manifest`, creating the layered
  child window fails with "Error creating window handle".
- `/raw` on ntfy collapses newlines; use `/json` and read `message`.
- The usage endpoint answers 429 with `Retry-After: 0`. Taking that literally retried in a
  tight loop and kept the limit alive; 429s now back off 5, 10, 20, 30 min.
- That rate limit is per account and shared with Claude Code, which polls the same
  endpoint from every open session. 429s are normal with several sessions open. The
  widget therefore keeps the last values in `state.json`, shows them dimmed while it
  cannot fetch, and skips the API on restarts within the poll interval.
- Claude Code's status line JSON reportedly carries `rate_limits` too (no API call), but
  the VS Code extension does not run the status line, so it is not used here.
