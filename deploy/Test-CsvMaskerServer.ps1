<#
.SYNOPSIS
    Read-only pre-flight check of a CSV Masker IIS server (DEPLOY_IIS.md steps 1-8).

.DESCRIPTION
    Reports PASS / WARN / FAIL for each setup step. It only reads: it never installs, creates,
    changes permissions or restarts anything. Run it in an elevated Windows PowerShell on the
    server (reading IIS configuration needs administrator rights).

      1. IIS and Windows Authentication features installed
      2. ASP.NET Core Module V2 and the .NET 10 ASP.NET Core runtime (Hosting Bundle)
      3. App, temp and recipe folders exist
      4. App pool settings (no managed runtime, memory recycle limit, no timed recycle, user profile)
      5. Folder permissions for the app pool identity
      6. IIS application at the expected path, using the app pool and folder
      7. Windows authentication on, anonymous off; HTTPS binding (or -AllowHttp)
      8. Deployed files, web.config upload limit, appsettings.Production.json (via Test-CsvMaskerSettings.ps1)

    Exits 1 if any check fails.

.EXAMPLE
    .\Test-CsvMaskerServer.ps1
.EXAMPLE
    .\Test-CsvMaskerServer.ps1 -Site 'Reports' -AppName 'csvmasker' -AppFolder 'E:\Sites\CsvMasker'
#>
[CmdletBinding()]
param(
    [string] $Site = 'Default Web Site',
    [string] $AppName = 'csvmasker',
    [string] $AppPool = 'CsvMasker',
    [string] $AppFolder = 'C:\Docs\csv_masker\app',
    # Default to the values in appsettings.Production.json; pass to override.
    [string] $TempFolder,
    [string] $RecipeFolder,

    # The deployment runs over HTTP by decision (see DEPLOY_IIS.md step 7): a missing HTTPS binding is a WARN, not a FAIL.
    [switch] $AllowHttp
)

$ErrorActionPreference = 'Stop'
$script:results = New-Object System.Collections.Generic.List[object]
$identity = "IIS AppPool\$AppPool"

function Add-Result([string] $Status, [string] $Step, [string] $Check, [string] $Detail) {
    $script:results.Add([pscustomobject]@{ Status = $Status; Step = $Step; Check = $Check; Detail = $Detail })
}

function Test-Rights([string] $Folder, [System.Security.AccessControl.FileSystemRights] $Needed) {
    $acl = Get-Acl -LiteralPath $Folder
    $granted = 0
    foreach ($rule in $acl.Access) {
        if ($rule.AccessControlType -eq 'Allow' -and $rule.IdentityReference.Value -ieq $identity) {
            $granted = $granted -bor [int]$rule.FileSystemRights
        }
    }
    return (($granted -band [int]$Needed) -eq [int]$Needed)
}

