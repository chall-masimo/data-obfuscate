<#
.SYNOPSIS
    Checks a CSV Masker appsettings.Production.json before go-live. Read-only.

.DESCRIPTION
    Validates what the app itself refuses to start without, plus things it can't check for
    itself:
      - Authorization:AllowedGroup is set, isn't the template placeholder, looks like
        DOMAIN\Group, and resolves to a real group (on a domain-joined machine).
      - Storage:TempFolder and Storage:RecipeFolder are absolute, separate, and not inside
        each other, the app folder, or its wwwroot.
      - Limits:MaxUploadBytes (if overridden) equals web.config's maxAllowedContentLength.

    Exits 1 if any check fails, so it can gate a deployment script.

.EXAMPLE
    .\Test-CsvMaskerSettings.ps1 -Path C:\Docs\csv_masker\app\appsettings.Production.json -AppFolder C:\Docs\csv_masker\app
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Path,

    # The deployed app folder (where web.config and wwwroot live). Optional.
    [string] $AppFolder
)

$ErrorActionPreference = 'Stop'
$script:results = New-Object System.Collections.Generic.List[object]

function Add-Result([string] $Status, [string] $Check, [string] $Detail) {
    $script:results.Add([pscustomobject]@{ Status = $Status; Check = $Check; Detail = $Detail })
}

function Test-Inside([string] $Child, [string] $Parent) {
    if ([string]::IsNullOrWhiteSpace($Child) -or [string]::IsNullOrWhiteSpace($Parent)) { return $false }
    $c = [System.IO.Path]::GetFullPath($Child).TrimEnd('\') + '\'
    $p = [System.IO.Path]::GetFullPath($Parent).TrimEnd('\') + '\'
    return $c.StartsWith($p, [System.StringComparison]::OrdinalIgnoreCase)
}

# --- Parse -------------------------------------------------------------------------------
if (-not (Test-Path -LiteralPath $Path)) {
    Add-Result 'FAIL' 'Settings file' "Not found: $Path"
    $script:results | Format-Table -AutoSize -Wrap
    exit 1
}
try {
    $settings = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    Add-Result 'PASS' 'Settings file' 'Valid JSON'
}
catch {
    Add-Result 'FAIL' 'Settings file' "Not valid JSON: $($_.Exception.Message)"
    $script:results | Format-Table -AutoSize -Wrap
    exit 1
}

# --- AD group ------------------------------------------------------------------------------
$group = $null
if ($settings.Authorization) { $group = $settings.Authorization.AllowedGroup }
if ([string]::IsNullOrWhiteSpace($group)) {
    Add-Result 'FAIL' 'Authorization:AllowedGroup' 'Not set: the app will refuse to start (HTTP 500.30).'
}
elseif ($group -like 'CHANGE-ME*') {
    Add-Result 'FAIL' 'Authorization:AllowedGroup' "Still the template placeholder '$group'."
}
elseif ($group -notmatch '^[^\\]+\\[^\\]+$') {
    Add-Result 'FAIL' 'Authorization:AllowedGroup' "'$group' isn't in DOMAIN\Group form."
}
else {
    try {
        $sid = (New-Object System.Security.Principal.NTAccount($group)).Translate([System.Security.Principal.SecurityIdentifier])
        Add-Result 'PASS' 'Authorization:AllowedGroup' "'$group' resolves ($($sid.Value))."
    }
    catch {
        Add-Result 'WARN' 'Authorization:AllowedGroup' "'$group' could not be resolved from this machine. Check the spelling, or run this on the domain-joined server."
    }
}

# --- Folders -------------------------------------------------------------------------------
$temp = $null; $recipes = $null
if ($settings.Storage) { $temp = $settings.Storage.TempFolder; $recipes = $settings.Storage.RecipeFolder }
$webRoot = $null
if ($AppFolder) { $webRoot = Join-Path $AppFolder 'wwwroot' }

foreach ($folder in @(@{ Name = 'Storage:TempFolder'; Value = $temp; Required = $false }, @{ Name = 'Storage:RecipeFolder'; Value = $recipes; Required = $true })) {
    $value = $folder.Value
    if ([string]::IsNullOrWhiteSpace($value)) {
        if ($folder.Required) { Add-Result 'FAIL' $folder.Name 'Not set: the app will refuse to start (HTTP 500.30).' }
        else { Add-Result 'WARN' $folder.Name "Not set: the app pool's %TEMP%\CsvMasker will be used. Set it explicitly so permissions can be locked down." }
        continue
    }
    if (-not [System.IO.Path]::IsPathRooted($value)) {
        Add-Result 'FAIL' $folder.Name "'$value' is not an absolute path."
        continue
    }
    if ($AppFolder -and (Test-Inside $value $AppFolder)) {
        $status = 'WARN'
        if (Test-Inside $value $webRoot) { $status = 'FAIL' }
        Add-Result $status $folder.Name "'$value' is inside the app folder; keep data outside $AppFolder (a deploy overwrites it; wwwroot is served)."
        continue
    }
    if (Test-Path -LiteralPath $value) { Add-Result 'PASS' $folder.Name "'$value' exists." }
    else { Add-Result 'WARN' $folder.Name "'$value' doesn't exist yet. Create it and grant the app pool Modify (DEPLOY_IIS.md steps 3 and 5)." }
}

if ($temp -and $recipes -and ((Test-Inside $temp $recipes) -or (Test-Inside $recipes $temp))) {
    Add-Result 'FAIL' 'Separate folders' 'TempFolder and RecipeFolder overlap. The sweeper deletes old files in the temp folder, so recipes would be lost; the app refuses to start.'
}
elseif ($temp -and $recipes) {
    Add-Result 'PASS' 'Separate folders' 'Temp and recipe folders are separate.'
}

# --- Upload limit vs web.config ------------------------------------------------------------------
$appLimit = 209715200
if ($settings.Limits -and $settings.Limits.MaxUploadBytes) { $appLimit = [long]$settings.Limits.MaxUploadBytes }
if ($AppFolder -and (Test-Path -LiteralPath (Join-Path $AppFolder 'web.config'))) {
    [xml]$webConfig = Get-Content -LiteralPath (Join-Path $AppFolder 'web.config') -Raw
    $node = $webConfig.SelectSingleNode('//requestLimits')
    if ($null -eq $node) {
        Add-Result 'FAIL' 'Upload limit' 'web.config has no requestLimits: IIS will reject uploads over ~28.6 MB.'
    }
    elseif ([long]$node.maxAllowedContentLength -ne $appLimit) {
        Add-Result 'FAIL' 'Upload limit' "web.config allows $($node.maxAllowedContentLength) bytes but the app allows $appLimit. They must match."
    }
    else {
        Add-Result 'PASS' 'Upload limit' "web.config and app both allow $appLimit bytes."
    }
}
elseif ($settings.Limits -and $settings.Limits.MaxUploadBytes) {
    Add-Result 'WARN' 'Upload limit' 'Limits:MaxUploadBytes is overridden; pass -AppFolder to check it against web.config.'
}

$script:results | Format-Table -AutoSize -Wrap
if ($script:results | Where-Object Status -eq 'FAIL') { exit 1 }
exit 0
