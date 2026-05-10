#Requires AutoHotkey v2.0
#SingleInstance Force

; Surface Charging Tray v1.1.1
; Right-click the tray icon for the menu, or use configurable hotkeys.
; v1.1.1 brings the AHK package's PowerShell scripts in line with the .exe
; build's multi-language UIA Name lookups so non-English Surface app
; installs can find the Battery & charging card. The .exe build has more
; (auto-discovery + ID caching + coalescing queue for rapid clicks); the
; AHK package keeps a smaller surface area on purpose. Note: the AHK
; package is being slowly phased out in favour of the .exe — see README.

Persistent()  ; keep the script running so the tray icon stays alive

scriptDir    := A_ScriptDir
psHidden     := scriptDir "\surface-set-mode-hidden.ps1"
psStatus     := scriptDir "\surface-get-status.ps1"
cacheFile    := scriptDir "\surface-state.json"
errorLogFile := scriptDir "\surface-error.log"
settingsFile := scriptDir "\surface-tray-settings.ini"
plugWhite    := scriptDir "\plug-white.ico"
plugBlack    := scriptDir "\plug-black.ico"
errorIcon    := scriptDir "\error-red.ico"

lastError := ""

; Default hotkey config (used on first run / when settings file is missing).
; AHK syntax: # = Win, ! = Alt, ^ = Ctrl, + = Shift
; Ctrl+Shift+digit is generally free on Windows; avoid Win+digit (taskbar
; slots) and Alt+Shift (input language switcher).
defaults := Map(
    "adaptive",        Map("enabled", "0", "key", "^+1"),
    "80",              Map("enabled", "0", "key", "^+2"),
    "100-1day",        Map("enabled", "0", "key", "^+3"),
    "100-1week",       Map("enabled", "0", "key", "^+4"),
    "cycle",           Map("enabled", "0", "key", "^+B"),
    "power-efficient", Map("enabled", "0", "key", "^+5"),
    "power-balanced",  Map("enabled", "0", "key", "^+6"),
    "power-perf",      Map("enabled", "0", "key", "^+7")
)

; Windows 11 Power mode overlay GUIDs (stable since Win10 1709).
; Same constants the .exe build uses — see PowerMode.cs.
PowerModeGuids := Map(
    "efficient",   "961cc777-2547-4f9d-8174-7d86181b8a7a",
    "balanced",    "00000000-0000-0000-0000-000000000000",
    "performance", "ded574b5-45a0-4f42-8737-46345c09c238"
)
hk := LoadSettings()  ; current hotkey config; mutated by settings dialog

; Persist defaults so the INI always exists alongside the script after a
; fresh first launch — makes the package self-contained and inspectable.
if !FileExist(settingsFile)
    SaveSettings(hk)

UpdateIconForTheme()                 ; pick correct color now
SetTimer(UpdateIconForTheme, 5000)   ; recheck every 5s for theme changes
ApplyMenuTheme()                     ; dark-mode the tray context menu
SetTimer(ApplyMenuTheme, 5000)       ; reapply on theme changes

; ----- Tray menu -----
A_TrayMenu.Delete()
A_TrayMenu.Add("Adaptive",                (*) => SetMode("adaptive"))
A_TrayMenu.Add("Limit to 80%",            (*) => SetMode("80"))
A_TrayMenu.Add("Charge to 100% (1 day)",  (*) => SetMode("100", "1day"))
A_TrayMenu.Add("Charge to 100% (1 week)", (*) => SetMode("100", "1week"))
A_TrayMenu.Add()  ; separator

; Windows Power mode submenu — three modes via direct powrprof.dll calls.
; Hidden if the API isn't supported (very old Windows builds, server SKUs).
powerMenu := Menu()
powerMenu.Add("Best power efficiency", (*) => SetPowerMode("efficient"))
powerMenu.Add("Balanced",              (*) => SetPowerMode("balanced"))
powerMenu.Add("Best performance",      (*) => SetPowerMode("performance"))
A_TrayMenu.Add("Windows Power mode", powerMenu)
if !IsPowerModeSupported() {
    try A_TrayMenu.Disable("Windows Power mode")
}