# --- Elevation --------------------------------------------------------------------------------------
$principal = New-Object System.Security.Principal.WindowsPrincipal([System.Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Add-Result 'WARN' '-' 'Elevation' 'Not running as administrator: IIS configuration checks may fail or be skipped.'
}

# --- Step 1: features ---------------------------------------------------------------------------------
if (Get-Command Get-WindowsFeature -ErrorAction SilentlyContinue) {
    foreach ($feature in 'Web-Server', 'Web-Windows-Auth') {
        $state = (Get-WindowsFeature -Name $feature).InstallState
        if ($state -eq 'Installed') { Add-Result 'PASS' '1' $feature 'Installed' }
        else { Add-Result 'FAIL' '1' $feature "InstallState: $state (Install-WindowsFeature $feature)" }
    }
}
else {
    Add-Result 'WARN' '1' 'Windows features' 'Get-WindowsFeature not available (not Windows Server?); skipped.'
}

# --- Step 2: Hosting Bundle -----------------------------------------------------------------------------
# Ask IIS where the module is registered (normally %ProgramFiles%\IIS\Asp.Net Core Module\V2\aspnetcorev2.dll)
# rather than guessing a folder, then confirm the file is really there.
$ancmModule = $null
if (Get-Module -ListAvailable WebAdministration) {
    Import-Module WebAdministration -ErrorAction SilentlyContinue
    $ancmModule = Get-WebGlobalModule -Name AspNetCoreModuleV2 -ErrorAction SilentlyContinue
}
if ($ancmModule) {
    $ancmImage = [Environment]::ExpandEnvironmentVariables($ancmModule.Image)
    if (Test-Path -LiteralPath $ancmImage) { Add-Result 'PASS' '2' 'ASP.NET Core Module V2' "Registered in IIS: $ancmImage" }
    else { Add-Result 'FAIL' '2' 'ASP.NET Core Module V2' "Registered in IIS but $ancmImage is missing: repair the .NET 10 Hosting Bundle, then restart IIS." }
}
else {
    Add-Result 'FAIL' '2' 'ASP.NET Core Module V2' 'Not registered in IIS. Install the .NET 10 Hosting Bundle (not just the runtime); if it was installed before IIS, run it again and choose Repair. Then restart IIS.'
}

$dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
if (Test-Path -LiteralPath $dotnet) {
    $runtimes = & $dotnet --list-runtimes 2>$null
    $aspnet10 = $runtimes | Where-Object { $_ -match '^Microsoft\.AspNetCore\.App 10\.' }
    if ($aspnet10) { Add-Result 'PASS' '2' 'ASP.NET Core 10 runtime' (($aspnet10 | Select-Object -Last 1) -split ' \[')[0] }
    else { Add-Result 'FAIL' '2' 'ASP.NET Core 10 runtime' 'Microsoft.AspNetCore.App 10.x not installed (Hosting Bundle).' }
}
else {
    Add-Result 'FAIL' '2' 'ASP.NET Core 10 runtime' "$dotnet not found (Hosting Bundle)."
}

# --- Settings (folders come from appsettings.Production.json) ----------------------------------------------
$settingsPath = Join-Path $AppFolder 'appsettings.Production.json'
$settings = $null
if (Test-Path -LiteralPath $settingsPath) {
    try { $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json } catch { }
}
if (-not $TempFolder -and $settings -and $settings.Storage) { $TempFolder = $settings.Storage.TempFolder }
if (-not $RecipeFolder -and $settings -and $settings.Storage) { $RecipeFolder = $settings.Storage.RecipeFolder }

# --- Steps 3 and 5: folders and permissions ------------------------------------------------------------------
$folders = @(
    @{ Name = 'App folder'; Path = $AppFolder; Rights = [System.Security.AccessControl.FileSystemRights]::ReadAndExecute; RightsName = 'Read & execute' },
    @{ Name = 'Temp folder'; Path = $TempFolder; Rights = [System.Security.AccessControl.FileSystemRights]::Modify; RightsName = 'Modify' },
    @{ Name = 'Recipe folder'; Path = $RecipeFolder; Rights = [System.Security.AccessControl.FileSystemRights]::Modify; RightsName = 'Modify' }
)
foreach ($folder in $folders) {
    if ([string]::IsNullOrWhiteSpace($folder.Path)) {
        Add-Result 'FAIL' '3' $folder.Name 'Unknown: not set in appsettings.Production.json and not passed as a parameter.'
        continue
    }
    if (-not (Test-Path -LiteralPath $folder.Path)) {
        Add-Result 'FAIL' '3' $folder.Name "$($folder.Path) does not exist."
        continue
    }
    Add-Result 'PASS' '3' $folder.Name $folder.Path
    try {
        if (Test-Rights $folder.Path $folder.Rights) { Add-Result 'PASS' '5' "$($folder.Name) permissions" "$identity has $($folder.RightsName)." }
        else { Add-Result 'FAIL' '5' "$($folder.Name) permissions" "$identity lacks $($folder.RightsName) (icacls, DEPLOY_IIS.md step 5)." }
    }
    catch {
        Add-Result 'WARN' '5' "$($folder.Name) permissions" "Could not read the ACL: $($_.Exception.Message)"
    }
}
if ($TempFolder -and (Test-Path -LiteralPath $TempFolder)) {
    $broad = (Get-Acl -LiteralPath $TempFolder).Access | Where-Object { $_.AccessControlType -eq 'Allow' -and $_.IdentityReference.Value -match '\\(Users|Everyone|Authenticated Users)$' }
    if ($broad) { Add-Result 'WARN' '5' 'Temp folder lockdown' "Broad groups can read the temp folder ($((($broad | ForEach-Object { $_.IdentityReference.Value }) | Sort-Object -Unique) -join ', ')). Raw uploads sit there briefly; lock it down (DEPLOY_IIS.md step 5)." }
    else { Add-Result 'PASS' '5' 'Temp folder lockdown' 'No broad user groups on the temp folder.' }
}

# --- Steps 4, 6, 7: IIS configuration ------------------------------------------------------------------------
$iis = $false
try { Import-Module WebAdministration -ErrorAction Stop; $iis = $true }
catch { Add-Result 'FAIL' '4' 'WebAdministration module' 'Not available: is IIS installed (with the management tools)?' }

if ($iis) {
    $poolPath = "IIS:\AppPools\$AppPool"
    if (-not (Test-Path $poolPath)) {
        Add-Result 'FAIL' '4' "App pool '$AppPool'" 'Does not exist (DEPLOY_IIS.md step 4).'
    }
    else {
        $pool = Get-Item $poolPath
        if ([string]::IsNullOrEmpty($pool.managedRuntimeVersion)) { Add-Result 'PASS' '4' 'Managed runtime' 'No Managed Code' }
        else { Add-Result 'FAIL' '4' 'Managed runtime' "Is '$($pool.managedRuntimeVersion)'; should be empty (No Managed Code)." }

        $memory = (Get-ItemProperty $poolPath -Name recycling.periodicRestart.privateMemory).Value
        if ($memory -gt 0) { Add-Result 'PASS' '4' 'Private memory limit' "$([math]::Round($memory / 1024)) MB" }
        else { Add-Result 'WARN' '4' 'Private memory limit' 'Not set: a runaway job could use unlimited memory on the shared server.' }

        $time = (Get-ItemProperty $poolPath -Name recycling.periodicRestart.time).Value
        if ($time -eq [TimeSpan]::Zero) { Add-Result 'PASS' '4' 'Timed recycle' 'Disabled' }
        else { Add-Result 'WARN' '4' 'Timed recycle' "Every $($time): a recycle mid-job cancels it. DEPLOY_IIS.md sets 00:00:00." }

        $loadProfile = (Get-ItemProperty $poolPath -Name processModel.loadUserProfile).Value
        if ($loadProfile) { Add-Result 'PASS' '4' 'Load user profile' 'True' }
        else { Add-Result 'FAIL' '4' 'Load user profile' 'False: antiforgery keys won''t persist across restarts.' }
    }

    $app = Get-WebApplication -Site $Site -Name $AppName -ErrorAction SilentlyContinue
    if (-not $app) {
        Add-Result 'FAIL' '6' "IIS application /$AppName" "Not found under site '$Site' (DEPLOY_IIS.md step 6)."
    }
    else {
        $physical = [Environment]::ExpandEnvironmentVariables($app.PhysicalPath).TrimEnd('\')
        if ($physical -ieq $AppFolder.TrimEnd('\')) { Add-Result 'PASS' '6' 'Physical path' $physical }
        else { Add-Result 'FAIL' '6' 'Physical path' "Is '$physical'; expected '$AppFolder'." }
        if ($app.ApplicationPool -ieq $AppPool) { Add-Result 'PASS' '6' 'Application pool' $AppPool }
        else { Add-Result 'FAIL' '6' 'Application pool' "Is '$($app.ApplicationPool)'; expected '$AppPool'." }

        $location = "$Site/$AppName"
        $anonymous = (Get-WebConfigurationProperty -PSPath 'IIS:\' -Location $location -Filter 'system.webServer/security/authentication/anonymousAuthentication' -Name enabled).Value
        $windows = (Get-WebConfigurationProperty -PSPath 'IIS:\' -Location $location -Filter 'system.webServer/security/authentication/windowsAuthentication' -Name enabled).Value
        if (-not $anonymous) { Add-Result 'PASS' '7' 'Anonymous authentication' 'Disabled' }
        else { Add-Result 'FAIL' '7' 'Anonymous authentication' 'Enabled: must be off (DEPLOY_IIS.md step 7).' }
        if ($windows) { Add-Result 'PASS' '7' 'Windows authentication' 'Enabled' }
        else { Add-Result 'FAIL' '7' 'Windows authentication' 'Disabled: must be on (DEPLOY_IIS.md step 7).' }
    }

    $https = Get-WebBinding -Name $Site -Protocol https -ErrorAction SilentlyContinue
    if ($https) { Add-Result 'PASS' '7' 'HTTPS binding' (($https | ForEach-Object { $_.bindingInformation }) -join '; ') }
    elseif ($AllowHttp) { Add-Result 'WARN' '7' 'HTTPS binding' "Site '$Site' has no HTTPS binding: uploads and downloads travel unencrypted (accepted with -AllowHttp)." }
    else { Add-Result 'FAIL' '7' 'HTTPS binding' "Site '$Site' has no HTTPS binding. Add one, or pass -AllowHttp if HTTP is a deliberate decision." }
    if ($https -and (Get-WebBinding -Name $Site -Protocol http -ErrorAction SilentlyContinue)) {
        Add-Result 'WARN' '7' 'HTTP binding' 'The site also answers on HTTP. The app redirects to HTTPS, but consider removing the binding if nothing else needs it.'
    }
}

# --- Step 8 / 9: deployed files and settings ----------------------------------------------------------------
if (Test-Path -LiteralPath $AppFolder) {
    foreach ($file in 'CsvMasker.Web.dll', 'web.config') {
        if (Test-Path -LiteralPath (Join-Path $AppFolder $file)) { Add-Result 'PASS' '8' $file 'Present' }
        else { Add-Result 'FAIL' '8' $file 'Missing: publish and copy the app (DEPLOY_IIS.md step 9).' }
    }
    if (Test-Path -LiteralPath (Join-Path $AppFolder 'app_offline.htm')) {
        Add-Result 'WARN' '8' 'app_offline.htm' 'Present: the app is offline until it is deleted.'
    }
    if (Test-Path -LiteralPath (Join-Path $AppFolder 'appsettings.Development.json')) {
        Add-Result 'PASS' '8' 'appsettings.Development.json' 'Present but unused (only read when ASPNETCORE_ENVIRONMENT=Development).'
    }
}

$settingsScript = Join-Path $PSScriptRoot 'Test-CsvMaskerSettings.ps1'
if (-not (Test-Path -LiteralPath $settingsPath)) {
    Add-Result 'FAIL' '8' 'appsettings.Production.json' "Not found in $AppFolder. Copy deploy\appsettings.Production.template.json and fill it in."
}
elseif (Test-Path -LiteralPath $settingsScript) {
    $output = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $settingsScript -Path $settingsPath -AppFolder $AppFolder 2>&1
    if ($LASTEXITCODE -eq 0) { Add-Result 'PASS' '8' 'appsettings.Production.json' 'Test-CsvMaskerSettings.ps1 passed.' }
    else { Add-Result 'FAIL' '8' 'appsettings.Production.json' "Test-CsvMaskerSettings.ps1 failed:`n$(($output | Out-String).Trim())" }
}

$script:results | Format-Table Status, Step, Check, Detail -AutoSize -Wrap
$failed = @($script:results | Where-Object Status -eq 'FAIL').Count
$warned = @($script:results | Where-Object Status -eq 'WARN').Count
Write-Host "$failed failed, $warned warning(s)."
if ($failed -gt 0) { exit 1 }
exit 0
