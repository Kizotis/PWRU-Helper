<#
.SYNOPSIS
    Reproduces PWRU Helper's Google Translate request exactly and records what comes back.

.DESCRIPTION
    P2 is a real HTTP 429 from translate.googleapis.com that survives an app restart, so the
    throttle state lives on Google's side (per public IP), not in the app. This probe sends the
    SAME request the app sends - same URL, same query parameters, same hard-coded User-Agent, no
    extra headers, system proxy, HTTP/1.1 - and records status, every response header
    (Retry-After above all), body shape and elapsed time.

    Request built from Services/GoogleGtxTranslator.cs (Services/TranslationService.cs until E3.S6):
      GET https://translate.googleapis.com/translate_a/single?client=gtx&sl=..&tl=..&dt=t&q=..
      User-Agent: Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36
      no Accept, no Accept-Language, no cookies, 12 s timeout.

    MODES
      -Smoke    5 requests, 2 s apart. Safe. Proves the probe works and captures the normal
                (200) response shape. This is the default.
      -Burst    -Count requests every -IntervalMs, stopping at the first 429; then one request
                every 30 s for up to -MaxWaitMinutes to measure how long the block lasts.
                *** SEE THE WARNING BELOW. Requires -IUnderstandTheRisk. ***
      -Variant  Same as -Burst but cycles header variants (no UA / a current Chrome UA /
                Accept-Language added) to test whether the fingerprint changes the threshold.
                Also requires -IUnderstandTheRisk.

    *** BURST WARNING ***
    Burst deliberately provokes Google into rate-limiting the machine's PUBLIC IP ADDRESS. That
    block can last minutes to hours and affects EVERY device on the same connection - other
    people in the house, and the app itself. Do not run it on a connection you are streaming or
    working on. Smoke mode carries no such risk.

    LIMITATION - TLS fingerprint. Windows PowerShell 5.1 runs on .NET Framework, so its TLS
    handshake is not byte-identical to .NET 8's. Both use SChannel on Windows, so the JA3 is
    close but not guaranteed equal. A negative result here does not prove the app is safe, and a
    429 here does not prove the app would have been throttled at exactly the same point.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\Probe-GoogleTranslate.ps1 -Smoke
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\Probe-GoogleTranslate.ps1 -Burst -Count 120 -IntervalMs 250 -IUnderstandTheRisk
#>
[CmdletBinding()]
param(
    [switch]$Smoke,
    [switch]$Burst,
    [switch]$Variant,
    [switch]$IUnderstandTheRisk,
    [int]$Count = 60,
    [int]$IntervalMs = 500,
    [int]$MaxWaitMinutes = 20,
    [int]$RecoveryPollSeconds = 30,
    [string]$Source = 'ru',
    [string]$Target = 'en',
    [string]$OutDir
)

$ErrorActionPreference = 'Continue'
# Force invariant number formatting: on a French/German Windows the default culture writes
# "2163,4" into the CSV, which breaks any tool that reads it as a number.
[System.Threading.Thread]::CurrentThread.CurrentCulture = [System.Globalization.CultureInfo]::InvariantCulture
if (-not ($Smoke -or $Burst -or $Variant)) { $Smoke = $true }

if (-not $OutDir) { $OutDir = Join-Path $env:USERPROFILE 'PWRU-Diagnostics' }
if (-not (Test-Path -LiteralPath $OutDir)) { New-Item -ItemType Directory -Path $OutDir -Force | Out-Null }

Add-Type -AssemblyName System.Web | Out-Null
# PS 5.1 can still default to TLS 1.0 on older boxes; Google requires 1.2+.
try { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 } catch { }

# The exact UA from Services/GoogleGtxTranslator.cs:46-47. Do not "modernise" it.
$AppUa = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0 Safari/537.36'

# Short Russian chat-like lines, rotated so consecutive requests are not identical strings
# (identical queries could be served from a Google-side cache and hide the throttle).
# Kept base64 so this .ps1 stays pure ASCII: PowerShell 5.1 reads a BOM-less UTF-8 script as
# ANSI, which would silently mangle literal Cyrillic and change the request being tested.
$Phrases = ([Text.Encoding]::UTF8.GetString([Convert]::FromBase64String(
    '0L/RgNC40LLQtdGCINCy0YHQtdC8fNC60YLQviDQuNC00LXRgiDQsiDQtNCw0L3QtnzQvdGD0LbQtdC9INGF0LjQu3zRgdC60L7Qu9GM0LrQviDRgdGC0L7QuNGCfNC20LTRgyDRgyDQv9C+0YDRgtCw0LvQsHzQtNCw0Lkg0L/QsNGC0Lgg0L/Qu9C40Ld80LjQtNGDINGH0LXRgNC10Lcg0LzQuNC90YPRgtGDfNGB0L/QsNGB0LjQsdC+INCx0L7Qu9GM0YjQvtC1fNCz0LTQtSDQsdC+0YHRgXzRjyDQs9C+0YLQvtCy'
))) -split '\|'

