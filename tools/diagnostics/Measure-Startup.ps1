<#
.SYNOPSIS
    Measures PWRU Helper startup time and splits it into PRE-PROCESS and IN-PROCESS time.

.DESCRIPTION
    P1 (slow startup) reportedly happens BEFORE any window appears. That window of time has two
    very different halves and they point at completely different culprits:

      T0 -> T1  PRE-PROCESS : from "launch requested" to "the process actually exists".
                              Defender scan-on-execute, SmartScreen / Smart App Control
                              reputation lookup, and image loading of a 180 MB unsigned
                              single-file exe all live here. The app's own code has not run yet.

      T1 -> T2  IN-PROCESS  : from Process.StartTime to the first top-level window.
                              Native-library self-extraction to %TEMP%\.net\, JIT, settings /
                              phrases / slang / squad file I/O in %APPDATA%, XAML load, and the
                              first layout of the Phrasebook grid live here.

    pre >> in   -> OS / antivirus / reputation branch.
    in  >> pre  -> app + extraction + roaming-profile I/O branch.

    The app owns a single-instance mutex, so every run kills the previous instance and waits for
    it to exit before launching again. The process is killed (not closed), so it does NOT write
    settings.json on the way out.

.PARAMETER ExePath
    PWRUHelper.exe to measure. Defaults to the portable publish output of this repo.

.PARAMETER Runs
    Number of launches. Default 3.

.PARAMETER LaunchMode
    Shell  = ShellExecute (what a double-click in Explorer does; SmartScreen applies to a
             Mark-of-the-Web exe). This is the realistic mode.
    Direct = CreateProcess (UseShellExecute=false). Skips the shell layer; comparing the two
             isolates the SmartScreen/shell contribution.

.PARAMETER ClearExtractionCache
    Deletes %TEMP%\.net\PWRUHelper\* before EACH run, forcing the single-file host to re-extract
    its native libraries (and Defender to re-scan every extracted file). OFF by default because
    it is not what a normal launch does - it reproduces "first launch of a new build" and
    "someone cleaned %TEMP%".

.PARAMETER OutDir
    Where the CSV + summary are written. Default: %USERPROFILE%\PWRU-Diagnostics

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\Measure-Startup.ps1 -Runs 3
#>
[CmdletBinding()]
param(
    [string]$ExePath,
    [int]$Runs = 3,
    [ValidateSet('Shell', 'Direct')][string]$LaunchMode = 'Shell',
    [switch]$ClearExtractionCache,
    [string]$OutDir,
    [string]$Note = '',
    [int]$TimeoutSeconds = 90,
    [int]$SettleMs = 1200
)

$ErrorActionPreference = 'Stop'
# Force invariant number formatting: on a French/German Windows the default culture writes
# "2163,4" into the CSV, which breaks any tool that reads it as a number.
[System.Threading.Thread]::CurrentThread.CurrentCulture = [System.Globalization.CultureInfo]::InvariantCulture
Set-StrictMode -Version 2.0

# ---------- paths ----------
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $ExePath) {
    $ExePath = Join-Path $scriptDir '..\..\bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\PWRUHelper.exe'
}
try { $ExePath = (Resolve-Path -LiteralPath $ExePath -ErrorAction Stop).Path }
catch { Write-Host "ERROR: exe not found: $ExePath" -ForegroundColor Red; exit 1 }

