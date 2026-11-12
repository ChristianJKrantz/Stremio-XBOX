# Shared Device Portal connection for the Xbox scripts (dot-sourced by Deploy-Xbox.ps1 and Get-XboxDiagnostics.ps1).
# The console's Device Portal certificate must be trusted on this PC; it is checked normally.

Add-Type -AssemblyName System.Net.Http

function New-PortalClient([System.Net.NetworkCredential]$credential) {
    $handler = New-Object System.Net.Http.HttpClientHandler
    $handler.CookieContainer = New-Object System.Net.CookieContainer
    $client = New-Object System.Net.Http.HttpClient($handler)
    $client.Timeout = [TimeSpan]::FromMinutes(10)
    if ($credential) {
        # Device Portal does not send a standard login challenge, so the login goes with every request (Basic, over HTTPS).
        # Scripts sign in as "auto-<username>": that is how Device Portal tells tools from its own web page, and
        # without the prefix it refuses the correct password (Microsoft's Device Portal tools do the same).
        $name = if ($credential.UserName -like 'auto-*') { $credential.UserName } else { 'auto-' + $credential.UserName }
        $pair = [Text.Encoding]::UTF8.GetBytes($name + ':' + $credential.Password)
        $client.DefaultRequestHeaders.Authorization = New-Object System.Net.Http.Headers.AuthenticationHeaderValue('Basic', [Convert]::ToBase64String($pair))
    }
    return @{ Client = $client; Cookies = $handler.CookieContainer }
}

# Connects, asking for the Device Portal login in the terminal only if the console wants one (password hidden).
function Connect-XboxPortal([string]$Xbox, [int]$Port = 11443) {
    $base = "https://${Xbox}:$Port"
    $http = New-PortalClient $null
    $probe = $http.Client.GetAsync("$base/api/os/info").GetAwaiter().GetResult()
    for ($attempt = 1; [int]$probe.StatusCode -eq 401 -and $attempt -le 3; $attempt++) {
        Write-Host $(if ($attempt -eq 1) { "Device Portal login for $Xbox (set under Remote Access in Dev Home on the Xbox):" } else { 'That login was refused. Try again:' })
        $user = Read-Host '  Username'
        $password = Read-Host '  Password' -AsSecureString
        if (-not $user) { throw 'No username entered.' }
        $http = New-PortalClient (New-Object System.Net.NetworkCredential($user, $password))
        $probe = $http.Client.GetAsync("$base/api/os/info").GetAwaiter().GetResult()
    }
    if (-not $probe.IsSuccessStatusCode) {
        $hint = if ([int]$probe.StatusCode -eq 401) { ' (check the username and password set under Remote Access in Dev Home on the Xbox)' } else { '' }
        throw "Device Portal answered $([int]$probe.StatusCode) $($probe.ReasonPhrase)$hint"
    }
    Write-Host ("Connected:    " + $probe.Content.ReadAsStringAsync().GetAwaiter().GetResult())

    # Device Portal wants its CSRF cookie echoed back as a header on changes.
    $csrf = $http.Cookies.GetCookies([Uri]$base) | Where-Object Name -eq 'CSRF-Token' | Select-Object -First 1
    if ($csrf) { $http.Client.DefaultRequestHeaders.Add('X-CSRF-Token', $csrf.Value) }
    $http.Base = $base
    return $http
}

function Get-PortalText($http, [string]$path) {
    $response = $http.Client.GetAsync($http.Base + $path).GetAwaiter().GetResult()
    return @{ Ok = $response.IsSuccessStatusCode; Status = [int]$response.StatusCode; Text = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult() }
}

# The installed Stremio-XBOX package (the list can lag an install by a few seconds, so look a few times).
# With -Version, waits for that exact version (right after an update the list can still show the old one).
function Find-StremioPackage($http, [int]$Tries = 8, [string]$Version) {
    $list = @()
    for ($try = 0; $try -lt $Tries; $try++) {
        if ($try) { Start-Sleep -Seconds 2 }
        $result = Get-PortalText $http '/api/app/packagemanager/packages'
        try { $list = @(($result.Text | ConvertFrom-Json).InstalledPackages) } catch { $list = @() }
        $app = $list | Where-Object { "$($_.PackageFamilyName) $($_.PackageFullName) $($_.Name)" -match 'Stremio' -and
            (-not $Version -or $_.PackageFullName -like "*_${Version}_*") } | Select-Object -First 1
        if ($app) { return $app }
    }
    Write-Warning ("No Stremio package in the console's list ({0} packages listed)." -f $list.Count)
    return $null
}
