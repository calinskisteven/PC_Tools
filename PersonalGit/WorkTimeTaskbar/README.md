> Build fix in v1.6.1: removed the public design-time `SoftCard.Accent` property that triggered .NET 10 WinForms analyzer error WFO1000.

# Work Timer v1.6.1 — responsive layout and refreshed design

This is a reconstructed update of the earlier Work Timer source, not a byte-for-byte recreation of the user's v1.3 executable. It keeps the live taskbar icon, tray right-click Pause/Resume, multiple projects, expected durations, and timer state file. **The Windows GUI has not been compiled or run in this environment**; please report any build error with its complete text.

## Build on Windows

1. Extract this ZIP, install the **.NET 10 SDK** (Windows x64), then open PowerShell inside the extracted folder.
2. Run `Set-ExecutionPolicy -Scope Process Bypass` and `./Build-Windows.ps1`.
3. Your self-contained EXE is `bin\Release\net10.0-windows\win-x64\publish\WorkTimeTaskbar.exe`.
4. Close the old version. Back up `%LocalAppData%\WorkTimeTimerLive\timers.txt` first. Keep the old EXE for rollback. Never use two versions simultaneously on the same timer data.

## Smart paused reminders

- **0–3 minutes after a pause:** no reminder while you remain at the PC (the timer stays paused). After **3 minutes**, if you are actively using your PC, a reminder appears.
- **Early return:** if the PC gets **at least 30 seconds of no mouse/keyboard input** while the timer is paused, then you return and start using the PC again, you get a reminder immediately — even during the initial three-minute grace period, unless you are snoozed.
- If you remain away, no popup opens until you return and use the PC.
- **Snooze defaults to 5 minutes, editable** in the popup (1–1,440 minutes). Closing the popup snoozes it instead of ignoring it. When snooze expires, it reminds again if you are active, or upon return if you are away.
- Resuming or pressing Stop cancels the reminder. Start-up is paused and receives the same 3-minute grace period, but explicit Stop ends reminder checks until the timer is resumed and manually paused again.
- The reminder checks system-wide Windows idle input (`GetLastInputInfo`); it does not inspect your work content.

## UI

The main timer, state, and current project each occupy their own row. Version 1.6 makes the entire main page vertically scrollable and dynamically reflows the controls using window width:

- **Wide windows:** four main actions in a row, two columns of settings, side-by-side project list/actions.
- **Medium windows:** two main actions per row; project list and project buttons stack in a single vertical column. Project buttons retain two columns if wide enough.
- **Narrow windows:** actions become one column, settings become one column, and the project buttons become one column. A vertical scrollbar appears if the full content is taller than the window.
- Explicit minimum heights prevent the project buttons from painting over one another when the window is short. Project names and footer text ellipsize instead of overlapping. Hover over a project to see the full name and duration.
- Rounded, double-buffered cards and buttons, a clearer two-line header, and slightly improved text contrast. The window minimum is now approximately 440 x 420 at 100% DPI; the initial window is larger, but can be freely resized.

The **reminder popup behavior, 3-minute grace period, 30-second away/return detection, default 5-minute snooze, tray controls, original timer data, and CSV journal** remain from v1.5. System tray right-click offers Pause/Resume, Snooze, Show, and Exit. No automatic resume of your paused timer.

## Automatic weekly project CSVs

Export folder: **`C:\dev\WorkLog`** (the full absolute path; not `C:dev\WorkLog`). The app will try to create it. The `Open Work Log` button opens the folder in File Explorer. The bottom of the app turns red and displays an error if the journal or export can't be saved. If the journal can't be loaded, the app will NOT overwrite it with a blank one.

A week runs **Monday through Sunday in the PC's local timezone**. For each week with tracked work, the app writes two UTF-8 CSV files:

- `WorkLog_20260921_20260927.csv`: one row per project, total HH:MM:SS, decimal hours, number of tracked sessions, plus a TOTAL row.
- `WorkLog_20260921_20260927_sessions.csv`: each project interval with local start/end timestamp and duration.

Example weekly summary format:

```csv
Week Start,Week End,Project,Duration (HH:MM:SS),Decimal Hours,Sessions
"2026-09-21","2026-09-27","Cosmic Motors","03:25:12",3.420,2
"2026-09-21","2026-09-27","VM1-S","01:30:00",1.500,1
"2026-09-21","2026-09-27","TOTAL","04:55:12",4.920,3
```

**Logging starts with the new version**: older accumulated hours in `timers.txt` are not automatically assigned to a historical week. The active project's work is recorded while the *main* timer runs; main timer hours without a task are exported as `Unassigned`. Pause/Stop finish the session, and choosing or renaming the active project starts a new labeled segment. Resetting timers never silently deletes historical weekly work logs. Work that crosses Monday 00:00 is split across the two weeks. CSVs are updated at 15-second checkpoints, on pause, project switch and close, so there is no need to wait until Sunday; the summary is regenerated from the source journal and manually editing exports is not recommended.

The source-of-truth journal is `%LocalAppData%\WorkTimeTimerLive\worklog-journal.json`. The original timer state remains `%LocalAppData%\WorkTimeTimerLive\timers.txt`. Back up both after starting v1.6. A crash may lose up to ~15 seconds of current-session logging from the journal; it doesn't backfill downtime on next startup.
