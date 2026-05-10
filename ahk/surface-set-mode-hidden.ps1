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

# Localized UI Names the Surface app may use for the Battery & charging card
# and its three radio buttons. Tried in order; first match wins. The .exe
# build keeps a parallel list (UiaCache.cs) — when adding a new language
# here, mirror it there too. The .exe also auto-discovers and caches IDs;
# this PowerShell path is simpler and relies on the bundled list.
$BatteryCardNames = @(
    'Battery & charging', 'Battery and charging', 'Battery', 'Charging',
    'Akku & Aufladen', 'Batterie et charge', 'Batería y carga',
    'Batteria e ricarica', 'Bateria e carregamento', 'Battery en opladen',
    'Akumulator i ładowanie', 'Batteri og opladning', 'Batteri och laddning',
    'Batteri og lading', 'Akku ja lataus', 'Батарея и зарядка',
    'バッテリーと充電', '电池和充电', '電池與充電', '배터리 및 충전',
    'Pil ve şarj', 'البطارية والشحن'
)
$AdaptiveNames = @(
    'Adaptive', 'Adaptive charging',
    'Adaptiv', 'Adaptatif', 'Adaptable', 'Adattiva', 'Adaptável', 'Adaptief',
    'アダプティブ', '自适应', '自適應', '적응형'
)
$Limit80Names = @(
    'Limit to 80%', 'Limit charging to 80%', 'Battery limit 80%',
    'Auf 80 % begrenzen', 'Limiter à 80 %', 'Limitar al 80 %',
    'Limita al 80%', 'Limitar a 80%', 'Beperken tot 80%',
    '80%に制限', '限制为80%', '限制至 80%', '80%로 제한'
)
$Charge100Names = @(
    'Charge to 100%', 'Charge to full', 'Charge fully',
    'Auf 100 % aufladen', 'Charger à 100 %', 'Cargar al 100 %',
    'Carica al 100%', 'Carregar a 100%', 'Opladen tot 100%',
    '100%まで充電', '充电至100%', '充電至 100%', '100%로 충전'
)

# Pick the right name array for our internal mode key.
$RadioNamesByMode = @{
    'adaptive' = $AdaptiveNames
    '80'       = $Limit80Names
    '100'      = $Charge100Names
}

# Crude substring-based classifier for the Duration combo items
# ("1 day" / "1 week" / their localized equivalents). Anything that doesn't
# read as "week" defaults to the day variant.
function Test-LooksLikeWeek([string]$label) {
    if (-not $label) { return $false }
    $lower = $label.ToLowerInvariant()
    return ($lower -match 'week|semaine|woche|settimana|semana|неделя|週|주')
}

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

    # Try each known localized Name in turn each poll. Card may be collapsed
    # at search time (radios not yet in the subtree); we expand it after.
    $bcGroup = Wait-For {
        foreach ($n in $BatteryCardNames) {
            $cond = New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::NameProperty, $n)
            $hit = $win.FindFirst([System.Windows.Automation.TreeScope]::Subtree, $cond)
            if ($hit) { return $hit }
        }
        return $null
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

    # Walk all known localized Names for this mode's radio. AndCondition
    # ensures we match a RadioButton specifically (other elements may share
    # the label).
    $candidateNames = $RadioNamesByMode[$Mode]
    $cType = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::RadioButton)
    $rb = Wait-For {
        foreach ($n in $candidateNames) {
            $cName = New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::NameProperty, $n)
            $cond  = New-Object System.Windows.Automation.AndCondition($cType, $cName)
            $hit = $win.FindFirst([System.Windows.Automation.TreeScope]::Subtree, $cond)
            if ($hit) { return $hit }
        }
        return $null
    } 5000 100
    if (-not $rb) {
        throw "Couldn't find the radio button for mode '$Mode'. Your Surface app may be an older build that doesn't expose this mode, or your Windows display language uses a label this script doesn't recognize yet."
    }

    $selPat = $rb.GetCurrentPattern(
        [System.Windows.Automation.SelectionItemPattern]::Pattern)
    if (-not $selPat.Current.IsSelected) {
        $selPat.Select()
        Start-Sleep -Milliseconds 300
    }

    if ($Mode -eq '100') {
        # Combo's AutomationId is Microsoft's stable internal ID, not localized.
        # Combo items, however, ARE localized — we match by substring (week/day)
        # using Test-LooksLikeWeek so non-English labels still classify.
        $wantWeek = ($Duration -eq '1week')
        $cCombo = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
            'DurationSelectionComboBox')
        $combo = Wait-For { $win.FindFirst([System.Windows.Automation.TreeScope]::Subtree, $cCombo) } 3000 100

        if ($combo) {
            # Skip the dance if the currently selected item already matches.
            $skipUpdate = $false
            try {
                $sp = $combo.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern)
                $sel = $sp.Current.GetSelection()
                if ($sel.Length -gt 0) {
                    $currentIsWeek = Test-LooksLikeWeek $sel[0].Current.Name
                    if ($currentIsWeek -eq $wantWeek) { $skipUpdate = $true }
                }
            } catch { }

            if (-not $skipUpdate) {
                try {
                    $cExp = $combo.GetCurrentPattern(
                        [System.Windows.Automation.ExpandCollapsePattern]::Pattern)
                    $cExp.Expand()
                    Start-Sleep -Milliseconds 350

                    # After expand, combo items appear as ListItem descendants.
                    # Find them all then pick by substring classification.
                    $itemCond = New-Object System.Windows.Automation.PropertyCondition(
                        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
                        [System.Windows.Automation.ControlType]::ListItem)
                    $items = Wait-For {
                        $found = $combo.FindAll([System.Windows.Automation.TreeScope]::Subtree, $itemCond)
                        if ($found.Count -gt 0) { return $found } else { return $null }
                    } 2000 100

                    $target = $null
                    if ($items) {
                        foreach ($it in $items) {
                            if ((Test-LooksLikeWeek $it.Current.Name) -eq $wantWeek) { $target = $it; break }
                        }
                    }
                    if ($target) {
                        $itemSel = $target.GetCurrentPattern(
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
