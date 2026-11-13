# Records the Xbox screen as a series of screenshots while you use Stremio-XBOX, then saves the app's log,
# to look back at a problem frame by frame.
#
#   powershell -ExecutionPolicy Bypass -File scripts\Watch-Xbox.ps1 -Xbox 192.168.1.50 [-Seconds 120] [-Every 15]
#
# Writes diagnostics\watch\<time>.jpg (scaled to 1600 px wide) and diagnostics\watch\session.log. Ctrl-C stops
# early and still saves the log. Uses the same Device Portal login as Deploy-Xbox.ps1.
#
# Each screenshot is a full 4K capture that takes the console several seconds. During 4K HDR playback a frame
# every 2 s slowed the video to about half speed (and froze it), so keep -Every large while a video plays;
# the app log's "Pace" lines show stutter without any screenshots.
param(
    [Parameter(Mandatory = $true)][string]$Xbox,
    [int]$Port = 11443,
    [int]$Seconds = 120,
    [double]$Every = 15
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'XboxPortal.ps1')
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot '..\diagnostics\watch'
New-Item -ItemType Directory -Force $out | Out-Null
$out = (Resolve-Path $out).Path
Get-ChildItem $out -File | Remove-Item

$http = Connect-XboxPortal $Xbox $Port
$jpeg = [Drawing.Imaging.ImageCodecInfo]::GetImageEncoders() | Where-Object MimeType -eq 'image/jpeg'
$quality = New-Object Drawing.Imaging.EncoderParameters(1)
$quality.Param[0] = New-Object Drawing.Imaging.EncoderParameter([Drawing.Imaging.Encoder]::Quality, [long]80)

function Save-Frame([string]$name) {
    $response = $http.Client.GetAsync($http.Base + '/ext/screenshot?download=false').GetAwaiter().GetResult()
    if (-not $response.IsSuccessStatusCode) { return $false }
    $stream = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
    $image = [Drawing.Image]::FromStream($stream)
    try {
        $width = [Math]::Min(1600, $image.Width)
        $height = [int]($image.Height * $width / $image.Width)
        $small = New-Object Drawing.Bitmap($image, $width, $height)
        try { $small.Save((Join-Path $out "$name.jpg"), $jpeg, $quality) } finally { $small.Dispose() }
    } finally {
        $image.Dispose()
        $stream.Dispose()
    }
    return $true
}

$frames = 0
try {
    Write-Host ("Recording for {0} s, a frame every {1} s. Start loading your video now (Ctrl-C stops early)." -f $Seconds, $Every)
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalSeconds -lt $Seconds) {
        $started = $watch.Elapsed.TotalSeconds
        $name = (Get-Date).ToString('HHmmss.f')
        if (Save-Frame $name) { $frames++; Write-Host -NoNewline "`rFrames: $frames  ($([int]$watch.Elapsed.TotalSeconds) s)   " }
        $wait = $Every - ($watch.Elapsed.TotalSeconds - $started)
        if ($wait -gt 0) { Start-Sleep -Milliseconds ([int]($wait * 1000)) }
    }
    Write-Host ''
} finally {
    $app = Find-StremioPackage $http 1
    if ($app) {
        $package = [Uri]::EscapeDataString($app.PackageFullName)
        $response = $http.Client.GetAsync($http.Base + "/api/filesystem/apps/file?knownfolderid=LocalAppData&packagefullname=$package&path=%5CLocalState&filename=session.log").GetAwaiter().GetResult()
        if ($response.IsSuccessStatusCode) {
            [IO.File]::WriteAllBytes((Join-Path $out 'session.log'), $response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult())
        }
    }
    Write-Host "Saved $frames frame(s) and the app log to $out"
}
