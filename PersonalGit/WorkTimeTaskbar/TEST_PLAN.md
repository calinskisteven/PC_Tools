# Manual smoke tests (Windows)

Close Work Timer v1.4 before starting v1.6. Make a copy of `%LocalAppData%\WorkTimeTimerLive\timers.txt` first.

1. **Stay at the PC:** Resume, pause, keep using the mouse or keyboard. At 2:59 remain paused with no popup; at approximately 3:00 get ONE reminder. (The UI polling interval is 0.5 seconds.)
2. **Return early:** Resume, pause, leave the mouse/keyboard untouched for 35 seconds, then move mouse. Expect the reminder immediately, before three minutes have elapsed.
3. **Stay away:** Pause and walk away. Even after three minutes, no popup until you return and use the PC.
4. **Snooze:** Set `Remind me again in` to 5 (or another whole number). Click Snooze. Main timer stays paused; no reminder until the snooze deadline. When actively using the PC, expect another reminder on expiry.
5. **Away during snooze:** Snooze and walk away beyond its expiry. Popup should wait for your next input.
6. **Resume/Stop:** Both dismiss an open popup. Stop should not start repeated reminders. Resume, then Pause starts a new three-minute grace period.
7. **Layout:** At Windows 100%, 150%, and 200% scaling (if possible), resize main window to the minimum, add a very long project name, and open the reminder and rename dialogs. No overlapping text/buttons; truncated active name should show fully on hover.
8. **Logging:** Add project A, set active, Resume, work ~30 sec, Pause. Use Open Work Log. Expect the current Monday–Sunday weekly CSV and `_sessions.csv`; durations should be close to 30 sec. Resume with project B, work, stop. Expect separate project lines, session intervals, and TOTAL.
9. **Durability:** While running wait 20 sec; close app and reopen. The CSV should retain the session and not count closed downtime. Backup both `timers.txt` and `worklog-journal.json`.
10. **Check rollover:** If you work over Sunday midnight into Monday, the session should be split across two weekly files. (No need to change PC clock to test this manually.)

## Version 1.6 responsive-layout checks

11. Resize gradually from 1200 to 450 pixels wide. At the wide size, the project list and actions must be side by side, at medium size stacked with a two-column button grid, and at the smallest size stacked with a one-column button grid. All eight project buttons remain accessible.
12. Reduce window height to ~420 px. Scroll down with mouse wheel or vertical scrollbar: all project buttons AND work-log footer must be reachable, with no overlap/clipping. Increase height again; page should use the available height.
13. At medium width toggle 100/150/200% Windows display scaling and reopen. Confirm button labels, timer digits, project labels, and checkboxes do not run into one another.
14. Set a long project name and expected duration; the project list should ellipsize, and hovering the item should reveal its full text. Verify keyboard Tab, Enter, and Space still activate buttons.
15. Verify pause reminder, tray controls, active task selection, work-log CSV, and old timers.txt persist as in tests 1–10.
