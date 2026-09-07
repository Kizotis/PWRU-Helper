<#
.SYNOPSIS
    Captures the environment facts that decide why PWRU Helper starts slowly on one machine and
    instantly on another. No admin required. Nothing is changed, only read.

.DESCRIPTION
    Writes machine-sheet-<host>-<date>.txt (human readable) and .json (machine readable) next to
    each other. User name and profile path are redacted. Nothing is uploaded anywhere; you send
    the files yourself.

    Every probe is optional: if a cmdlet is missing or access is denied, the field says so and
    the script keeps going.

.PARAMETER ExePath
    The PWRUHelper.exe to inspect (Mark-of-the-Web, signature, location). Defaults to this
    repo's portable publish output, then to any running instance.

.PARAMETER IncludePublicIp
    OFF by default for privacy. When set, queries a public "what is my IP" service and records
    the address - only useful when investigating a per-IP Google throttle (P2).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\Get-MachineSheet.ps1
#>
[CmdletBinding()]
param(
    [string]$ExePath,
    [string]$OutDir,
    [switch]$IncludePublicIp
)

$ErrorActionPreference = 'Continue'
# Force invariant number formatting: on a French/German Windows the default culture writes
# "2163,4" into the CSV, which breaks any tool that reads it as a number.
[System.Threading.Thread]::CurrentThread.CurrentCulture = [System.Globalization.CultureInfo]::InvariantCulture
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $OutDir) { $OutDir = Join-Path $env:USERPROFILE 'PWRU-Diagnostics' }
if (-not (Test-Path -LiteralPath $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

if (-not $ExePath) {
    $guess = Join-Path $scriptDir '..\..\bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\PWRUHelper.exe'
    if (Test-Path -LiteralPath $guess) { $ExePath = (Resolve-Path -LiteralPath $guess).Path }
    else {
        try { $ExePath = @(Get-Process -Name PWRUHelper -ErrorAction Stop)[0].Path } catch { $ExePath = $null }
    }
}

$sheet = [ordered]@{}
function Add-Section { param([string]$Name) $script:sheet[$Name] = [ordered]@{}; return $Name }
function Put { param([string]$Section, [string]$Key, $Value) $script:sheet[$Section][$Key] = $Value }
# Every probe goes through this: an optional probe must never stop the sheet.
function Try-Put {
    param([string]$Section, [string]$Key, [scriptblock]$Probe)
    try { $v = & $Probe; if ($null -eq $v) { $v = '(none)' } ; Put $Section $Key $v }
    catch { Put $Section $Key "(unavailable: $($_.Exception.Message))" }
}
function Reg {
    param([string]$Path, [string]$Name)
    try { (Get-ItemProperty -Path $Path -Name $Name -ErrorAction Stop).$Name }
    catch { '(not set)' }
}

# ------------------------------------------------------------------ identity / OS
$s = Add-Section 'machine'
Put $s 'computer_name'   $env:COMPUTERNAME
Put $s 'collected_utc'   ([DateTime]::UtcNow.ToString('s'))
Put $s 'powershell'      $PSVersionTable.PSVersion.ToString()
Put $s 'is_elevated'     ([bool](([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)))
Put $s 'os_version'      ([Environment]::OSVersion.Version.ToString())
Put $s 'os_64bit'        ([Environment]::Is64BitOperatingSystem)
$cv = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
Put $s 'product_name'    (Reg $cv 'ProductName')
Put $s 'display_version' (Reg $cv 'DisplayVersion')
Put $s 'build'           ("{0}.{1}" -f (Reg $cv 'CurrentBuild'), (Reg $cv 'UBR'))
Try-Put $s 'cpu'  { (Get-ItemProperty 'HKLM:\HARDWARE\DESCRIPTION\System\CentralProcessor\0' -Name ProcessorNameString -ErrorAction Stop).ProcessorNameString }
Try-Put $s 'cpu_cores' { $env:NUMBER_OF_PROCESSORS }
Try-Put $s 'ram_gb' { [math]::Round((Get-CimInstance Win32_ComputerSystem -ErrorAction Stop).TotalPhysicalMemory / 1GB, 1) }
Try-Put $s 'uptime' { $b = (Get-CimInstance Win32_OperatingSystem -ErrorAction Stop).LastBootUpTime; "{0} (booted {1})" -f ([DateTime]::Now - $b).ToString('d\.hh\:mm'), $b }
Try-Put $s 'domain_join' {
    $o = & dsregcmd /status
    ($o | Select-String -Pattern 'AzureAdJoined|DomainJoined|WorkplaceJoined|EnterpriseJoined' | ForEach-Object { $_.Line.Trim() }) -join ' | '
}

# ------------------------------------------------------------------ Defender
$s = Add-Section 'defender'
Try-Put $s 'preference' {
    $p = Get-MpPreference -ErrorAction Stop
    [ordered]@{
        CloudBlockLevel           = $p.CloudBlockLevel
        CloudExtendedTimeout      = $p.CloudExtendedTimeout
        DisableBlockAtFirstSeen   = $p.DisableBlockAtFirstSeen
        SubmitSamplesConsent      = $p.SubmitSamplesConsent
        MAPSReporting             = $p.MAPSReporting
        DisableRealtimeMonitoring = $p.DisableRealtimeMonitoring
        DisableScanningNetworkFiles = $p.DisableScanningNetworkFiles
        ExclusionPath             = @($p.ExclusionPath)
        ExclusionProcess          = @($p.ExclusionProcess)
        ExclusionExtension        = @($p.ExclusionExtension)
    }
}
Try-Put $s 'status' {
    $c = Get-MpComputerStatus -ErrorAction Stop
    [ordered]@{
        AMEngineVersion              = $c.AMEngineVersion
        AMProductVersion             = $c.AMProductVersion
        AntivirusSignatureLastUpdated= "$($c.AntivirusSignatureLastUpdated)"
        RealTimeProtectionEnabled    = $c.RealTimeProtectionEnabled
        IsTamperProtected            = $c.IsTamperProtected
        AntivirusEnabled             = $c.AntivirusEnabled
        BehaviorMonitorEnabled       = $c.BehaviorMonitorEnabled
    }
}
Try-Put $s 'third_party_av' {
    # SecurityCenter2 lists every registered AV product; more than one row means a non-Defender AV.
    (Get-CimInstance -Namespace root\SecurityCenter2 -ClassName AntiVirusProduct -ErrorAction Stop |
        ForEach-Object { $_.displayName }) -join ', '
}
Try-Put $s 'operational_log_last20' {
    # Needs no admin on most builds; access denied is normal on locked-down machines.
    $e = Get-WinEvent -LogName 'Microsoft-Windows-Windows Defender/Operational' -MaxEvents 20 -ErrorAction Stop
    @($e | ForEach-Object { "{0} id={1} {2}" -f $_.TimeCreated.ToString('s'), $_.Id, ($_.LevelDisplayName) })
}

# ------------------------------------------------------------------ reputation gates
$s = Add-Section 'reputation'
Put $s 'smartscreen_hkcu'   (Reg 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer' 'SmartScreenEnabled')
Put $s 'smartscreen_hklm'   (Reg 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer' 'SmartScreenEnabled')
Put $s 'smartscreen_policy' (Reg 'HKLM:\SOFTWARE\Policies\Microsoft\Windows\System' 'EnableSmartScreen')
$sac = Reg 'HKLM:\SYSTEM\CurrentControlSet\Control\CI\Policy' 'VerifiedAndReputablePolicyState'
$sacText = switch ("$sac") { '0' { '0 = off' } '1' { '1 = ENFORCED' } '2' { '2 = evaluation/audit' } default { "$sac" } }
Put $s 'smart_app_control' $sacText
Try-Put $s 'device_guard' {
    $d = Get-CimInstance -Namespace root\Microsoft\Windows\DeviceGuard -ClassName Win32_DeviceGuard -ErrorAction Stop
    [ordered]@{
        VirtualizationBasedSecurityStatus = $d.VirtualizationBasedSecurityStatus
        SecurityServicesRunning           = @($d.SecurityServicesRunning)
        CodeIntegrityPolicyEnforcementStatus = $d.CodeIntegrityPolicyEnforcementStatus
    }
}

# ------------------------------------------------------------------ the exe itself
$s = Add-Section 'executable'
if ($ExePath -and (Test-Path -LiteralPath $ExePath)) {
    $item = Get-Item -LiteralPath $ExePath
    Put $s 'path'          $ExePath
    Put $s 'size_bytes'    $item.Length
    Put $s 'last_write'    "$($item.LastWriteTime)"
    Try-Put $s 'sha256'    { (Get-FileHash -LiteralPath $ExePath -Algorithm SHA256 -ErrorAction Stop).Hash }
    Try-Put $s 'signature' { $g = Get-AuthenticodeSignature -LiteralPath $ExePath -ErrorAction Stop; "$($g.Status) / $($g.SignerCertificate.Subject)" }
    # No Zone.Identifier stream = no Mark-of-the-Web = SmartScreen does not gate this file.
    # A locally built exe never has one; a GitHub download does (ZoneId=3).
    Put $s 'mark_of_the_web' $(
        try { (Get-Content -LiteralPath $ExePath -Stream Zone.Identifier -ErrorAction Stop) -join '; ' }
        catch { '(none - no Mark-of-the-Web on this file)' }
    )
    Try-Put $s 'attributes' { "$($item.Attributes)" }   # ReparsePoint / Offline reveal OneDrive placeholders
    Try-Put $s 'location_class' {
        $p = $ExePath.ToLowerInvariant()
        $tags = @()
        if ($p -like '*\onedrive*')            { $tags += 'OneDrive-synced' }
        if ($p -like '*\desktop\*')            { $tags += 'Desktop' }
        if ($p -like '*\downloads\*')          { $tags += 'Downloads' }
        if ($p -like '*\program files*')       { $tags += 'ProgramFiles (MSI install)' }
        if ($p -like '\\*')                    { $tags += 'UNC/network path' }
        if ($tags.Count -eq 0) { $tags += 'plain local folder' }
        $drive = [System.IO.Path]::GetPathRoot($ExePath)
        $tags += "drive=$drive"
        $tags -join ', '
    }
} else {
    Put $s 'path' '(exe not found - pass -ExePath)'
}

# ------------------------------------------------------------------ folders the app touches
$s = Add-Section 'folders'
$extract = Join-Path $env:TEMP '.net\PWRUHelper'
Try-Put $s 'single_file_extraction' {
    if (-not (Test-Path -LiteralPath $extract)) { return "(absent) $extract" }
    $f = @(Get-ChildItem -LiteralPath $extract -Recurse -File -ErrorAction Stop)
    $bytes = 0; foreach ($i in $f) { $bytes += $i.Length }
    $oldest = ($f | Sort-Object LastWriteTime | Select-Object -First 1).LastWriteTime
    $newest = ($f | Sort-Object LastWriteTime | Select-Object -Last 1).LastWriteTime
    "{0} files, {1} KB, written {2} .. {3}  ({4})" -f $f.Count, [math]::Round($bytes/1KB), $oldest, $newest, $extract
}
Try-Put $s 'appdata_pwru' {
    $d = Join-Path $env:APPDATA 'PWRUHelper'
    if (-not (Test-Path -LiteralPath $d)) { return "(absent) $d" }
    $f = @(Get-ChildItem -LiteralPath $d -Recurse -File -ErrorAction Stop)
    (@($f | ForEach-Object { "{0} ({1} KB, {2})" -f $_.Name, [math]::Round($_.Length/1KB,1), $_.LastWriteTime.ToString('s') })) -join ' | '
}
Try-Put $s 'appdata_path'      { $env:APPDATA }
Try-Put $s 'appdata_reparse'   { "$((Get-Item -LiteralPath $env:APPDATA -Force).Attributes)" }
Try-Put $s 'temp_path'         { $env:TEMP }
Try-Put $s 'onedrive_kfm'      { if ($env:OneDrive) { "OneDrive=$env:OneDrive" } else { '(no OneDrive env var)' } }

# ------------------------------------------------------------------ graphics / power / display
$s = Add-Section 'display_power'
Try-Put $s 'gpu' {
    @(Get-CimInstance Win32_VideoController -ErrorAction Stop |
        ForEach-Object { "{0} (driver {1}, {2})" -f $_.Name, $_.DriverVersion, $_.DriverDate })
}
Try-Put $s 'power_plan' { (& powercfg /getactivescheme) -join ' ' }
Try-Put $s 'monitors' {
    Add-Type -AssemblyName System.Windows.Forms -ErrorAction Stop
    @([System.Windows.Forms.Screen]::AllScreens | ForEach-Object { "{0} {1}x{2}{3}" -f $_.DeviceName, $_.Bounds.Width, $_.Bounds.Height, $(if ($_.Primary) { ' (primary)' } else { '' }) })
}
Put $s 'applied_dpi' (Reg 'HKCU:\Control Panel\Desktop\WindowMetrics' 'AppliedDPI')

# ------------------------------------------------------------------ network (P2 relevant)
$s = Add-Section 'network'
Try-Put $s 'winhttp_proxy'  { (& netsh winhttp show proxy) -join ' ' }
$ie = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Internet Settings'
Put $s 'ie_proxy_enable'    (Reg $ie 'ProxyEnable')
Put $s 'ie_proxy_server'    (Reg $ie 'ProxyServer')
Put $s 'ie_autoconfig_url'  (Reg $ie 'AutoConfigURL')
Put $s 'ie_auto_detect'     (Reg $ie 'AutoDetect')
Try-Put $s 'connection_profiles' {
    @(Get-NetConnectionProfile -ErrorAction Stop | ForEach-Object { "{0}: {1}, IPv4={2}" -f $_.InterfaceAlias, $_.NetworkCategory, $_.IPv4Connectivity })
}
Try-Put $s 'dns_servers' {
    @(Get-DnsClientServerAddress -AddressFamily IPv4 -ErrorAction Stop |
        Where-Object { $_.ServerAddresses.Count -gt 0 } |
        ForEach-Object { "{0}: {1}" -f $_.InterfaceAlias, ($_.ServerAddresses -join ',') })
}
if ($IncludePublicIp) {
    Try-Put $s 'public_ip' { (Invoke-RestMethod -Uri 'https://api.ipify.org?format=json' -TimeoutSec 10 -ErrorAction Stop).ip }
} else {
    Put $s 'public_ip' '(not collected - pass -IncludePublicIp if the investigation needs it)'
}

# ------------------------------------------------------------------ render
function Redact {
    param([string]$Text)
    if (-not $Text) { return $Text }
    $out = $Text
    $targets = @(@($env:USERPROFILE, '<USERPROFILE>'), @($env:USERNAME, '<USER>'))
    # %TEMP% is reported in 8.3 form (ANTOIN~1), which the plain user-name rule would miss.
    if ($env:USERNAME -and $env:USERNAME.Length -gt 6) {
        $targets += , @(($env:USERNAME.Substring(0, 6) + '~1'), '<USER>')
    }
    foreach ($pair in $targets) {
        if ($pair[0]) { $out = $out -replace ('(?i)' + [regex]::Escape($pair[0])), $pair[1] }
    }
    return $out
}
$stamp = Get-Date -Format 'yyyy-MM-dd'
$txtPath  = Join-Path $OutDir ("machine-sheet-{0}-{1}.txt"  -f $env:COMPUTERNAME, $stamp)
$jsonPath = Join-Path $OutDir ("machine-sheet-{0}-{1}.json" -f $env:COMPUTERNAME, $stamp)

$lines = New-Object System.Collections.ArrayList
[void]$lines.Add("PWRU Helper - machine sheet")
[void]$lines.Add("generated $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss K') - user name and profile path are redacted")
foreach ($section in $sheet.Keys) {
    [void]$lines.Add("")
    [void]$lines.Add("[$section]")
    foreach ($k in $sheet[$section].Keys) {
        $v = $sheet[$section][$k]
        if ($v -is [System.Collections.IDictionary]) {
            [void]$lines.Add(("  {0}:" -f $k))
            foreach ($k2 in $v.Keys) {
                $v2 = $v[$k2]; if ($v2 -is [array]) { $v2 = if ($v2.Count) { $v2 -join ', ' } else { '(empty)' } }
                [void]$lines.Add(("    {0,-32} {1}" -f $k2, (Redact "$v2")))
            }
        } elseif ($v -is [array]) {
            [void]$lines.Add(("  {0}:" -f $k))
            if ($v.Count -eq 0) { [void]$lines.Add("    (empty)") }
            foreach ($e in $v) { [void]$lines.Add("    " + (Redact "$e")) }
        } else {
            [void]$lines.Add(("  {0,-26} {1}" -f $k, (Redact "$v")))
        }
    }
}
$lines | Set-Content -LiteralPath $txtPath -Encoding ASCII
(Redact ($sheet | ConvertTo-Json -Depth 6)) | Set-Content -LiteralPath $jsonPath -Encoding ASCII

Write-Host ""
Write-Host "Machine sheet : $txtPath" -ForegroundColor Green
Write-Host "JSON          : $jsonPath" -ForegroundColor Green
Write-Host ""
