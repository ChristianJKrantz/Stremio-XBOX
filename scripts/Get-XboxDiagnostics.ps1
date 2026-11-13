# Saves what is on the Xbox screen and Stremio-XBOX's log into the diagnostics folder, to see a problem as it happens.
#
#   powershell -ExecutionPolicy Bypass -File scripts\Get-XboxDiagnostics.ps1 -Xbox 192.168.1.50
#
# Run it while the problem is on screen. Writes diagnostics\xbox-screen.png, diagnostics\xbox-session.log and
# diagnostics\xbox-previous.log (the run before). Uses the same Device Portal login as Deploy-Xbox.ps1.
#
# -NoScreenshot fetches only the logs. Use it while a video plays: a 4K screenshot during HDR playback froze the
# picture on a Series X until the player was restarted.
param(
    [Parameter(Mandatory = $true)][string]$Xbox,
    [int]$Port = 11443,
    [switch]$NoScreenshot
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'XboxPortal.ps1')
$out = Join-Path $PSScriptRoot '..\diagnostics'
New-Item -ItemType Directory -Force $out | Out-Null
$out = (Resolve-Path $out).Path

$http = Connect-XboxPortal $Xbox $Port

function Save-Url([string]$path, [string]$file) {
    $response = $http.Client.GetAsync($http.Base + $path).GetAwaiter().GetResult()
    if (-not $response.IsSuccessStatusCode) { return [int]$response.StatusCode }
    $bytes = $response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult()
    [IO.File]::WriteAllBytes((Join-Path $out $file), $bytes)
    return 200
}

# Screen first, while the problem is still showing.
if (-not $NoScreenshot) {
    $shot = 0
    foreach ($path in @('/ext/screenshot?download=false', '/ext/screenshot', '/api/xbox/screenshot')) {
        $shot = Save-Url $path 'xbox-screen.png'
        if ($shot -eq 200) { Write-Host "Screen:       $out\xbox-screen.png"; break }
    }
    if ($shot -ne 200) { Write-Warning "Screenshot not available from Device Portal (last answer $shot)." }
}

# The app's own log files, from its LocalState folder.
$app = Find-StremioPackage $http 1
if ($app) {
    $package = [Uri]::EscapeDataString($app.PackageFullName)
    foreach ($pair in @(@('session.log', 'xbox-session.log'), @('previous.log', 'xbox-previous.log'))) {
        $status = Save-Url "/api/filesystem/apps/file?knownfolderid=LocalAppData&packagefullname=$package&path=%5CLocalState&filename=$($pair[0])" $pair[1]
        if ($status -eq 200) {
            Write-Host "Log:          $out\$($pair[1])"
        } else {
            Write-Warning "$($pair[0]) not available ($status)."
        }
    }
} else {
    Write-Warning 'Stremio-XBOX is not installed, so there is no app log to fetch.'
}
