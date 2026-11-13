# Saves the Device Portal login for one Xbox, so Deploy-Xbox.ps1, Get-XboxDiagnostics.ps1 and Watch-Xbox.ps1 stop
# asking for it.
#
#   powershell -ExecutionPolicy Bypass -File scripts\Save-XboxLogin.ps1 -Xbox 192.168.1.50
#   powershell -ExecutionPolicy Bypass -File scripts\Save-XboxLogin.ps1 -Xbox 192.168.1.50 -Forget
#
# The login is checked against the console first, then kept in %LOCALAPPDATA%\StremioXbox (never in the repository),
# with the password encrypted by Windows (DPAPI) so only this Windows account on this PC can read it. -Forget
# deletes it.
param(
    [Parameter(Mandatory = $true)][string]$Xbox,
    [int]$Port = 11443,
    [switch]$Forget
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'XboxPortal.ps1')
$path = Get-XboxLoginPath $Xbox

if ($Forget) {
    if (Test-Path $path) {
        Remove-Item $path -Force
        Write-Host "Deleted the saved login for $Xbox."
    } else {
        Write-Host "No saved login for $Xbox."
    }
    return
}

$base = "https://${Xbox}:$Port"
Write-Host "Device Portal login for $Xbox (set under Remote Access in Dev Home on the Xbox), to save on this PC:"
for ($attempt = 1; $attempt -le 3; $attempt++) {
    $user = Read-Host '  Username'
    if (-not $user) { throw 'No username entered.' }
    $password = Read-Host '  Password' -AsSecureString
    $credential = New-Object System.Management.Automation.PSCredential($user, $password)
    $http = New-PortalClient $credential.GetNetworkCredential()
    $probe = $http.Client.GetAsync("$base/api/os/info").GetAwaiter().GetResult()
    if ($probe.IsSuccessStatusCode) {
        New-Item -ItemType Directory -Force (Split-Path $path) | Out-Null
        $credential | Export-Clixml $path
        Write-Host "Saved. The Xbox scripts now log in to $Xbox on their own (delete with -Forget)."
        return
    }
    if ([int]$probe.StatusCode -ne 401) { throw "Device Portal answered $([int]$probe.StatusCode) $($probe.ReasonPhrase)" }
    Write-Host 'That login was refused. Try again:'
}
throw 'Login refused three times; nothing saved.'
