# CSV Masker — IIS Setup & Deployment (Windows Server 2022)

Assumes IIS is already running on the server with an existing HTTPS site (examples use
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

Keep the app, temp data, and recipes in separate folders, with temp and recipes outside the
web root. Use a data drive if the server has one.

```powershell
New-Item -ItemType Directory -Force -Path "C:\inetpub\CsvMasker"      # app binaries
New-Item -ItemType Directory -Force -Path "D:\CsvMaskerData\temp"     # uploads/outputs (transient)
New-Item -ItemType Directory -Force -Path "D:\CsvMaskerData\recipes"  # saved configs (no data)
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

```powershell
$id = "IIS AppPool\CsvMasker"
icacls "C:\inetpub\CsvMasker"       /grant "${id}:(OI)(CI)RX"
icacls "D:\CsvMaskerData\temp"      /grant "${id}:(OI)(CI)M"
icacls "D:\CsvMaskerData\recipes"   /grant "${id}:(OI)(CI)M"
```

Optionally tighten `D:\CsvMaskerData\temp` so only the app pool identity and
Administrators have access (remove inherited `Users` read). Raw sensitive uploads sit there
briefly.

---

## Step 6 — Create the IIS application

```powershell
New-WebApplication -Site "Default Web Site" -Name "csvmasker" `
    -PhysicalPath "C:\inetpub\CsvMasker" -ApplicationPool "CsvMasker"
```

The app will live at `https://<server>/csvmasker`. ASP.NET Core picks up the `/csvmasker`
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

**HTTPS:** confirm the parent site has an HTTPS binding with a valid certificate. The
application inherits it. If the site also has an HTTP binding, the app should redirect to
HTTPS (or remove the HTTP binding if nothing else needs it).

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

1. Copy a file named `app_offline.htm` into `C:\inetpub\CsvMasker`. The app shuts down and
   IIS serves that page.
2. Copy the contents of `.\publish` into `C:\inetpub\CsvMasker`, overwriting.
3. Delete `app_offline.htm`. The app starts on the next request.

Make sure `appsettings.json` on the server has the production values (AD group, folder paths,
limits). Consider keeping server-specific settings in `appsettings.Production.json` so a
publish doesn't overwrite them.

---

## Step 10 — Smoke test

1. Browse to `https://<server>/csvmasker` from a domain-joined machine. You should get in
   silently, or get a credential prompt in non-Edge/Chrome browsers.
2. Test with an account **outside** the AD group. You should get a 403.
3. Upload a small non-sensitive CSV, run it through to download, and confirm
   `D:\CsvMaskerData\temp` is empty afterward.
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
