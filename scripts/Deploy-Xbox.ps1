# Installs a built Stremio-XBOX package on an Xbox in Developer Mode through Device Portal, then starts it.
#
#   powershell -ExecutionPolicy Bypass -File scripts\Deploy-Xbox.ps1 -Xbox 192.168.1.50 [-Package path\to\StremioXbox_x.y.z.w_x64.msix]
#
# Before the first run:
#   1. Build a signed package (scripts\New-DevCert.ps1 once, then a Release x64 build in Visual Studio or MSBuild).
#   2. Trust the console's Device Portal certificate on this PC (download https://<xbox>:11443/config/rootcertificate
#      and run: certutil -user -addstore Root <file>). This script checks the certificate normally.
# If Device Portal asks for a username and password, you are prompted for them; they are only sent to the console.
param(
    [Parameter(Mandatory = $true)][string]$Xbox,
    [string]$Package,
    [int]$Port = 11443,
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'

if (-not $Package) {
    $root = Join-Path $PSScriptRoot '..\StremioXbox\AppPackages'
    # The highest version built (StremioXbox_<version>_x64.msix): the console only takes an update with a higher version.
    $Package = Get-ChildItem $root -Recurse -Filter 'StremioXbox_*_x64.msix' -ErrorAction SilentlyContinue |
        Sort-Object { try { [Version]($_.Name -replace '^StremioXbox_', '' -replace '_x64\.msix$', '') } catch { [Version]'0.0' } } -Descending |
        Select-Object -First 1 -ExpandProperty FullName
    if (-not $Package) { throw "No StremioXbox_*_x64.msix under $root. Build a Release x64 package first, or pass -Package." }
}
$Package = (Resolve-Path $Package).Path
# The x64 framework packages, minus the .Desktop VCLibs (desktop-only; the console doesn't take them).
$dependencies = @(Get-ChildItem (Join-Path (Split-Path $Package) 'Dependencies\x64') -Include *.appx, *.msix -Recurse -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -notlike '*.Desktop.*' })
Write-Host "Package:      $Package"
Write-Host ("Dependencies: " + $(if ($dependencies.Count) { ($dependencies | ForEach-Object Name) -join ', ' } else { 'none found' }))

. (Join-Path $PSScriptRoot 'XboxPortal.ps1')
$http = Connect-XboxPortal $Xbox $Port
$base = $http.Base

# Upload the package and its dependencies in one install request. The multipart body is written by hand:
# Device Portal does not read .NET's form encoding (unquoted file names plus a filename* field).
$boundary = [Guid]::NewGuid().ToString('N')
$body = New-Object System.IO.MemoryStream
function Write-Text([string]$text) { $bytes = [Text.Encoding]::UTF8.GetBytes($text); $body.Write($bytes, 0, $bytes.Length) }
foreach ($file in @($Package) + @($dependencies | ForEach-Object FullName)) {
    $leaf = Split-Path $file -Leaf
    Write-Text "--$boundary`r`nContent-Disposition: form-data; name=`"$leaf`"; filename=`"$leaf`"`r`nContent-Type: application/octet-stream`r`n`r`n"
    $bytes = [System.IO.File]::ReadAllBytes($file)
    $body.Write($bytes, 0, $bytes.Length)
    Write-Text "`r`n"
}
Write-Text "--$boundary--`r`n"
$form = New-Object System.Net.Http.ByteArrayContent(, $body.ToArray())
$form.Headers.TryAddWithoutValidation('Content-Type', "multipart/form-data; boundary=$boundary") | Out-Null
$name = [Uri]::EscapeDataString((Split-Path $Package -Leaf))
Write-Host ("Uploading {0:N1} MB..." -f ($body.Length / 1MB))
$upload = $http.Client.PostAsync("$base/api/app/packagemanager/package?package=$name", $form).GetAwaiter().GetResult()
if (-not $upload.IsSuccessStatusCode) {
    throw "Upload failed: $([int]$upload.StatusCode) $($upload.Content.ReadAsStringAsync().GetAwaiter().GetResult())"
}

# 204 = still installing, 200 = done (the body says whether it worked).
Write-Host "Installing..."
do {
    Start-Sleep -Seconds 2
    $state = $http.Client.GetAsync("$base/api/app/packagemanager/state").GetAwaiter().GetResult()
} while ([int]$state.StatusCode -eq 204)
$result = $state.Content.ReadAsStringAsync().GetAwaiter().GetResult()
if (-not $state.IsSuccessStatusCode) { throw "Install failed: $([int]$state.StatusCode) $result" }
# A 200 can still carry a refusal (e.g. the same version is already installed with different contents).
try { $outcome = $result | ConvertFrom-Json } catch { $outcome = $null }
if ($outcome -and $outcome.Success -eq $false) {
    $why = if ($outcome.Reason -match 'same identity') { ' Raise Version in StremioXbox\Package.appxmanifest and build again (an update needs a higher version).' } else { '' }
    throw "Install refused by the console: $($outcome.Reason)$why"
}
Write-Host "Installed.    $result"

if ($NoLaunch) { return }

# Start the app: Device Portal takes the app id and the package's full name, both base64-encoded.
$version = (Split-Path $Package -Leaf) -replace '^StremioXbox_', '' -replace '_x64\.msix$', ''
$app = Find-StremioPackage $http 10 $version
if (-not $app) { Write-Warning "Installed $version; start it from Dev Home."; return }
$appId = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($app.PackageRelativeId))
$full = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($app.PackageFullName))
$launch = $http.Client.PostAsync("$base/api/taskmanager/app?appid=$appId&package=$full", $null).GetAwaiter().GetResult()
Write-Host ("Launch:       " + $(if ($launch.IsSuccessStatusCode) { "started $($app.PackageFullName)" } else { "failed ($([int]$launch.StatusCode)); start it from Dev Home" }))
