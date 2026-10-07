# CSV Masker — IIS Setup & Deployment (Windows Server 2022)

Assumes IIS is already running on the server with an existing site (examples use
`Default Web Site`). The app is deployed as an IIS **application** at `/csvmasker`. Run all
PowerShell in an elevated session on the server.

---

## Step 1 — Confirm required IIS features

IIS is already installed. Windows Authentication is a separate feature that may not be.

```powershell
Get-WindowsFeature Web-Server, Web-Windows-Auth, Web-Mgmt-Console | Format-Table Name, InstallState
```

Install anything missing (no restart normally required):

```powershell
Install-WindowsFeature Web-Windows-Auth, Web-Mgmt-Console
```

---

## Step 2 — Install the ASP.NET Core Hosting Bundle (.NET 10)

1. On a machine with internet access, download the **Hosting Bundle** for the current
   .NET 10 release from https://dotnet.microsoft.com/download/dotnet/10.0 (under
   "ASP.NET Core Runtime" → Windows → *Hosting Bundle*).
2. Copy it to the server and run it. **Plan a quiet window:** IIS needs a restart afterward,
   which briefly interrupts other hosted pages (SSRS has its own HTTP service and is not
   hosted in IIS, so it's unaffected).
3. Restart IIS so it picks up the new module:

```powershell
net stop was /y
net start w3svc
```

`net stop was` stops dependent services too. If any other web-related service was
running before, confirm it came back afterward (`Get-Service W3SVC, WAS`).

4. Verify:

```powershell
& "$env:windir\system32\inetsrv\appcmd.exe" list modules | Select-String AspNetCore
dotnet --list-runtimes
```

You should see `AspNetCoreModuleV2` and a `Microsoft.AspNetCore.App 10.x` runtime.

---

## Step 3 — Create folders

Everything lives under one root, `C:\Docs\csv_masker`, in three **sibling** folders. Temp and
recipes must not be inside the app folder: a deploy overwrites the app folder, and `wwwroot`
is served. Recipes must not be inside temp either, because the sweeper empties it.

```powershell
New-Item -ItemType Directory -Force -Path "C:\Docs\csv_masker\app"      # app binaries (IIS physical path)
New-Item -ItemType Directory -Force -Path "C:\Docs\csv_masker\temp"     # uploads/outputs (transient)
New-Item -ItemType Directory -Force -Path "C:\Docs\csv_masker\recipes"  # saved configs (no data)
```

---

## Step 4 — Create a dedicated app pool

```powershell
Import-Module WebAdministration

New-WebAppPool -Name "CsvMasker"

# .NET Core apps don't use the CLR loaded by IIS
Set-ItemProperty IIS:\AppPools\CsvMasker -Name managedRuntimeVersion -Value ""

# Recycle if private memory exceeds 1 GB (value in KB). Protects SQL Server & neighbors.
Set-ItemProperty IIS:\AppPools\CsvMasker -Name recycling.periodicRestart.privateMemory -Value 1048576

# Disable the default 29-hour scheduled recycle (avoids killing a job mid-run at a random time)
Set-ItemProperty IIS:\AppPools\CsvMasker -Name recycling.periodicRestart.time -Value "00:00:00"

# Needed so ASP.NET Core Data Protection keys (antiforgery tokens) persist across restarts
Set-ItemProperty IIS:\AppPools\CsvMasker -Name processModel.loadUserProfile -Value $true
```

Identity stays the default `ApplicationPoolIdentity` (`IIS AppPool\CsvMasker`). It needs no
SQL access, since the app has no database.

Adjust the 1 GB limit to what the server can spare. Keep the app's
`Limits:MaxMappingEntries` setting comfortably below what that limit allows, so the app
fails a job cleanly before IIS recycles the pool.

---

## Step 5 — Set folder permissions

Folders under the root of `C:\` inherit a rule giving **every authenticated user Modify** on
them. Raw sensitive uploads sit in `temp` briefly, so lock the root down first. Remove
inherited access, leaving only Administrators and SYSTEM, then grant the app pool what it
needs:

```powershell
$root = "C:\Docs\csv_masker"
$id   = "IIS AppPool\CsvMasker"

# Root: no inherited access; only Administrators and SYSTEM (inherited by the three subfolders).
icacls $root /inheritance:r /grant:r "*S-1-5-32-544:(OI)(CI)F" "*S-1-5-18:(OI)(CI)F"

icacls "$root\app"     /grant "${id}:(OI)(CI)RX"   # read & execute the binaries
icacls "$root\temp"    /grant "${id}:(OI)(CI)M"    # write uploads and outputs
icacls "$root\recipes" /grant "${id}:(OI)(CI)M"    # save recipes
```

(`*S-1-5-32-544` and `*S-1-5-18` are the built-in Administrators group and SYSTEM, by SID, so
the commands work on any server language.) Check the result with `icacls $root\temp`. It
should list only Administrators, SYSTEM and `IIS AppPool\CsvMasker`, with no `Users` or
`Authenticated Users`. The pre-flight script (step 10) flags this as "Temp folder lockdown".

---

## Step 6 — Create the IIS application

```powershell
New-WebApplication -Site "Default Web Site" -Name "csvmasker" `
    -PhysicalPath "C:\Docs\csv_masker\app" -ApplicationPool "CsvMasker"
```

The app will live at `http://<server>/csvmasker`. ASP.NET Core picks up the `/csvmasker`
path base automatically under IIS.

---

## Step 7 — Authentication: Windows on, Anonymous off

```powershell
$loc = "Default Web Site/csvmasker"

Set-WebConfigurationProperty -PSPath IIS:\ -Location $loc `
    -Filter /system.webServer/security/authentication/anonymousAuthentication `
    -Name enabled -Value $false

Set-WebConfigurationProperty -PSPath IIS:\ -Location $loc `
    -Filter /system.webServer/security/authentication/windowsAuthentication `
    -Name enabled -Value $true
```

These are written to `applicationHost.config` (the auth sections are locked at the server
level by default, which is why they're not set in the app's `web.config`). Restricting access
to your team's AD group is done inside the app (`Authorization:AllowedGroup`), so no IIS URL
Authorization feature is needed.

**HTTP vs HTTPS (decision: HTTP).** The site has only an HTTP (`:80`) binding, and the tool
is deployed over HTTP, like the server's other internal web apps. Windows sign-in works the
same way over HTTP: passwords are never sent. The accepted risk is that **uploaded files,
masked downloads and the review/preview pages (which show sample values) travel unencrypted on
the internal network**. Revisit this before routinely masking regulated data (e.g. patient
data).

To add HTTPS later:
1. Get a certificate from the internal CA for the server's short and fully qualified names.
2. Check port 443 isn't already used by SSRS (`netsh http show sslcert ipport=0.0.0.0:443`).
3. Add an `https` binding on the site with that certificate.

No app change is needed: it starts redirecting HTTP to HTTPS on its own once an HTTPS binding
exists. It deliberately sends no HSTS header, because HSTS covers the whole host name and would
break the server's other HTTP-only apps for anyone who had used this tool.

---

## Step 8 — Upload size limit

IIS rejects requests over 30,000,000 bytes (~28.6 MB) by default, before the app sees them.
The project ships its own `web.config` (`src/CsvMasker.Web/web.config`) with the limit set to
200 MB, the agreed maximum. Publish keeps it. Its request-filtering section:

```xml
<configuration>
  <system.webServer>
    <security>
      <requestFiltering>
        <requestLimits maxAllowedContentLength="209715200" />
      </requestFiltering>
    </security>
  </system.webServer>
</configuration>
```

This value must match `Limits:MaxUploadBytes` in `appsettings.json` (a unit test checks the
two files agree). To change the limit, change both, and change the server's
`appsettings.Production.json` if it overrides the value. The app applies the same value to
its own limits (`IISServerOptions.MaxRequestBodySize`, Kestrel, and
`FormOptions.MultipartBodyLengthLimit`) and checks the size again while streaming. If uploads
fail with a 404.13 error, IIS is the one rejecting them. If the page says "larger than the …
limit", it's the app.

Masking runs on background workers; `Limits:MaxConcurrentJobs` (default 2) caps how many run
at once across all users, to protect the server's other workloads.

---

## Step 9 — Build and deploy

On your dev machine:

```powershell
dotnet publish .\src\CsvMasker.Web -c Release -o .\publish
```

This produces a framework-dependent publish, which uses the runtime from the Hosting Bundle
and keeps the deployment small.

To deploy, use the `app_offline.htm` trick so IIS releases file locks:

1. Copy a file named `app_offline.htm` into `C:\Docs\csv_masker\app`. The app shuts down and
   IIS serves that page.
2. Copy the contents of `.\publish` into `C:\Docs\csv_masker\app`, overwriting.
3. Delete `app_offline.htm`. The app starts on the next request.

Put the server's values in `C:\Docs\csv_masker\app\appsettings.Production.json`, starting from
`deploy\appsettings.Production.template.json` in the repo. The publish doesn't include that
file, so a deploy never overwrites it, and git ignores any real one. **`Authorization:AllowedGroup`
and `Storage:RecipeFolder` are required:** without either the app refuses to start, and IIS
shows HTTP 500.30 with the reason in Event Viewer. The recipe folder must not be inside the
temp folder (the sweeper empties that).

```json
{
  "Authorization": { "AllowedGroup": "DOMAIN\\SalesOps-CsvMasker" },
  "Storage": {
    "TempFolder": "C:\\Docs\\csv_masker\\temp",
    "RecipeFolder": "C:\\Docs\\csv_masker\\recipes"
  }
}
```

Check the file before starting the app (copy the `deploy` folder to the server; the
scripts are read-only):

```powershell
.\deploy\Test-CsvMaskerSettings.ps1 -Path C:\Docs\csv_masker\app\appsettings.Production.json -AppFolder C:\Docs\csv_masker\app
```

It fails on a missing or placeholder group, a group that doesn't resolve in AD, overlapping or
relative folders, folders inside the app folder, and an upload limit that doesn't match
web.config.

Recipes are plain JSON files (one per recipe, no data inside). Back up
`C:\Docs\csv_masker\recipes` with the server's normal backups, or copy files between servers to
share recipes.

Use the `DOMAIN\Group` form; matching is case-insensitive and includes nested groups.
Membership is read from the user's logon token, so someone just added to the group may need to
sign out and back in (or wait for a new Kerberos ticket) before they get in.

---

## Step 10 — Smoke test

First run the read-only pre-flight in an elevated Windows PowerShell on the server. It checks
steps 1–9 and prints PASS/WARN/FAIL for each:

```powershell
.\deploy\Test-CsvMaskerServer.ps1 -AllowHttp   # defaults: 'Default Web Site', /csvmasker, pool CsvMasker, C:\Docs\csv_masker\app
```

It checks:
- Windows features and the Hosting Bundle;
- folders and app-pool permissions (it also suggests tightening the temp folder);
- app pool settings;
- the IIS application path and pool;
- Windows on / anonymous off;
- the HTTPS binding (a WARN with `-AllowHttp`, which this deployment uses);
- deployed files;
- the settings file.

It changes nothing; fix any FAIL, then continue:

1. Browse to `http://<server>/csvmasker` from a domain-joined machine. You should get in
   silently, or get a credential prompt in non-Edge/Chrome browsers.
2. Test with an account **outside** the AD group. You should get a 403.
3. Upload a small non-sensitive CSV, run it through to download, and confirm
   `C:\Docs\csv_masker\temp` is empty afterward.
4. Upload a file just under the size limit, then one just over it, to confirm the limit
   behaves as expected.

---

## Troubleshooting

| Symptom | Likely cause |
|---|---|
| HTTP 500.31 / "Failed to load ASP.NET Core runtime" | Hosting Bundle missing or wrong .NET version, or IIS not restarted after install |
| HTTP 500.30 / "In-process start failure" | App threw at startup: check Event Viewer → Windows Logs → Application (source: IIS AspNetCore Module V2) |
| HTTP 500.19 | `web.config` error, or the server-locked auth sections were put in `web.config` instead of being set in Step 7 |
| 401 loop / repeated prompts | Windows auth not enabled, anonymous still on, or the browser isn't treating the server as Intranet zone |
| Links 404 under `/csvmasker` | Hard-coded root-relative URLs in the app. Use `~/` and tag helpers |
| 404.13 on upload | IIS `maxAllowedContentLength` too low |

For startup failures, you can temporarily enable `stdoutLogEnabled="true"` in `web.config`
(and create the `logs` folder it points to). **Turn it back off afterward**: stdout capture
could include data from exceptions. Check that folder for anything sensitive before
deleting it.
