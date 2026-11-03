# Offline install (Windows Server 2022)

The offline bundle installs the LIMS middleware on a server with no internet access (design 10.2 and 10.3).
It is built by CI (`windows-msi` job, artifact `offline-bundle`) and carried to the server on removable media.

## What is in the bundle

| Path | Contents |
|---|---|
| `Install-Offline.ps1` | Runs the whole install in order (below) |
| `msi/LimsMiddleware-<version>-x64.msi` | The service. Self-contained .NET 10, so no .NET runtime is installed |
| `prereqs/postgresql-17.*-windows-x64.exe` | EDB PostgreSQL 17 installer, with its Visual C++ runtime. Hash pinned in `deploy/bundle/prereqs.json`, Authenticode signature checked when the bundle is built |
| `tools/lims-capture/` | Site visit capture tool (self-contained); see `CAPTURE-README.md` |
| `scripts/windows/` | `configure.ps1`, `verify.ps1`, `backup.ps1`, `restore-test.ps1`, `install.ps1` |
| `scripts/sql/` | `roles.sql`, `grants.sql` |
| `docs/` | Backup runbook, implementation notes |
| `bundle.json` | Version, commit, build time, PostgreSQL version and hash, whether the MSI is signed |
| `SHA256SUMS.txt` | SHA-256 of every file in the bundle |

The MSI is **not code-signed** yet (`bundle.json` shows `"msi": false`). Signing needs the hospital's or vendor's
code-signing certificate.

## Before you start

* Compare the hash of the zip with `LimsMiddleware-<version>-offline.zip.sha256`, published next to it by CI,
  before you copy it to the server: `Get-FileHash .\LimsMiddleware-<version>-offline.zip`.
* The hospital HTTPS certificate is imported into `LocalMachine\My` with its private key. Note its thumbprint.
* Agree with hospital IT on the PostgreSQL superuser password. The script uses it and does not store it.
* Have ready: the HMIS base URL and token URL, the HMIS client secret and webhook secret, the staff VLAN subnet
  (HTTPS) and the analyzer VLAN subnet with the ports of analyzers that connect in (for example BC-5000 on 5100).
* Drives: `D:` for data (`D:\LimsMiddleware`, `D:\PostgreSQL\17\data`), `E:` for backups. Override with
  `-DataDir`, `-PgDataDir` and `-BackupDir`.

## Install

Unzip to a local folder (not a network share), open Windows PowerShell **as Administrator** in it, and run:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\Install-Offline.ps1 -CertThumbprint <thumbprint> `
    -HmisBaseUrl https://<hmis>/api/lab/v1/ -HmisTokenUrl https://<hmis>/oauth/token `
    -HmisClientSecret (Read-Host -AsSecureString "HMIS client secret") `
    -HmisWebhookSecret (Read-Host -AsSecureString "HMIS webhook secret") `
    -HttpsAllowFrom <staff subnet> -AnalyzerPorts 5100 -AnalyzerAllowFrom <analyzer subnet> `
    -BackupDir E:\LimsBackup -VerifyUrl https://<name on the certificate>
```

It asks for the PostgreSQL superuser password and a password for the bootstrap web administrator (12 characters
or more), then:

1. Checks every file against `SHA256SUMS.txt` and stops if anything is missing or changed.
2. Installs PostgreSQL 17 as service `postgresql-x64-17`, listening on localhost only. Skipped if that service exists.
   To use a PostgreSQL server elsewhere, pass `-PgHost` and `-PgPort` (the client tools are still installed).
3. Installs the MSI. The service `LimsMiddleware` runs as `NT SERVICE\LimsMiddleware`.
4. Runs `scripts\windows\configure.ps1`:
   * creates the roles `lims_owner`, `lims_app` and `lims_readonly` with generated passwords, and the `lims` database;
   * applies migrations as `lims_owner`, then `grants.sql`;
   * writes `appsettings.Local.json` in the install folder with every secret as a `dpapi-machine:` value;
   * keeps the owner and read-only connections (DPAPI protected) in `D:\LimsMiddleware\admin\db-connections.json`,
     readable by Administrators only, for upgrades;
   * gives the service account read access to the certificate's private key;
   * adds inbound firewall rules only for the subnets given. Nothing is opened to all addresses.
5. Starts the service and runs `verify.ps1` (live and ready checks, free space).

A transcript is saved as `install-<time>.log` in the bundle folder. It contains no passwords.

Then sign in as the bootstrap administrator, create named accounts, deactivate the bootstrap account, and schedule
`scripts\windows\backup.ps1` (see `docs\BACKUP-RUNBOOK.md`).

## Running configure.ps1 again

`configure.ps1` can be run again, for example to add the HMIS secrets later. It generates new database passwords
each time, so stop the service first and start it afterwards:

```powershell
Stop-Service LimsMiddleware
.\scripts\windows\configure.ps1 -PgBin "C:\Program Files\PostgreSQL\17\bin" -DataDir D:\LimsMiddleware `
    -SqlDir .\scripts\sql -CertThumbprint <thumbprint> -HmisClientSecret (Read-Host -AsSecureString) ...
Start-Service LimsMiddleware
```

## Upgrading

Stop the service, install the new MSI, apply migrations with the stored owner connection and the grants, then start:

```powershell
Stop-Service LimsMiddleware
msiexec /i msi\LimsMiddleware-<new version>-x64.msi /qn DATADIR=D:\LimsMiddleware CERTTHUMBPRINT=<thumbprint>
$owner = (Get-Content -Raw D:\LimsMiddleware\admin\db-connections.json | ConvertFrom-Json).owner
& "C:\Program Files\LimsMiddleware\Lims.Host.exe" migrate --connection $owner
```

Run `grants.sql` as `lims_owner` (or run `configure.ps1` again, which does both), then `Start-Service LimsMiddleware`.
The service refuses to start while migrations are pending, with a message naming them.

## Building the bundle

CI builds it on every push. To build it by hand on a Windows machine with internet access:

```powershell
dotnet publish src/Lims.Host -c Release -r win-x64 --self-contained true -o artifacts/publish/win-x64
dotnet publish tools/Lims.Capture -c Release -r win-x64 --self-contained true -o artifacts/publish/lims-capture
dotnet build installer/Lims.Installer.wixproj -c Release -p:PublishDir=$PWD\artifacts\publish\win-x64\
deploy/bundle/build-bundle.ps1 -Msi <path to the .msi> -CaptureDir artifacts/publish/lims-capture -Version <version>
```

To move to a newer PostgreSQL 17 release, change `version` and `url` in `deploy/bundle/prereqs.json`, clear `sha256`,
build once with `-AllowUnpinned`, check the printed hash and signature, and pin the hash.