A_TrayMenu.Add()  ; separator
A_TrayMenu.Add("Refresh status",          (*) => RefreshFromApp())
A_TrayMenu.Add("Open Surface app",        (*) => OpenSurfaceApp())
A_TrayMenu.Add("Settings...",             (*) => ShowSettings())
A_TrayMenu.Add("Run at Windows login",    (*) => ToggleAutoStart())
A_TrayMenu.Add("Show last error",         (*) => ShowLastError())
A_TrayMenu.Add("Exit",                    (*) => ExitApp())

UpdateAutoStartMenuCheck()
UpdatePowerCheckMarks()
SetTimer(UpdatePowerCheckMarks, 5000)  ; reflect external changes (Settings UI, AC/DC switch)

ApplyHotkeys()
LoadCacheToTray()

; ----------------------------------------------------------------------
; Mode switching
; ----------------------------------------------------------------------

SetMode(mode, duration := "") {
    A_IconTip := "Surface Charging: switching..."
    cmd := 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File "' psHidden '" -Mode ' mode
    if (duration != "")
        cmd .= ' -Duration ' duration

    exitCode := 1
    try
        exitCode := RunWait(cmd, , "Hide")
    catch as e {
        ReportError("Couldn't run PowerShell: " e.Message)
        return
    }

    if (exitCode != 0) {
        ReportError(ReadErrorLog())
        return
    }

    ClearError()
    LoadCacheToTray()
}

RefreshFromApp() {
    A_IconTip := "Surface Charging: refreshing..."
    cmd := 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File "' psStatus '"'

    exitCode := 1
    try
        exitCode := RunWait(cmd, , "Hide")
    catch as e {
        ReportError("Couldn't run PowerShell: " e.Message)
        return
    }

    if (exitCode != 0) {
        ReportError(ReadErrorLog())
        return
    }

    ClearError()
    LoadCacheToTray()
}

CycleMode() {
    ; Reads current mode from cache, picks the next one in the cycle.
    ; Order: adaptive -> 80 -> 100/1day -> 100/1week -> adaptive ...
    state := ParseCache()
    mode := state.mode
    duration := state.duration

    if (mode = "adaptive")
        SetMode("80")
    else if (mode = "80")
        SetMode("100", "1day")
    else if (mode = "100" && duration = "1day")
        SetMode("100", "1week")
    else
        SetMode("adaptive")
}

OpenSurfaceApp() {
    Run('explorer shell:appsFolder\Microsoft.SurfaceHub_8wekyb3d8bbwe!App')
}

; ----------------------------------------------------------------------
; Error surfacing
; ----------------------------------------------------------------------

ReportError(msg) {
    global lastError
    lastError := msg
    A_IconTip := "Surface Charging: ERROR — right-click 'Show last error'"
    if FileExist(errorIcon)
        try A_TrayMenu.SetIcon("Show last error", errorIcon)
    TrayTip("Surface charging tray", SubStr(msg, 1, 200), 0x10)
}

ClearError() {
    global lastError
    lastError := ""
    try A_TrayMenu.RemoveIcon("Show last error")
}

ReadErrorLog() {
    if (FileExist(errorLogFile)) {
        try
            return Trim(FileRead(errorLogFile))
    }
    return "Unknown error (no error log was written)."
}

ShowLastError() {
    if (lastError = "")
        MsgBox("No errors recorded. Last action succeeded.", "Surface tray", 0x40)
    else
        MsgBox(lastError, "Last error", 0x10)
}

; ----------------------------------------------------------------------
; Cache + tray tooltip / check marks
; ----------------------------------------------------------------------

ParseCache() {
    out := { mode: "", duration: "" }
    if (FileExist(cacheFile)) {
        try {
            content := FileRead(cacheFile)
            if (RegExMatch(content, '"Mode"\s*:\s*"([^"]+)"', &m1))
                out.mode := m1[1]
            if (RegExMatch(content, '"Duration"\s*:\s*"([^"]+)"', &m2))
                out.duration := m2[1]
        }
    }
    return out
}

LoadCacheToTray() {
    s := ParseCache()
    A_IconTip := "Surface Charging: " ModeToLabel(s.mode, s.duration)

    A_TrayMenu.Uncheck("Adaptive")
    A_TrayMenu.Uncheck("Limit to 80%")
    A_TrayMenu.Uncheck("Charge to 100% (1 day)")
    A_TrayMenu.Uncheck("Charge to 100% (1 week)")

    switch s.mode {
        case "adaptive":
            A_TrayMenu.Check("Adaptive")
        case "80":
            A_TrayMenu.Check("Limit to 80%")
        case "100":
            if (s.duration = "1day")
                A_TrayMenu.Check("Charge to 100% (1 day)")
            else if (s.duration = "1week")
                A_TrayMenu.Check("Charge to 100% (1 week)")
    }
}