if (-not $OutDir) { $OutDir = Join-Path $env:USERPROFILE 'PWRU-Diagnostics' }
if (-not (Test-Path -LiteralPath $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

$stamp    = Get-Date -Format 'yyyyMMdd-HHmmss'
$csvPath  = Join-Path $OutDir ("startup-{0}-{1}.csv" -f $env:COMPUTERNAME, $stamp)
$sumPath  = Join-Path $OutDir ("startup-{0}-{1}.txt" -f $env:COMPUTERNAME, $stamp)
$extract  = Join-Path $env:TEMP '.net\PWRUHelper'
$procName = [System.IO.Path]::GetFileNameWithoutExtension($ExePath)

# ---------- helpers ----------
function Stop-AppInstances {
    # The app holds a single-instance mutex; a leftover instance makes the next launch exit
    # immediately and produce a nonsense measurement.
    $killed = 0
    try { $ps = @(Get-Process -Name $procName -ErrorAction Stop) } catch { $ps = @() }
    foreach ($p in $ps) {
        try { $p.Kill(); $killed++ } catch { }
    }
    $deadline = (Get-Date).AddSeconds(15)
    while ((Get-Date) -lt $deadline) {
        try { $left = @(Get-Process -Name $procName -ErrorAction Stop) } catch { $left = @() }
        if ($left.Count -eq 0) { break }
        Start-Sleep -Milliseconds 50
    }
    return $killed
}

function Get-ExtractionState {
    if (-not (Test-Path -LiteralPath $extract)) { return @{ Present = $false; Files = 0; Bytes = 0 } }
    try {
        $f = @(Get-ChildItem -LiteralPath $extract -Recurse -File -ErrorAction Stop)
        $bytes = 0; foreach ($i in $f) { $bytes += $i.Length }
        return @{ Present = ($f.Count -gt 0); Files = $f.Count; Bytes = $bytes }
    } catch { return @{ Present = $false; Files = -1; Bytes = -1 } }
}

function Clear-ExtractionDir {
    if (-not (Test-Path -LiteralPath $extract)) { return $true }
    try { Remove-Item -LiteralPath $extract -Recurse -Force -ErrorAction Stop; return $true }
    catch { Write-Host "  (could not clear $extract : $($_.Exception.Message))" -ForegroundColor Yellow; return $false }
}

# ---------- one measured launch ----------
function Invoke-OneRun {
    param([int]$Index, [bool]$Cleared, [hashtable]$ExtractBefore)

    $proc = $null
    $row = [ordered]@{
        run                = $Index
        timestamp          = (Get-Date).ToString('s')
        launch_mode        = $LaunchMode
        extraction_cleared = $Cleared
        extract_files_before = $ExtractBefore.Files
        pre_process_ms     = ''
        in_process_ms      = ''
        total_ms           = ''
        pid                = ''
        result             = 'ok'
        note               = $Note
    }

    try {
        $t0 = [DateTime]::Now                    # T0 - launch requested
        $sw = [System.Diagnostics.Stopwatch]::StartNew()

        if ($LaunchMode -eq 'Shell') {
            # Start-Process defaults to UseShellExecute = $true -> the same code path Explorer
            # uses, so SmartScreen sees the Mark-of-the-Web if there is one.
            $proc = Start-Process -FilePath $ExePath -PassThru
        } else {
            $psi = New-Object System.Diagnostics.ProcessStartInfo
            $psi.FileName         = $ExePath
            $psi.UseShellExecute  = $false        # raw CreateProcess, no shell / SmartScreen layer
            $psi.WorkingDirectory = (Split-Path -Parent $ExePath)
            $proc = [System.Diagnostics.Process]::Start($psi)
        }

        if (-not $proc) {
            # ShellExecute occasionally hands off without returning a Process; fall back.
            $deadline = (Get-Date).AddSeconds(10)
            while (-not $proc -and (Get-Date) -lt $deadline) {
                try { $proc = @(Get-Process -Name $procName -ErrorAction Stop)[0] } catch { }
                Start-Sleep -Milliseconds 10
            }
        }
        if (-not $proc) { $row.result = 'no-process'; return $row }

        $row.pid = $proc.Id

        # T1 - the process really exists. Kernel-reported, independent of our polling.
        $t1 = $null
        $deadline = (Get-Date).AddSeconds(10)
        while (-not $t1 -and (Get-Date) -lt $deadline) {
            try { $t1 = $proc.StartTime } catch { Start-Sleep -Milliseconds 5 }
        }

        # T2 - first top-level window. Poll at ~5 ms.
        $hwnd = [IntPtr]::Zero
        $limit = $TimeoutSeconds * 1000
        while ($sw.ElapsedMilliseconds -lt $limit) {
            try {
                $proc.Refresh()
                if ($proc.HasExited) { $row.result = 'exited-before-window'; break }
                $hwnd = $proc.MainWindowHandle
                if ($hwnd -ne [IntPtr]::Zero) { break }
            } catch { $row.result = 'process-gone'; break }
            [System.Threading.Thread]::Sleep(5)
        }
        $sw.Stop()

        if ($hwnd -eq [IntPtr]::Zero -and $row.result -eq 'ok') { $row.result = 'timeout-no-window' }

        $total = [double]$sw.Elapsed.TotalMilliseconds
        $row.total_ms = [math]::Round($total, 1)
        if ($t1) {
            $pre = ($t1 - $t0).TotalMilliseconds
            if ($pre -lt 0) { $pre = 0 }          # DateTime.Now resolution can make this slightly negative
            $row.pre_process_ms = [math]::Round($pre, 1)
            $row.in_process_ms  = [math]::Round([math]::Max($total - $pre, 0), 1)
        } else {
            $row.result = 'no-starttime'
        }
    }
    catch {
        $row.result = "error: $($_.Exception.Message)"
    }
    finally {
        # Never leave the app running.
        Start-Sleep -Milliseconds $SettleMs
        [void](Stop-AppInstances)
    }
    return $row
}

# ---------- main ----------
$exeItem = Get-Item -LiteralPath $ExePath
$hash = try { (Get-FileHash -LiteralPath $ExePath -Algorithm SHA256).Hash } catch { 'unavailable' }

Write-Host ""
Write-Host "PWRU Helper - startup measurement" -ForegroundColor Cyan
Write-Host "  exe    : $ExePath"
Write-Host "  size   : $([math]::Round($exeItem.Length/1MB,1)) MB   sha256: $hash"
Write-Host "  built  : $($exeItem.LastWriteTime)"
Write-Host "  mode   : $LaunchMode   runs: $Runs   clear-extraction: $ClearExtractionCache"
Write-Host ""

[void](Stop-AppInstances)
$rows = @()

for ($i = 1; $i -le $Runs; $i++) {
    $cleared = $false
    if ($ClearExtractionCache) { $cleared = Clear-ExtractionDir }
    $before = Get-ExtractionState
    Write-Host ("run {0}/{1} ... (extraction dir: {2} files)" -f $i, $Runs, $before.Files) -NoNewline
    $r = Invoke-OneRun -Index $i -Cleared $cleared -ExtractBefore $before
    $rows += (New-Object psobject -Property $r)
    Write-Host ("  pre={0} ms  in={1} ms  total={2} ms  [{3}]" -f $r.pre_process_ms, $r.in_process_ms, $r.total_ms, $r.result)
}

$rows | Export-Csv -LiteralPath $csvPath -NoTypeInformation -Encoding ASCII

# ---------- summary ----------
$ok = @($rows | Where-Object { $_.result -eq 'ok' })
$lines = New-Object System.Collections.ArrayList
[void]$lines.Add("PWRU Helper startup measurement")
[void]$lines.Add("machine      : $env:COMPUTERNAME")
[void]$lines.Add("date         : $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss K')")
[void]$lines.Add("exe          : $ExePath")
[void]$lines.Add("exe size     : $($exeItem.Length) bytes")
[void]$lines.Add("exe sha256   : $hash")
[void]$lines.Add("exe modified : $($exeItem.LastWriteTime)")
[void]$lines.Add("launch mode  : $LaunchMode")
[void]$lines.Add("clear cache  : $ClearExtractionCache")
[void]$lines.Add("note         : $Note")
[void]$lines.Add("")
[void]$lines.Add("run  pre_process_ms  in_process_ms  total_ms  result")
foreach ($r in $rows) {
    [void]$lines.Add(("{0,-4} {1,14} {2,14} {3,9}  {4}" -f $r.run, $r.pre_process_ms, $r.in_process_ms, $r.total_ms, $r.result))
}
if ($ok.Count -gt 0) {
    $avgPre = ($ok | Measure-Object -Property pre_process_ms -Average).Average
    $avgIn  = ($ok | Measure-Object -Property in_process_ms  -Average).Average
    $avgTot = ($ok | Measure-Object -Property total_ms       -Average).Average
    [void]$lines.Add("")
    [void]$lines.Add(("average (n={0})  pre={1} ms  in={2} ms  total={3} ms" -f $ok.Count, [math]::Round($avgPre,1), [math]::Round($avgIn,1), [math]::Round($avgTot,1)))
    [void]$lines.Add("")
    if ($avgTot -gt 0 -and ($avgPre / $avgTot) -gt 0.5) {
        [void]$lines.Add("READING: most of the time is spent BEFORE the process exists ->")
        [void]$lines.Add("         antivirus scan-on-execute / SmartScreen / reputation, not the app's code.")
    } else {
        [void]$lines.Add("READING: most of the time is spent INSIDE the process ->")
        [void]$lines.Add("         self-extraction, JIT, file I/O in %APPDATA%, XAML load and first layout.")
    }
}
[void]$lines.Add("")
[void]$lines.Add("T0 = launch requested | T1 = Process.StartTime | T2 = first top-level window (MainWindowHandle).")
[void]$lines.Add("T2 is when the window is CREATED; the first painted frame follows within a few ms.")

$lines | Set-Content -LiteralPath $sumPath -Encoding ASCII

Write-Host ""
Write-Host "CSV     : $csvPath" -ForegroundColor Green
Write-Host "Summary : $sumPath" -ForegroundColor Green
Write-Host ""
