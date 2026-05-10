# Future Feature Ideas

## Charging Mode Scheduler (target: v1.2.0)

User request: "schedule that lets user choose a time and a targeted charging mode. e.g. at 7am switch to Charge to 100% (so the surface is ready for being on the road)."

### Sleep-mode question

> Can the tray app switch charging modes if the surface is in sleep mode?

**No, not on its own.** When the system enters Modern Standby (S0 low-power) or true sleep (S3, rare on modern Surfaces), our tray process is suspended along with everything else — no code runs. Hibernate (S4) is even more terminal: the process is completely gone.

**But Windows Task Scheduler can wake the system.** Tasks created with the `WakeToRun` flag will:
1. Wake the device briefly when the trigger fires
2. Run the configured action (e.g., launch `SurfaceChargingTray.exe --set-mode 100-1day`)
3. Return to sleep after the task completes

This is exactly how Windows Update, defrag, and OneDrive sync run while you're asleep. Same mechanism, no admin required for per-user tasks.

### Proposed architecture

**1. CLI entry point on `SurfaceChargingTray.exe`**
   - `SurfaceChargingTray.exe --set-mode adaptive`
   - `SurfaceChargingTray.exe --set-mode 80`
   - `SurfaceChargingTray.exe --set-mode 100 --duration 1day`
   - `SurfaceChargingTray.exe --set-mode 100 --duration 1week`
   - `SurfaceChargingTray.exe --set-power efficient|balanced|performance`

   Runs the action headlessly (no tray spawned), exits when done. Reuses the existing `SurfaceController.SetMode` / `PowerMode.Set` paths so behavior is identical to a tray click.

**2. Schedule storage in `settings.ini`**
   ```ini
   [schedule.morning-charge]
   time=07:00
   days=Mon,Tue,Wed,Thu,Fri
   mode=100
   duration=1day
   wake_from_sleep=true
   enabled=true
   ```
   Multiple `[schedule.*]` sections allowed.

**3. Settings tab: "Schedule"**
   - List of existing schedules with enable toggles
   - Add/edit dialog: time picker + day-of-week checkboxes + mode dropdown + "wake device" toggle
   - On Save: install/update Windows Task Scheduler tasks under a `\SurfaceChargingTray\` folder

**4. Task Scheduler integration**
   - Use COM via `Schedule.Service` (no admin for per-user tasks)
   - One scheduled task per schedule entry, naming convention `SurfaceChargingTray_<schedule-name>`
   - Action: launch our exe with the matching `--set-mode` args
   - Trigger: daily/weekly with the user's time and days
   - Settings: `WakeToRun=true` if user opted in, `RunOnlyIfIdle=false`, `StartWhenAvailable=true` (so missed runs catch up after wake)

**5. Cleanup**
   - On uninstall (or when a schedule is deleted), remove the matching scheduled tasks
   - Settings dialog "Remove all schedules" button as escape hatch

### Open design questions

- **Does mode-switch survive sleep?** Need to test: if Task Scheduler wakes device at 7am and we set mode to 100%, does the system actually reach 100% before going back to sleep? Or does it need to stay awake?
- **Notification UX**: should the user see a toast when a scheduled mode-switch fires? Probably yes (silent state changes are confusing) — show a 3-second "Switched to Charge to 100% (1 day) — scheduled" balloon.
- **Daylight saving / timezone changes**: Task Scheduler handles this, but need to verify weekly schedules don't drift.
- **Multiple devices**: If a user has the same `settings.ini` on two Surfaces (synced via OneDrive), both would run the schedule. Probably fine, but worth noting.
- **Per-AC/DC awareness**: Should "switch to 100% at 7am" also depend on whether the device is plugged in? Probably yes — schedule a reminder via toast if the battery is low and the device isn't on AC.

### Effort estimate

~1–2 evenings of work:
- CLI parsing: ~30 min
- Settings UI tab + dialog: ~2 hours
- Task Scheduler COM wrapper: ~1 hour
- Persistence + load/save: ~30 min
- Testing scheduled triggers including wake-from-sleep: ~1 hour