ModeToLabel(mode, duration) {
    switch mode {
        case "adaptive":
            return "Adaptive"
        case "80":
            return "Limit to 80%"
        case "100":
            if (duration = "1day")
                return "Charge to 100% (1 day)"
            else if (duration = "1week")
                return "Charge to 100% (1 week)"
            else
                return "Charge to 100%"
        default:
            return "?"
    }
}

; ----------------------------------------------------------------------
; Windows Power mode (Best efficiency / Balanced / Best performance)
;
; Uses powrprof.dll directly via DllCall — no PowerShell hop. The
; PowerSet*PowerMode functions need a POINTER to a 16-byte GUID buffer
; (NOT the GUID by value); passing by value AVs on ARM64 because the
; calling convention treats large structs differently. Same fix the
; .exe build uses (see PowerMode.cs).
;
; PowerGetEffectiveOverlayScheme returns the currently active overlay
; (accounts for AC/DC state). Returns Empty GUID when on Balanced
; (the "no overlay" default state).
;
; PowerSetUserConfigured{AC,DC}PowerMode is the modern (Win10 1809+)
; per-AC/DC pair. We set BOTH on each click so the choice persists
; across plug-in/unplug, matching the unified tray-menu mental model.
; ----------------------------------------------------------------------

GuidStringToBuffer(guidStr) {
    buf := Buffer(16, 0)
    rc := DllCall("ole32\CLSIDFromString", "WStr", "{" guidStr "}", "Ptr", buf, "UInt")
    if (rc != 0)
        throw Error("Invalid GUID: " guidStr)
    return buf
}

GuidBufferToString(buf) {
    out := Buffer(80, 0)
    if (DllCall("ole32\StringFromGUID2", "Ptr", buf, "Ptr", out, "Int", 40) = 0)
        return ""
    s := StrGet(out, "UTF-16")
    ; Strip the surrounding braces from "{xxxxxxxx-...}".
    return SubStr(s, 2, StrLen(s) - 2)
}

IsPowerModeSupported() {
    try {
        buf := Buffer(16, 0)
        return DllCall("powrprof\PowerGetEffectiveOverlayScheme", "Ptr", buf, "UInt") = 0
    } catch {
        return false
    }
}

GetPowerMode() {
    try {
        buf := Buffer(16, 0)
        if (DllCall("powrprof\PowerGetEffectiveOverlayScheme", "Ptr", buf, "UInt") != 0)
            return ""
        return StrLower(GuidBufferToString(buf))
    } catch {
        return ""
    }
}

SetPowerMode(modeName) {
    global PowerModeGuids
    if !PowerModeGuids.Has(modeName) {
        ReportError("Unknown Power mode: " modeName)
        return false
    }
    try {
        buf := GuidStringToBuffer(PowerModeGuids[modeName])
        rcAc := DllCall("powrprof\PowerSetUserConfiguredACPowerMode", "Ptr", buf, "UInt")
        rcDc := DllCall("powrprof\PowerSetUserConfiguredDCPowerMode", "Ptr", buf, "UInt")
        if (rcAc != 0 || rcDc != 0) {
            ReportError("Failed to set Windows Power mode to " modeName " (ac=" rcAc ", dc=" rcDc ")")
            return false
        }
    } catch as e {
        ReportError("Failed to set Windows Power mode: " e.Message)
        return false
    }
    UpdatePowerCheckMarks()
    return true
}

UpdatePowerCheckMarks() {
    global powerMenu
    cur := GetPowerMode()
    try powerMenu.Uncheck("Best power efficiency")
    try powerMenu.Uncheck("Balanced")
    try powerMenu.Uncheck("Best performance")
    switch cur {
        case "961cc777-2547-4f9d-8174-7d86181b8a7a":
            try powerMenu.Check("Best power efficiency")
        case "00000000-0000-0000-0000-000000000000":
            try powerMenu.Check("Balanced")
        case "ded574b5-45a0-4f42-8737-46345c09c238":
            try powerMenu.Check("Best performance")
        ; Anything else (OEM custom overlay) leaves all three unchecked.
    }
}

