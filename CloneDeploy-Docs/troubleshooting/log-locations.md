# Log Locations

Use the log from the component performing the failed operation. Record the time, device name and task or policy involved so you can match related entries across the server and endpoint.

These are the default production locations. Custom logging settings and Linux data-directory changes can move them. Open protected logs with administrator or root access.

## Main App

| Platform | Location |
| --- | --- |
| Windows | `%ProgramData%\CloneDeploy\Server\logs\app-YYYYMMDD.log` |
| Linux | `/var/lib/clonedeploy/server/logs/app-YYYYMMDD.log` |

The Main App writes daily log files. Check these for application errors, database failures, agent requests and background processing. The default retention is 30 files.

Fatal startup errors are also written to `startup-errors.log` in the data directory's `logs` folder when that location is available. If it cannot be used, the application attempts to write under `CloneDeploy-Server/logs` in the service account's temporary directory.

On Linux with the systemd service from the installation guide, inspect recent service output with:

```bash
sudo journalctl -u clonedeploy-server -n 100 --no-pager
```

For a custom Linux data location, use the `LinuxDataRoot` value in `/etc/clonedeploy/server.json` in place of `/var/lib/clonedeploy/server`.

## Endpoint agents

| Platform | Location |
| --- | --- |
| Windows | `%ProgramData%\CloneDeploy\AgentLogs\agent.log` |
| macOS | `/Library/Logs/CloneDeploy/agent.log` |

Use the agent log for enrollment, connectivity, inventory, action execution and update errors. On Windows, older entries roll into `agent.1.log` through `agent.4.log` in the same directory. `AgentLogs` is a separate directory beside the agent's protected state directory.

The macOS launch daemon sends both standard output and error output to `agent.log`. To view recent entries:

```bash
sudo tail -n 100 /Library/Logs/CloneDeploy/agent.log
```

Windows agent update installation details are stored separately:

- `%ProgramData%\CloneDeploy\Agent\Updates\install.log` — MSI installation log for the automatic update.
- `%ProgramData%\CloneDeploy\Agent\Updates\update-result.json` — recorded update result.

## Site Service

**Windows**

Check **Event Viewer → Windows Logs → Application** for `CloneDeploy-SiteService` service warnings and errors. If Windows cannot start the service, also check **Windows Logs → System** for Service Control Manager events.

The standard Site Service does not configure a rolling application log file. `%ProgramData%\CloneDeploy\SiteService` holds its configuration and state, rather than a default `siteservice.log`.

**Linux**

Check the output captured by the service manager. For a systemd deployment, replace `YOUR_SITE_SERVICE_UNIT` with the unit name used during installation:

```bash
sudo journalctl -u YOUR_SITE_SERVICE_UNIT -n 100 --no-pager
```

The default data directory is `/var/lib/clonedeploy/siteservice`; it is not a default file-log destination. A custom service definition may redirect output elsewhere.

## Windows Worker and WinPE Builder

For service startup or background errors, check **Event Viewer → Windows Logs → Application** on the Windows host. The service names are **CloneDeploy-WindowsWorker** and **CloneDeploy WinPE Builder**. Service logging normally records warnings and errors; it is separate from individual build logs.

For WinPE preparation, open **Administration → Imaging Settings → WinPE Builder → History** and use **Download logs** on the affected prerequisite installation.

Individual WinPE builds and prerequisite installations also produce files under these locations:

| Host | Build queue location |
| --- | --- |
| Windows Main App | `%ProgramData%\CloneDeploy\Server\Staging\WinPe` |
| Linux Main App | `/var/lib/clonedeploy/server/Staging/WinPe` |
| Windows Worker | `%ProgramData%\CloneDeploy-WindowsWorker\WindowsBuilds` |

Build logs are stored in `JOB_ID/build.log`. Prerequisite setup logs are in `setup-JOB_ID/setup.log`. Windows setup can also produce `adk-install.log` and `winpe-install.log` in the setup job directory. Replace `JOB_ID` with the job identifier; files are created as the corresponding work runs. A Linux Main App receives returned logs from its worker.

## Imager in Windows PE

Imager writes `imager-*.log` files under `%TEMP%\CloneDeploy-Imager\logs` in the running WinPE environment. Related DISM diagnostic files may be written alongside the Imager log. The `LogDirectory` setting in `imager.json` overrides the default location.

Copy the logs before restarting WinPE, because files on its RAM disk do not survive a reboot.

## Windows installer failures

The server-side installers write setup-failure summaries to:

| Installer | Location |
| --- | --- |
| Main App | `%ProgramData%\CloneDeploy\ServerSetup\setup.log` |
| Site Service | `%ProgramData%\CloneDeploy\SiteServiceSetup\setup.log` |
| Windows Worker | `%ProgramData%\CloneDeploy\WindowsWorkerSetup\setup.log` |

These files contain setup-script failures, rather than a complete MSI trace. To capture a verbose agent MSI log, run from an elevated command prompt and choose a writable output location:

```cmd
msiexec.exe /i "CloneDeploy-Agent-x64.msi" /L*v "C:\Windows\Temp\CloneDeploy-Agent-install.log"
```

Keep `enrollment.json` beside the MSI or supply `ENROLLMENT_FILE` when installing a new agent.

## Choosing the next check

For a device that is not responding, distinguish its last recorded check-in from a live agent connection. Check the agent service, server URL reachability, certificate trust and pending enrollment approval before retrying commands.

For a policy that does not run, review activation, targeting, frequency, execution windows, conditions, user context and previous results. Use the earliest relevant agent error rather than treating a later skipped action as the original cause.

For imaging failures, identify whether the task failed during network boot, content transfer, Windows configuration or agent handoff. Check storage readiness and WinPE drivers for boot or transfer failures, then correlate the imaging task with the Imager log.

For Windows installation failures, `pg_ctl` exit code `0xC0000135` indicates a missing DLL; install or repair the required x64 Visual C++ runtime before rerunning setup. A missing-framework startup error requires the x64 .NET and ASP.NET Core 10 runtimes. Missing runtimes can prevent application logging entirely; use the installer or service-manager output in that case.