function Invoke-Probe {
    param([int]$Seq, [string]$Phase, [string]$VariantName, [string]$Text)

    $q   = [System.Web.HttpUtility]::UrlEncode($Text)
    $url = "https://translate.googleapis.com/translate_a/single?client=gtx&sl=$Source&tl=$Target&dt=t&q=$q"

    $row = [ordered]@{
        seq = $Seq; phase = $Phase; variant = $VariantName
        timestamp = (Get-Date).ToString('s')
        status = ''; status_text = ''; elapsed_ms = ''
        retry_after = ''; content_type = ''; server = ''; alt_svc = ''
        body_len = ''; body_shape = ''; body_head = ''
        all_headers = ''
    }

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $resp = $null
    try {
        $req = [System.Net.HttpWebRequest]::Create($url)
        $req.Method           = 'GET'
        $req.Timeout          = 12000          # same 12 s budget as the app
        $req.ReadWriteTimeout = 12000
        $req.ProtocolVersion  = [System.Net.HttpVersion]::Version11
        $req.KeepAlive        = $true
        # $req.Proxy is the system proxy by default - same as the app's HttpClient.

        switch ($VariantName) {
            'app'          { $req.UserAgent = $AppUa }
            'no-ua'        { }                                     # no User-Agent at all
            'chrome-recent'{ $req.UserAgent = 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36' }
            'app+lang'     { $req.UserAgent = $AppUa; $req.Headers.Add('Accept-Language', 'en-US,en;q=0.9,ru;q=0.8') }
            default        { $req.UserAgent = $AppUa }
        }

        try { $resp = $req.GetResponse() }
        catch [System.Net.WebException] {
            # 4xx/5xx land here; the response object still carries status + headers.
            if ($_.Exception.Response) { $resp = $_.Exception.Response }
            else { $row.status = 'no-response'; $row.status_text = $_.Exception.Message }
        }

        if ($resp) {
            $http = [System.Net.HttpWebResponse]$resp
            $row.status       = [int]$http.StatusCode
            $row.status_text  = "$($http.StatusCode)"
            $row.retry_after  = $http.Headers['Retry-After']
            $row.content_type = $http.Headers['Content-Type']
            $row.server       = $http.Headers['Server']
            $row.alt_svc      = $http.Headers['Alt-Svc']
            $row.all_headers  = (@($http.Headers.AllKeys | ForEach-Object { "$_=$($http.Headers[$_])" }) -join ' | ')

            $body = ''
            try {
                $sr = New-Object System.IO.StreamReader($http.GetResponseStream())
                $body = $sr.ReadToEnd()
                $sr.Close()
            } catch { }
            $row.body_len = $body.Length
            if ($body -match '^\s*\[\[')                       { $row.body_shape = 'json-array' }
            elseif ($body -match '(?i)<!doctype html|<html')   { $row.body_shape = 'html' }
            elseif ($body.Length -eq 0)                        { $row.body_shape = 'empty' }
            else                                               { $row.body_shape = 'other' }
            # Non-JSON bodies are the interesting ones (captcha / block page): keep a slice.
            $head = $body.Substring(0, [Math]::Min(300, $body.Length))
            $row.body_head = ($head -replace '\s+', ' ')
            $http.Close()
        }
    }
    catch {
        $row.status = 'error'; $row.status_text = $_.Exception.Message
    }
    finally { $sw.Stop(); $row.elapsed_ms = [math]::Round($sw.Elapsed.TotalMilliseconds, 1) }

    return (New-Object psobject -Property $row)
}

# ------------------------------------------------------------------ run
$stamp   = Get-Date -Format 'yyyyMMdd-HHmmss'
$mode    = if ($Variant) { 'variant' } elseif ($Burst) { 'burst' } else { 'smoke' }
$csvPath = Join-Path $OutDir ("google-probe-{0}-{1}-{2}.csv" -f $mode, $env:COMPUTERNAME, $stamp)
$sumPath = Join-Path $OutDir ("google-probe-{0}-{1}-{2}.txt" -f $mode, $env:COMPUTERNAME, $stamp)

if (($Burst -or $Variant) -and -not $IUnderstandTheRisk) {
    Write-Host ""
    Write-Host "REFUSING TO RUN." -ForegroundColor Red
    Write-Host "Burst/Variant mode deliberately provokes a rate limit on this connection's PUBLIC IP."
    Write-Host "It can throttle Google Translate for everyone on this connection for minutes to hours."
    Write-Host "Re-run with -IUnderstandTheRisk if that is acceptable right now."
    Write-Host ""
    exit 2
}

$rows = @()
$firstBlock = $null

if ($mode -eq 'smoke') {
    Write-Host "Smoke test: 5 requests, 2 s apart (safe)." -ForegroundColor Cyan
    for ($i = 1; $i -le 5; $i++) {
        $r = Invoke-Probe -Seq $i -Phase 'smoke' -VariantName 'app' -Text $Phrases[($i - 1) % $Phrases.Count]
        $rows += $r
        Write-Host ("  {0}. HTTP {1}  {2} ms  {3}  {4} bytes" -f $i, $r.status, $r.elapsed_ms, $r.body_shape, $r.body_len)
        if ($i -lt 5) { Start-Sleep -Milliseconds 2000 }
    }
}
else {
    $variants = if ($Variant) { @('app', 'no-ua', 'chrome-recent', 'app+lang') } else { @('app') }
    Write-Host ("{0} mode: up to {1} requests every {2} ms, variants: {3}" -f $mode, $Count, $IntervalMs, ($variants -join ',')) -ForegroundColor Yellow
    for ($i = 1; $i -le $Count; $i++) {
        $v = $variants[($i - 1) % $variants.Count]
        $r = Invoke-Probe -Seq $i -Phase 'load' -VariantName $v -Text $Phrases[($i - 1) % $Phrases.Count]
        $rows += $r
        Write-Host ("  {0}. [{1}] HTTP {2}  {3} ms  {4}" -f $i, $v, $r.status, $r.elapsed_ms, $r.body_shape)
        if ("$($r.status)" -eq '429' -or "$($r.status)" -eq '403') { $firstBlock = $r; break }
        Start-Sleep -Milliseconds $IntervalMs
    }

    if ($firstBlock) {
        Write-Host ("Blocked at request {0} (HTTP {1}). Measuring recovery..." -f $firstBlock.seq, $firstBlock.status) -ForegroundColor Yellow
        $t0 = Get-Date
        $seq = $firstBlock.seq
        while (((Get-Date) - $t0).TotalMinutes -lt $MaxWaitMinutes) {
            Start-Sleep -Seconds $RecoveryPollSeconds
            $seq++
            $r = Invoke-Probe -Seq $seq -Phase 'recovery' -VariantName 'app' -Text $Phrases[($seq - 1) % $Phrases.Count]
            $rows += $r
            $mins = [math]::Round(((Get-Date) - $t0).TotalMinutes, 1)
            Write-Host ("  +{0} min: HTTP {1} {2}" -f $mins, $r.status, $r.body_shape)
            if ("$($r.status)" -eq '200') { break }
        }
    }
}

$rows | Export-Csv -LiteralPath $csvPath -NoTypeInformation -Encoding ASCII

# ------------------------------------------------------------------ summary
$lines = New-Object System.Collections.ArrayList
[void]$lines.Add("PWRU Helper - Google Translate probe")
[void]$lines.Add("mode        : $mode")
[void]$lines.Add("machine     : $env:COMPUTERNAME")
[void]$lines.Add("date        : $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss K')")
[void]$lines.Add("endpoint    : translate.googleapis.com/translate_a/single?client=gtx&sl=$Source&tl=$Target&dt=t&q=...")
[void]$lines.Add("user-agent  : $AppUa")
[void]$lines.Add("requests    : $($rows.Count)")
[void]$lines.Add("")
$byStatus = $rows | Group-Object status | Sort-Object Name
foreach ($g in $byStatus) { [void]$lines.Add(("status {0,-12} x{1}" -f $g.Name, $g.Count)) }
$okRows = @($rows | Where-Object { "$($_.status)" -eq '200' })
if ($okRows.Count -gt 0) {
    $avg = ($okRows | Measure-Object -Property elapsed_ms -Average).Average
    [void]$lines.Add(("average elapsed on 200: {0} ms" -f [math]::Round($avg, 1)))
}
$ra = @($rows | Where-Object { $_.retry_after })
[void]$lines.Add("Retry-After header seen: " + $(if ($ra.Count) { "YES (" + (($ra | ForEach-Object { $_.retry_after }) -join ',') + ")" } else { "no" }))
if ($firstBlock) {
    [void]$lines.Add("first block at request $($firstBlock.seq) with HTTP $($firstBlock.status)")
    $rec = @($rows | Where-Object { $_.phase -eq 'recovery' -and "$($_.status)" -eq '200' })
    [void]$lines.Add("recovered: " + $(if ($rec.Count) { "yes, after ~$([math]::Round((([datetime]$rec[0].timestamp) - ([datetime]$firstBlock.timestamp)).TotalMinutes,1)) min" } else { "not within $MaxWaitMinutes min" }))
}
[void]$lines.Add("")
[void]$lines.Add("First response headers:")
if ($rows.Count -gt 0) { [void]$lines.Add("  " + $rows[0].all_headers) }
[void]$lines.Add("Body shape of first response: $($rows[0].body_shape) ($($rows[0].body_len) bytes)")
[void]$lines.Add("  head: $($rows[0].body_head)")
[void]$lines.Add("")
[void]$lines.Add("NOTE: PS 5.1 runs on .NET Framework; its TLS fingerprint is close to but not identical")
[void]$lines.Add("to the app's .NET 8 client. Treat thresholds measured here as indicative, not exact.")

$lines | Set-Content -LiteralPath $sumPath -Encoding ASCII

Write-Host ""
Write-Host "CSV     : $csvPath" -ForegroundColor Green
Write-Host "Summary : $sumPath" -ForegroundColor Green
Write-Host ""