; ----------------------------------------------------------------------
; Auto-start at Windows login
; ----------------------------------------------------------------------

GetStartupShortcutPath() {
    return A_Startup "\Surface Charging Tray.lnk"
}

IsAutoStartInstalled() {
    return FileExist(GetStartupShortcutPath()) ? true : false
}

UpdateAutoStartMenuCheck() {
    if IsAutoStartInstalled()
        A_TrayMenu.Check("Run at Windows login")
    else
        A_TrayMenu.Uncheck("Run at Windows login")
}

ToggleAutoStart() {
    sp := GetStartupShortcutPath()
    if IsAutoStartInstalled() {
        try {
            FileDelete(sp)
            TrayTip("Surface charging tray", "Auto-start disabled.", 0x1)
        } catch as e {
            ReportError("Couldn't remove startup shortcut: " e.Message)
        }
    } else {
        try {
            FileCreateShortcut(
                A_ScriptFullPath,           ; Target  (the .ahk file)
                sp,                          ; LinkFile (in startup folder)
                scriptDir,                   ; WorkingDir
                "",                          ; Args
                "Surface Charging Tray",     ; Description
                plugWhite                    ; IconFile
            )
            TrayTip("Surface charging tray", "Will start with Windows.", 0x1)
        } catch as e {
            ReportError("Couldn't create startup shortcut: " e.Message)
        }
    }
    UpdateAutoStartMenuCheck()
}

UpdateIconForTheme() {
    try
        light := RegRead("HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme")
    catch
        light := 0
    icon := (light = 1) ? plugBlack : plugWhite
    if FileExist(icon)
        TraySetIcon(icon)
}

IsAppsDarkMode() {
    ; AppsUseLightTheme governs the colour theme of regular app windows
    ; (different from SystemUsesLightTheme, which controls the taskbar).
    try {
        light := RegRead("HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme")
        return (light = 0)
    } catch {
        return false
    }
}

ApplyMenuTheme() {
    ; Enables the system's dark-mode theme for popup menus rendered by this
    ; process (the tray right-click menu, in particular). Uses the same two
    ; undocumented uxtheme.dll ordinals (135 = SetPreferredAppMode,
    ; 136 = FlushMenuThemes) that Edge / File Explorer use. Stable since
    ; Windows 10 1903.
    static cachedDark := -1
    isDark := IsAppsDarkMode() ? 1 : 0
    if (isDark = cachedDark)
        return
    cachedDark := isDark
    try {
        hUx := DllCall("LoadLibrary", "Str", "uxtheme.dll", "Ptr")
        if !hUx
            return
        setMode := DllCall("GetProcAddress", "Ptr", hUx, "Ptr", 135, "Ptr")
        if setMode
            DllCall(setMode, "Int", isDark ? 2 : 3)   ; 2 = ForceDark, 3 = ForceLight
        flushMenus := DllCall("GetProcAddress", "Ptr", hUx, "Ptr", 136, "Ptr")
        if flushMenus
            DllCall(flushMenus)
    }
}

; ----------------------------------------------------------------------
; Hotkey settings (load / save / apply / GUI)
; ----------------------------------------------------------------------

LoadSettings() {
    out := Map()
    for action, def in defaults {
        enabled := def["enabled"]
        key     := def["key"]
        if FileExist(settingsFile) {
            try {
                v := IniRead(settingsFile, "hotkeys", action "_enabled", def["enabled"])
                enabled := v
            }
            try {
                v := IniRead(settingsFile, "hotkeys", action "_key", def["key"])
                key := v
            }
        }
        out[action] := Map("enabled", enabled, "key", key)
    }
    return out
}

SaveSettings(cfg) {
    for action, m in cfg {
        IniWrite(m["enabled"], settingsFile, "hotkeys", action "_enabled")
        IniWrite(m["key"],     settingsFile, "hotkeys", action "_key")
    }
}

