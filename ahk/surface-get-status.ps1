# Reads the Surface app's current charging mode and writes the cache.
# Hidden launch + close pattern, READ-ONLY: does not call Select() on any element.
# Writes surface-state.json on success and surface-error.log on failure.

$ErrorActionPreference = 'Stop'
$errorLog = Join-Path $PSScriptRoot 'surface-error.log'

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

Add-Type @"
using System;
using System.Runtime.InteropServices;
public class W32 {
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int n);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int w, int z, uint f);
}
"@

$SW_HIDE = 0

function Find-SurfaceProcess {
    Get-Process |
        Where-Object { $_.MainWindowTitle -eq 'Surface' -and $_.MainWindowHandle -ne 0 } |
        Select-Object -First 1
}

function Hide-Window([IntPtr]$hwnd) {
    [W32]::SetWindowPos($hwnd, [IntPtr]::Zero, -32000, -32000, 0, 0, 0x0001 -bor 0x0004 -bor 0x0010) | Out-Null
    [W32]::ShowWindow($hwnd, $SW_HIDE) | Out-Null
}

function Wait-For([scriptblock]$test, [int]$timeoutMs = 10000, [int]$pollMs = 100) {
    $deadline = (Get-Date).AddMilliseconds($timeoutMs)
    while ((Get-Date) -lt $deadline) {
        $r = & $test
        if ($r) { return $r }
        Start-Sleep -Milliseconds $pollMs
    }
    return $null
}

$proc = $null
$launchedByUs = $false

try {
    # Always close any existing Surface app instance and relaunch fresh
    # so we land on the home page where the Battery & charging card lives.
    $proc = Find-SurfaceProcess
    if ($proc) {
        try {
            $proc.CloseMainWindow() | Out-Null
            if (-not $proc.WaitForExit(2500)) { $proc.Kill() }
        } catch { }
        Start-Sleep -Milliseconds 300
        $proc = $null
    }

    $launchedByUs = $true
    $startApp = Get-StartApps | Where-Object { $_.Name -eq 'Surface' } | Select-Object -First 1
    $aumid = if ($startApp) { $startApp.AppID } else { 'Microsoft.SurfaceHub_8wekyb3d8bbwe!App' }
    Start-Process "shell:appsFolder\$aumid" | Out-Null
    $proc = Wait-For { Find-SurfaceProcess } 10000 50
    if (-not $proc) {
        throw "Surface app didn't launch within 10 seconds. Is the Surface app installed and working?"
    }
    Hide-Window $proc.MainWindowHandle

    $win = [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle)
    if (-not $win) { throw "Could not bind UI Automation to the Surface window." }

    $bcCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, 'Battery & charging')
    $bcGroup = Wait-For { $win.FindFirst([System.Windows.Automation.TreeScope]::Subtree, $bcCond) } 10000 200
    if (-not $bcGroup) {
        throw "'Battery & charging' card not found. Your Surface model or app version may not support the three charging modes."
    }

    try {
        $expPat = $bcGroup.GetCurrentPattern(
            [System.Windows.Automation.ExpandCollapsePattern]::Pattern)
        if ($expPat.Current.ExpandCollapseState -ne [System.Windows.Automation.ExpandCollapseState]::Expanded) {
            $expPat.Expand()
            Start-Sleep -Milliseconds 400
        }
    } catch { }

    $names = @('Adaptive', 'Limit to 80%', 'Charge to 100%')
    $selectedMode = $null
    foreach ($n in $names) {
        $cType = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::RadioButton)
        $cName = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty, $n)
        $cond  = New-Object System.Windows.Automation.AndCondition($cType, $cName)
        $rb = $win.FindFirst([System.Windows.Automation.TreeScope]::Subtree, $cond)
        if ($rb) {
            try {
                $sp = $rb.GetCurrentPattern(
                    [System.Windows.Automation.SelectionItemPattern]::Pattern)
                if ($sp.Current.IsSelected) { $selectedMode = $n; break }
            } catch { }
        }
    }
    if (-not $selectedMode) {
        throw "None of the three charging-mode radios appear selected. Your Surface app build may differ from the one this tool was written for."
    }

    $selectedDuration = $null
    if ($selectedMode -eq 'Charge to 100%') {
        $cCombo = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
            'DurationSelectionComboBox')
        $combo = $win.FindFirst([System.Windows.Automation.TreeScope]::Subtree, $cCombo)
        if ($combo) {
            try {
                $sp = $combo.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern)
                $sel = $sp.Current.GetSelection()
                if ($sel.Length -gt 0) { $selectedDuration = $sel[0].Current.Name }
            } catch { }
        }
    }

    $modeKey = switch ($selectedMode) {
        'Adaptive'        { 'adaptive' }
        'Limit to 80%'    { '80' }
        'Charge to 100%'  { '100' }
        default           { $null }
    }
    $durKey = switch ($selectedDuration) {
        '1 day'   { '1day' }
        '1 week'  { '1week' }
        default   { $null }
    }

    $state = [PSCustomObject]@{
        Mode      = $modeKey
        Duration  = if ($modeKey -eq '100') { $durKey } else { $null }
        Timestamp = (Get-Date).ToString('s')
    }
    $cachePath = Join-Path $PSScriptRoot 'surface-state.json'
    $state | ConvertTo-Json | Out-File $cachePath -Encoding utf8

    if ($launchedByUs) {
        Start-Sleep -Milliseconds 200
        try {
            $proc.CloseMainWindow() | Out-Null
            if (-not $proc.WaitForExit(2000)) { $proc.Kill() }
        } catch { }
    }

    if (Test-Path $errorLog) { Remove-Item $errorLog -Force }

    Write-Host "Mode=$modeKey Duration=$durKey"
}
catch {
    $_.Exception.Message | Out-File $errorLog -Encoding utf8 -NoNewline
    Write-Host $_.Exception.Message
    if ($launchedByUs -and $proc) {
        try {
            $proc.CloseMainWindow() | Out-Null
            if (-not $proc.WaitForExit(2000)) { $proc.Kill() }
        } catch { }
    }
    exit 1
}
