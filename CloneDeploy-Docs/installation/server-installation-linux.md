# Linux Installation

> [!IMPORTANT]
> A Windows Worker is required if the main application is installed on Linux and you intend to use OS imaging.  Plan one **Windows 11 x64 virtual machine or physical computer** for WinPE builds and Windows-only driver preparation. A compatible Windows Server host can also be used; a server edition is not required.

The Main App runs on **Linux x64** and includes its own web server. Apache or Nginx is not required. Install the Linux server and complete Initial Configuration. If you will use imaging, continue with Windows Worker setup below.

## 1. Install .NET

Follow Microsoft's [.NET installation instructions for your Linux distribution](https://learn.microsoft.com/en-us/dotnet/core/install/linux) and install the **ASP.NET Core Runtime 10.0** package, typically named `aspnetcore-runtime-10.0`. This also installs the .NET runtime. Use **10.0.13 or later, .NET 11 is not supported**.

Run `dotnet --list-runtimes` and confirm that both `Microsoft.NETCore.App` and `Microsoft.AspNetCore.App` show a compatible 10.0 version.

## 2. Download the server

Download the **CloneDeploy-Server Linux x64.tar.gz archive** from the [latest CloneDeploy release on GitHub](https://github.com/jdolny/CloneDeploy/releases/latest). Extract its contents into `/opt/clonedeploy/server`.

Create a dedicated Linux service account named `clonedeploy` and give it read and execute access to the extracted application files.

## 3. Configure the database and server

Install PostgreSQL locally or use an existing PostgreSQL server. Create a database and login for CloneDeploy, enable the `citext` extension in that database, and give the login permission to apply schema migrations. CloneDeploy initializes its tables when it first starts.

Copy the archive's `server.example.json` to `/etc/clonedeploy/server.json`. Set `ConnectionStrings:CloneDeploy-Server-Database` to your PostgreSQL connection string, including the database name and credentials.

Create `/var/lib/clonedeploy/server` for application data. Give the `clonedeploy` account write access to this directory, `/etc/clonedeploy`, and `server.json` so the application can save settings. Restrict configuration and data access to that account and administrators.

To store application data elsewhere, change `LinuxDataRoot` in `server.json` and create that directory before starting the service. Keep it separate from the application files.

## 4. Run CloneDeploy as a service

On a distribution using systemd, create `/etc/systemd/system/clonedeploy-server.service`:

```ini
[Unit]
Description=CloneDeploy Server
Wants=network-online.target
After=network-online.target

[Service]
User=clonedeploy
WorkingDirectory=/opt/clonedeploy/server
ExecStart=/usr/bin/dotnet /opt/clonedeploy/server/CloneDeploy-Server.dll
Environment=ASPNETCORE_ENVIRONMENT=Production
AmbientCapabilities=CAP_NET_BIND_SERVICE
Restart=on-failure
RestartSec=10

[Install]
WantedBy=multi-user.target
```

Adjust `/usr/bin/dotnet` if `command -v dotnet` reports a different installation path. `CAP_NET_BIND_SERVICE` lets the service account use the default HTTP and HTTPS ports without running as root.

Enable and start the service:

```bash
sudo systemctl daemon-reload
sudo systemctl enable --now clonedeploy-server
sudo systemctl status clonedeploy-server
```

If startup fails, review the logs with `sudo journalctl -u clonedeploy-server -n 100 --no-pager`.

## 5. Open the Web UI

Allow the initial HTTP port through the server firewall; the default is **80**. From the private management network, open the server's HTTP address, for example `http://clonedeploy.example.org/`.

Continue with Initial Configuration to create the first administrator and configure HTTPS. Use Linux paths for local storage and `sudo systemctl restart clonedeploy-server` when a restart is required. Allow the configured HTTPS ports through the firewall; the defaults are **443** for management, **8443** for agents and imaging, and **8444** for the endpoint portal.

After completing Initial Configuration and verifying HTTPS access, continue with Prepare for Device Management before installing agents. The Main App prepares agent deployment bundles locally using the packaged installers; this does not require Windows Worker.

> [!NOTE]
> **Complete Initial Configuration before continuing.** Follow the Initial Configuration steps to set up administrator access, HTTPS and storage. Once those steps are complete and HTTPS access is verified, return to this page and continue below with **Set up Windows Worker for imaging**.

## Set up Windows Worker for imaging

Complete the remaining steps only if you will use imaging. Finish Initial Configuration and verify HTTPS access before creating the worker setup file.

### 6. Prepare the Windows host

Use a Windows 11 x64 VM or physical computer, or a Windows Server x64 host compatible with the required .NET runtime and Windows ADK. Keep this machine available when Windows preparation jobs need to run, and allow disk space for downloaded tools, drivers and temporary WinPE build files.

Install these machine-wide prerequisites:

- [Latest .NET 10 Hosting Bundle](https://aka.ms/dotnetcore-10-0-windowshosting), providing the .NET and ASP.NET Core runtimes **10.0.13 or a newer 10.0 patch**.
- [Latest Microsoft Visual C++ x64 Redistributable](https://aka.ms/vc14/vc_redist.x64.exe), version **14.44.35211.0 or later**.

Allow outbound access from the worker to the Main App's service HTTPS port, **TCP 8443** by default, and to Microsoft's download servers for ADK and WinPE prerequisite installation. Windows Worker initiates its connections to the Main App and does not need an inbound management listener.

Windows Worker and Site Service cannot be installed on the same Windows host. Use separate machines if both roles are needed.

### 7. Create the Windows Worker setup file

In the CloneDeploy Web UI, sign in as an administrator:

1. Open **Administration → Server Settings → General**. Confirm that **Public application URL** is saved and **Main server IP** under **Site Service connections** is reachable from the Windows host.
2. Open **Administration → Site Services** and select **Add SiteService**.
3. Select **Windows Worker (one per Linux main server)**.
4. Enter a **Service name**, such as `Windows Worker`.
5. Select **Create and download setup file** and copy the downloaded JSON to a local folder on the Windows host.

The setup file contains a single-use enrollment token valid for **24 hours**. If it expires before enrollment, open the registration's **Enrollment** tab and select **Download new setup file**.

### 8. Install Windows Worker

1. Download the **CloneDeploy Windows Worker x64 MSI** from the [CloneDeploy GitHub releases](https://github.com/jdolny/CloneDeploy/releases). Use the same release version as the Linux Main App.
2. Run the MSI with administrator privileges and choose the installation directory.
3. When prompted for the setup JSON, select the local file downloaded in the previous step.
4. Complete installation and verify that the **CloneDeploy-WindowsWorker** Windows service is running.

The installer runs the worker as LocalSystem. The worker generates its identity locally, contacts the Main App and waits for approval.

### 9. Approve and enable the worker

1. Return to **Administration → Site Services** and open the worker registration. Wait for enrollment to show **Pending**.
2. On the Windows host, open `%ProgramData%\CloneDeploy\WindowsWorkerSetup\identity-fingerprint.txt`.
3. Open the registration's **Configuration** tab and compare its identity key fingerprint with the file on the Windows host.
4. Confirm that the reported OS is **Windows**, enable **Enable Windows Worker**, and select **Approve and configure**.
5. On **General**, verify that enrollment is approved, worker capabilities are enabled, and **Configuration** becomes **Applied** after the worker contacts the server.

If a previous worker registration exists, revoke it before creating a replacement; only one active registration is allowed.

### 10. Install the WinPE prerequisites

In the Web UI, open **Administration → Imaging Settings → WinPE Builder**:

1. Wait for the Windows builder to appear online.
2. Select **Install prerequisites**.
3. Review the required ADK and WinPE versions and Microsoft's license information, then select **Accept and install**.
4. Follow progress in **History**. The installation runs on the Windows Worker.
5. If a restart is required, restart the Windows host and wait for the worker to reconnect.
6. Confirm that **Status** reports the builder as ready with the required ADK and WinPE versions.

The Linux server and Windows Worker are now prepared for creating WinPE boot environments. Configure imaging storage and boot services for the deployment methods you intend to use.

## Changing the Linux data location

An omitted or blank LinuxDataRoot uses `/var/lib/clonedeploy/server`. The configured data directory must already exist and be mounted before startup. Relative paths, `/`, and paths overlapping the application directory are rejected.

The data directory contains published content, mount links, staging and temporary files, keys, default logs and backups. Configuration remains in `/etc/clonedeploy/server.json` even when the data location changes. Restrict both locations to the service account and administrators; the service needs write access to the configuration file and its containing directory to save settings through the Web UI. Application binaries can remain read-only.

To change the data location, stop the service, move the data that must be retained, update LinuxDataRoot and restore the required permissions before restarting. There is no automatic data migration. Run the production service with `ASPNETCORE_ENVIRONMENT=Production`.
