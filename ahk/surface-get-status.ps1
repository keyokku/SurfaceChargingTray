# Reads the Surface app's current charging mode and writes the cache.
# Hidden launch + close pattern, READ-ONLY: does not call Select() on any element.
# Writes surface-state.json on success and surface-error.log on failure.

$ErrorActionPreference = 'Stop'
$errorLog = Join-Path $PSScriptRoot 'surface-error.log'

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

# Localized UI Names — see surface-set-mode-hidden.ps1 header for context.
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

function Invoke-GetStatus {
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

    # For each mode-key, walk its localized Names looking for one that is
    # currently selected. First IsSelected hit wins. Maps directly to our
    # internal mode-key without needing a Name → key dictionary, since each
    # name list IS already keyed by mode.
    $modeMap = @(
        @{ key = 'adaptive'; names = $AdaptiveNames }
        @{ key = '80';       names = $Limit80Names }
        @{ key = '100';      names = $Charge100Names }
    )
    $cType = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::RadioButton)
    $script:modeKey = $null
    foreach ($m in $modeMap) {
        foreach ($n in $m.names) {
            $cName = New-Object System.Windows.Automation.PropertyCondition(
                [System.Windows.Automation.AutomationElement]::NameProperty, $n)
            $cond  = New-Object System.Windows.Automation.AndCondition($cType, $cName)
            $rb = $win.FindFirst([System.Windows.Automation.TreeScope]::Subtree, $cond)
            if ($rb) {
                try {
                    $sp = $rb.GetCurrentPattern(
                        [System.Windows.Automation.SelectionItemPattern]::Pattern)
                    if ($sp.Current.IsSelected) { $script:modeKey = $m.key; break }
                } catch { }
            }
        }
        if ($script:modeKey) { break }
    }
    if (-not $script:modeKey) {
        throw "None of the three charging-mode radios appear selected. Your Surface app build may differ from the one this tool was written for."
    }

    # Duration combo only exists when mode is 100%. Combo's AutomationId is
    # stable across locales; selected item's Name is localized and gets
    # classified via Test-LooksLikeWeek substring match.
    $script:durKey = $null
    if ($script:modeKey -eq '100') {
        $cCombo = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
            'DurationSelectionComboBox')
        $combo = $win.FindFirst([System.Windows.Automation.TreeScope]::Subtree, $cCombo)
        if ($combo) {
            try {
                $sp = $combo.GetCurrentPattern([System.Windows.Automation.SelectionPattern]::Pattern)
                $sel = $sp.Current.GetSelection()
                if ($sel.Length -gt 0) {
                    $script:durKey = if (Test-LooksLikeWeek $sel[0].Current.Name) { '1week' } else { '1day' }
                }
            } catch { }
        }
    }

    $state = [PSCustomObject]@{
        Mode      = $script:modeKey
        Duration  = if ($script:modeKey -eq '100') { $script:durKey } else { $null }
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

# Run once. Retry once on transient activation/UIA failures.
$script:modeKey = $null
$script:durKey  = $null
$result = Invoke-GetStatus
if ($result -and ($result -match 'Battery & charging' -or $result -match 'UI Automation')) {
    Start-Sleep -Milliseconds 500
    $result = Invoke-GetStatus
}

if ($result) {
    $result | Out-File $errorLog -Encoding utf8 -NoNewline
    Write-Host $result
    exit 1
}

if (Test-Path $errorLog) { Remove-Item $errorLog -Force }
Write-Host "Mode=$($script:modeKey) Duration=$($script:durKey)"
