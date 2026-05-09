param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('adaptive', '80', '100')]
    [string]$Mode,

    [ValidateSet('1day', '1week')]
    [string]$Duration = '1day'
)

# Architecture A: hidden launch -> set mode -> close.
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

function Invoke-SetMode {
    $proc = $null
    $launchedByUs = $false

    try {
    # If the Surface app is already running, close it first. The user
    # could be on any page (Device info, Help, etc.) where the
    # Battery & charging card isn't in the UI Automation tree, which would
    # make our search fail. Killing-and-relaunching guarantees we land on
    # the home page where the card lives.
    $proc = Find-SurfaceProcess
    if ($proc) {
        try {
            $proc.CloseMainWindow() | Out-Null
            if (-not $proc.WaitForExit(2500)) { $proc.Kill() }
        } catch { }
        Start-Sleep -Milliseconds 300
        $proc = $null
    }

    # Always launch fresh.
    $launchedByUs = $true
    $startApp = Get-StartApps | Where-Object { $_.Name -eq 'Surface' } | Select-Object -First 1
    $aumid = if ($startApp) { $startApp.AppID } else { 'Microsoft.SurfaceHub_8wekyb3d8bbwe!App' }
    Start-Process "shell:appsFolder\$aumid" | Out-Null
    $proc = Wait-For { Find-SurfaceProcess } 10000 50
    if (-not $proc) {
        throw "Surface app didn't launch within 10 seconds. Is the Surface app installed and working?"
    }
    Hide-Window $proc.MainWindowHandle

    $win = Wait-For {
        try { [System.Windows.Automation.AutomationElement]::FromHandle($proc.MainWindowHandle) }
        catch { $null }
    } 5000 100
    if (-not $win) { throw "Could not bind UI Automation to the Surface window." }

    $bcCond = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, 'Battery & charging')
    $bcGroup = Wait-For {
        $win.FindFirst([System.Windows.Automation.TreeScope]::Subtree, $bcCond)
    } 10000 200
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

    $nameMap = @{
        'adaptive' = 'Adaptive'
        '80'       = 'Limit to 80%'
        '100'      = 'Charge to 100%'
    }
    $targetName = $nameMap[$Mode]

    $cType = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::RadioButton)
    $cName = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty, $targetName)
    $cond  = New-Object System.Windows.Automation.AndCondition($cType, $cName)

    $rb = Wait-For { $win.FindFirst([System.Windows.Automation.TreeScope]::Subtree, $cond) } 5000 100
    if (-not $rb) {
        throw "Couldn't find the '$targetName' radio button. Your Surface app may be an older build that doesn't expose this mode."
    }

    $selPat = $rb.GetCurrentPattern(
        [System.Windows.Automation.SelectionItemPattern]::Pattern)
    if (-not $selPat.Current.IsSelected) {
        $selPat.Select()
        Start-Sleep -Milliseconds 300
    }

    if ($Mode -eq '100') {
        $durLabel = if ($Duration -eq '1week') { '1 week' } else { '1 day' }

        $cCombo = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
            'DurationSelectionComboBox')
        $combo = Wait-For { $win.FindFirst([System.Windows.Automation.TreeScope]::Subtree, $cCombo) } 3000 100

        if ($combo) {
            $currentLabel = ''
            try {
                $sp = $combo.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern)
                $sel = $sp.Current.GetSelection()
                if ($sel.Length -gt 0) { $currentLabel = $sel[0].Current.Name }
            } catch { }

            if ($currentLabel -ne $durLabel) {
                try {
                    $cExp = $combo.GetCurrentPattern(
                        [System.Windows.Automation.ExpandCollapsePattern]::Pattern)
                    $cExp.Expand()
                    Start-Sleep -Milliseconds 350

                    $cItem = New-Object System.Windows.Automation.PropertyCondition(
                        [System.Windows.Automation.AutomationElement]::NameProperty, $durLabel)
                    $item = Wait-For { $win.FindFirst([System.Windows.Automation.TreeScope]::Subtree, $cItem) } 2000 100
                    if ($item) {
                        $itemSel = $item.GetCurrentPattern(
                            [System.Windows.Automation.SelectionItemPattern]::Pattern)
                        $itemSel.Select()
                        Start-Sleep -Milliseconds 250
                    } else {
                        $cExp.Collapse()
                    }
                } catch { }
            }
        }
    }

    $state = [PSCustomObject]@{
        Mode      = $Mode
        Duration  = if ($Mode -eq '100') { $Duration } else { $null }
        Timestamp = (Get-Date).ToString('s')
    }
    $cachePath = Join-Path $PSScriptRoot 'surface-state.json'
    $state | ConvertTo-Json | Out-File $cachePath -Encoding utf8

    Start-Sleep -Milliseconds 200
    try {
        $proc.CloseMainWindow() | Out-Null
        if (-not $proc.WaitForExit(2000)) { $proc.Kill() }
    } catch { }

    return $null  # success
    }
    catch {
        if ($launchedByUs -and $proc) {
            try {
                $proc.CloseMainWindow() | Out-Null
                if (-not $proc.WaitForExit(2000)) { $proc.Kill() }
            } catch { }
        }
        return $_.Exception.Message
    }
}

# Run once. On a transient activation/UIA failure, retry once after a
# short pause — these usually clear up if the system was momentarily
# busy (e.g. right after the user closed Settings, or focus was still
# settling).
$result = Invoke-SetMode
if ($result -and ($result -match 'Battery & charging' -or $result -match 'UI Automation')) {
    Start-Sleep -Milliseconds 500
    $result = Invoke-SetMode
}

if ($result) {
    $result | Out-File $errorLog -Encoding utf8 -NoNewline
    Write-Host $result
    exit 1
}

if (Test-Path $errorLog) { Remove-Item $errorLog -Force }

$durStr = if ($Mode -eq '100') { " ($Duration)" } else { '' }
Write-Host "Done. Mode=$Mode$durStr"