ApplyHotkeys() {
    ; Disable all known hotkeys first, then re-register the enabled ones.
    for action, m in hk {
        try Hotkey(m["key"], "Off")
    }
    actionToCallback := Map(
        "adaptive",        (*) => SetMode("adaptive"),
        "80",              (*) => SetMode("80"),
        "100-1day",        (*) => SetMode("100", "1day"),
        "100-1week",       (*) => SetMode("100", "1week"),
        "cycle",           (*) => CycleMode(),
        "power-efficient", (*) => SetPowerMode("efficient"),
        "power-balanced",  (*) => SetPowerMode("balanced"),
        "power-perf",      (*) => SetPowerMode("performance")
    )
    for action, m in hk {
        if (m["enabled"] = "1" && m["key"] != "") {
            try Hotkey(m["key"], actionToCallback[action], "On")
        }
    }
}

ShowSettings() {
    isDark := IsAppsDarkMode()

    if isDark {
        ; Let standard controls render with dark theme. Ordinal 135 of uxtheme.dll
        ; is the SetPreferredAppMode entry point (undocumented but stable since Win10).
        try {
            hUxtheme := DllCall("LoadLibrary", "Str", "uxtheme.dll", "Ptr")
            if hUxtheme
                try DllCall(DllCall("GetProcAddress", "Ptr", hUxtheme, "Ptr", 135, "Ptr"), "Int", 2)
        }
    }

    g := Gui("+AlwaysOnTop +ToolWindow", "Surface Charging Tray — Settings")
    g.MarginX := 14
    g.MarginY := 14

    if isDark {
        g.BackColor := "0x1F1F1F"
        g.SetFont("s10 cWhite")
        ; Dark title bar (DWMWA_USE_IMMERSIVE_DARK_MODE = 20 on Win11, 19 on older Win10)
        try DllCall("dwmapi\DwmSetWindowAttribute", "Ptr", g.Hwnd, "Int", 20, "Int*", 1, "Int", 4)
        try DllCall("dwmapi\DwmSetWindowAttribute", "Ptr", g.Hwnd, "Int", 19, "Int*", 1, "Int", 4)
    } else {
        g.SetFont("s10")
    }
    grayColor := isDark ? "cBBBBBB" : "c606060"

    g.Add("Text", "w380", "Hotkeys (uncheck to disable). Click a hotkey field and press the keys you want to use.")
    g.Add("Text", "w380 " grayColor, "Tip: avoid Alt+Shift (Windows uses it for input language) and Win+digit (taskbar slots).")

    rows := [
        { action: "adaptive",        label: "Adaptive" },
        { action: "80",              label: "Limit to 80%" },
        { action: "100-1day",        label: "Charge to 100% (1 day)" },
        { action: "100-1week",       label: "Charge to 100% (1 week)" },
        { action: "cycle",           label: "Cycle through charging modes" },
        { action: "power-efficient", label: "Power: Best power efficiency" },
        { action: "power-balanced",  label: "Power: Balanced" },
        { action: "power-perf",      label: "Power: Best performance" }
    ]

    controls := Map()
    y := 70
    for r in rows {
        m := hk[r.action]
        cb := g.Add("Checkbox", "x14 y" y " w220", r.label)
        cb.Value := (m["enabled"] = "1") ? 1 : 0
        hkCtrl := g.Add("Hotkey", "x240 y" (y - 3) " w160", m["key"])
        controls[r.action] := { enabled: cb, key: hkCtrl }
        y += 32
    }

    btnSave   := g.Add("Button", "x14 y" (y + 12) " w90 Default", "Save")
    btnCancel := g.Add("Button", "x114 y" (y + 12) " w90", "Cancel")

    ; Footer (smaller font)
    g.SetFont("s8 " grayColor)
    g.Add("Link", "x14 y" (y + 60) " w380",
        'If you liked this, consider a <a href="https://ko-fi.com/keyokku">tip (ko-fi)</a>.')

    btnSave.OnEvent("Click", (*) => SaveAndClose())
    btnCancel.OnEvent("Click", (*) => g.Destroy())

    g.OnEvent("Close", (*) => g.Destroy())
    g.Show("AutoSize")

    SaveAndClose() {
        global hk
        for action, c in controls {
            hk[action]["enabled"] := c.enabled.Value ? "1" : "0"
            hk[action]["key"]     := c.key.Value
        }
        SaveSettings(hk)
        ApplyHotkeys()
        g.Destroy()
        TrayTip("Surface charging tray", "Hotkeys updated.", 0x1)
    }
}
