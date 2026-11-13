# Drives Stremio-XBOX on the console from this PC, for testing: press controller buttons, open pages, see what is
# on screen, read the log. Needs a Developer Mode (sideloaded) install of 0.2.15 or later, running.
#
#   powershell -ExecutionPolicy Bypass -File scripts\Xbox-Remote.ps1 -Xbox 192.168.1.50 state
#   powershell -ExecutionPolicy Bypass -File scripts\Xbox-Remote.ps1 -Xbox 192.168.1.50 press down,down,start
#   powershell -ExecutionPolicy Bypass -File scripts\Xbox-Remote.ps1 -Xbox 192.168.1.50 navigate '#/library'
#   powershell -ExecutionPolicy Bypass -File scripts\Xbox-Remote.ps1 -Xbox 192.168.1.50 log -Lines 50
#   powershell -ExecutionPolicy Bypass -File scripts\Xbox-Remote.ps1 -Xbox 192.168.1.50 seek 877   (seconds, while a video plays)
#   powershell -ExecutionPolicy Bypass -File scripts\Xbox-Remote.ps1 -Xbox 192.168.1.50 eval 'location.hash'
#   powershell -ExecutionPolicy Bypass -File scripts\Xbox-Remote.ps1 -Xbox 192.168.1.50 eval -File check.js
#
# Buttons: up down left right a b x y menu (or start) view lb rb lt rt ls. Several, comma-separated, are pressed
# one after another (-Delay ms apart). The app's key comes from its LocalState\remote.json through the Device Portal
# login (saved with Save-XboxLogin.ps1) the first time, then is kept in %LOCALAPPDATA%\StremioXbox, encrypted by
# Windows for this account. It travels over the home network in plain HTTP, like Device Portal's own port 11080.
param(
    [Parameter(Mandatory = $true)][string]$Xbox,
    [Parameter(Mandatory = $true, Position = 0)][ValidateSet('ping', 'state', 'press', 'seek', 'focus', 'navigate', 'eval', 'log')][string]$Command,
    [Parameter(Position = 1)][string]$Value,
    [string]$File, # eval: run the script in this file (too long for a command line).
    [int]$Lines = 100,
    [int]$Delay = 400,
    [int]$Port = 8765
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'XboxPortal.ps1')
if ($File) { $Value = [IO.File]::ReadAllText((Resolve-Path $File).Path) }

$keyPath = Join-Path $env:LOCALAPPDATA ("StremioXbox\remote-key-" + ($Xbox -replace '[^A-Za-z0-9.-]', '_') + '.txt')

function Get-RemoteKey([switch]$Refresh) {
    if (-not $Refresh -and (Test-Path $keyPath)) {
        $secure = Get-Content $keyPath | ConvertTo-SecureString
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure))
    }
    $http = Connect-XboxPortal $Xbox
    $app = Find-StremioPackage $http 1
    if (-not $app) { throw 'Stremio-XBOX is not installed on the console.' }
    $package = [Uri]::EscapeDataString($app.PackageFullName)
    $file = Get-PortalText $http "/api/filesystem/apps/file?knownfolderid=LocalAppData&packagefullname=$package&path=%5CLocalState&filename=remote.json"
    if (-not $file.Ok) { throw "No remote.json from the app ($($file.Status)): start Stremio-XBOX 0.2.15 or later on the console first." }
    $key = ($file.Text | ConvertFrom-Json).key
    New-Item -ItemType Directory -Force (Split-Path $keyPath) | Out-Null
    ConvertTo-SecureString $key -AsPlainText -Force | ConvertFrom-SecureString | Set-Content $keyPath
    return $key
}

function Invoke-Remote([string]$method, [string]$path, [string]$body, [string]$key) {
    $uri = "http://${Xbox}:$Port$path"
    $request = @{ Uri = $uri; Method = $method; Headers = @{ 'X-Key' = $key }; UseBasicParsing = $true; TimeoutSec = 30 }
    if ($body) { $request.Body = [Text.Encoding]::UTF8.GetBytes($body); $request.ContentType = 'text/plain; charset=utf-8' }
    try {
        $response = Invoke-WebRequest @request
        return @{ Status = 200; Text = [Text.Encoding]::UTF8.GetString($response.RawContentStream.ToArray()) }
    } catch [System.Net.WebException] {
        $status = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 0 }
        if ($status -eq 0) { throw "No answer from Stremio-XBOX at ${Xbox}:$Port. Is the app open on the console (0.2.15 or later)?" }
        return @{ Status = $status; Text = '' }
    }
}

function Send([string]$method, [string]$path, [string]$body) {
    $key = Get-RemoteKey
    $answer = Invoke-Remote $method $path $body $key
    if ($answer.Status -eq 401) {
        $key = Get-RemoteKey -Refresh # Reinstalled: a new key.
        $answer = Invoke-Remote $method $path $body $key
    }
    if ($answer.Status -ne 200) { throw "Stremio-XBOX answered $($answer.Status)." }
    return $answer.Text
}

function Show([string]$json) {
    try { $json | ConvertFrom-Json | ConvertTo-Json -Depth 8 } catch { $json }
}

switch ($Command) {
    'ping' { Show (Send 'GET' '/ping') }
    'state' { Show (Send 'GET' '/state') }
    'log' { (Send 'GET' "/log?lines=$Lines" | ConvertFrom-Json).lines }
    'navigate' { Show (Send 'POST' '/navigate' $Value) }
    'seek' { Show (Send 'POST' "/seek?to=$Value") }
    'focus' { Show (Send 'POST' '/eval' ('window.__sxFocus(' + (ConvertTo-Json $Value -Compress) + ')')) }
    'eval' { Show (Send 'POST' '/eval' $Value) }
    'press' {
        $buttons = @($Value -split '[,\s]+' | Where-Object { $_ })
        if (-not $buttons) { throw 'press needs one or more buttons, e.g. press down,a' }
        for ($i = 0; $i -lt $buttons.Count; $i++) {
            if ($i) { Start-Sleep -Milliseconds $Delay }
            Show (Send 'POST' "/press/$($buttons[$i].ToLowerInvariant())")
        }
    }
}
