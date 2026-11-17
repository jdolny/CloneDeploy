using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using System.Runtime.Serialization.Json;
using Microsoft.Win32;
using WixToolset.Dtf.WindowsInstaller;

namespace CloneDeploy.Setup
{
    public static class ServerActions
    {
        private static readonly Dictionary<string, string> Properties = new Dictionary<string, string>
        {
            { "InstallPath", "APPDIR" }, { "ServiceUser", "SERVICE_USER" },
            { "ServicePassword", "SERVICE_PASSWORD" },
            { "DatabaseMode", "DB_MODE" },
            { "DatabaseHost", "DB_HOST" }, { "DatabasePort", "DB_PORT" },
            { "DatabaseName", "DB_NAME" }, { "DatabaseUser", "DB_USER" },
            { "DatabasePassword", "DB_PASSWORD" },
            { "DatabaseCa", "DB_CA" },
            { "HttpPort", "HTTP_PORT" }, { "InstallRole", "INSTALL_ROLE" }, { "EnrollmentFile", "SITE_ENROLLMENT_FILE" }
        };

        [CustomAction]
        public static ActionResult ResolveRole(Session session)
        {
            var role = session["PackagedRole"];
            if (role != "App" && role != "SiteService" && role != "WindowsWorker") return Fail(session, "The installer role is missing.");
            if (role != "App" && session["REMOVE"] != "ALL")
            {
                var otherRole = role == "SiteService" ? "WindowsWorker" : "SiteService";
                using (var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var otherInstall = machine.OpenSubKey(@"SOFTWARE\CloneDeploy\Installation\" + otherRole))
                using (var otherService = machine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\CloneDeploy-" + otherRole))
                    if (otherInstall != null || otherService != null)
                        return Fail(session, "Site Service and Windows Worker cannot share a host. Uninstall the other component first.");
            }
            session["INSTALL_ROLE"] = role;
            // Recompute ownership from the registry in both UI and execute sequences.
            session["INSTALLED_ROLE"] = "";
            using (var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var installed = root.OpenSubKey(@"SOFTWARE\CloneDeploy\Installation\" + role))
            {
                var marker = installed;
                var path = marker == null ? null : marker.GetValue("Path") as string;
                if (!string.IsNullOrEmpty(path))
                {
                    session["INSTALLED_ROLE"] = role;
                    session["APPDIR"] = path;
                }
            }
            if (!string.IsNullOrEmpty(session["OLDPRODUCTS"]) && string.IsNullOrEmpty(session["INSTALLED_ROLE"]))
                return Fail(session, "An older installation was detected, but its installation folder could not be recovered. Repair the existing installation before upgrading.");
            return ActionResult.Success;
        }

        [CustomAction]
        public static ActionResult ValidateVisualCpp(Session session)
        {
            if (!string.Equals(session["REMOVE"], "ALL", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    // This custom-action assembly is x64, so SystemDirectory is the native system folder.
                    var runtime = Path.Combine(Environment.SystemDirectory, "msvcp140.dll");
                    var version = File.Exists(runtime) ? FileVersionInfo.GetVersionInfo(runtime) : null;
                    if (version == null || !HasRequiredVisualCpp(new Version(version.FileMajorPart, version.FileMinorPart, version.FileBuildPart, version.FilePrivatePart)))
                        return Fail(session, "Microsoft Visual C++ x64 Runtime 14.44.35211.0 or later is required. Install the x64 Visual C++ Redistributable before running this MSI.");
                }
                catch (Exception ex)
                {
                    // Keep this source compatible with Windows PowerShell 5.1's
                    // Add-Type compiler, used by the credential-handoff checks.
                    if (!(ex is IOException || ex is UnauthorizedAccessException || ex is System.ComponentModel.Win32Exception)) throw;
                    return Fail(session, "Could not verify the Microsoft Visual C++ x64 Runtime. Install or repair the x64 Visual C++ Redistributable, then retry.");
                }
            }
            return ActionResult.Success;
        }

        internal static bool HasRequiredVisualCpp(Version version)
        {
            return version != null && version >= new Version(14, 44, 35211, 0);
        }

        internal const string RequiredDotNetVersion = "10.0.13";

        [CustomAction]
        public static ActionResult ValidateDotNet(Session session)
        {
            if (string.Equals(session["REMOVE"], "ALL", StringComparison.OrdinalIgnoreCase)) return ActionResult.Success;
            try
            {
                // Use the machine-wide x64 installation, not the invoking user's PATH
                // or a per-user SDK that the Windows service cannot discover.
                var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet");
                foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
                {
                    using (var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                    using (var installed = machine.OpenSubKey(@"SOFTWARE\dotnet\Setup\InstalledVersions\x64"))
                    {
                        var location = installed == null ? null : installed.GetValue("InstallLocation") as string;
                        if (!string.IsNullOrWhiteSpace(location)) { root = location; break; }
                    }
                }
                if (!HasRequiredDotNet(root))
                    return Fail(session, "Install the x64 .NET Runtime and ASP.NET Core Runtime 10.0.13 or a newer 10.0 patch before running this MSI. The .NET Hosting Bundle or SDK can supply both. x86, preview, and other major versions do not satisfy this requirement.");
                return ActionResult.Success;
            }
            catch (Exception ex)
            {
                if (!(ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException || ex is ArgumentException)) throw;
                return Fail(session, "Could not verify the machine-wide x64 .NET 10 runtimes. Install or repair .NET Runtime and ASP.NET Core Runtime 10.0.13 or a newer 10.0 patch, then retry.");
            }
        }

        internal static bool HasRequiredDotNet(string root)
        {
            if (!File.Exists(Path.Combine(root, "dotnet.exe"))) return false;
            foreach (var framework in new[] { "Microsoft.NETCore.App", "Microsoft.AspNetCore.App" })
            {
                var folder = Path.Combine(root, "shared", framework);
                var found = false;
                if (Directory.Exists(folder))
                    foreach (var versionFolder in Directory.EnumerateDirectories(folder))
                    {
                        Version version;
                        if (!Version.TryParse(Path.GetFileName(versionFolder), out version) || version.Major != 10 || version.Minor != 0 ||
                            version < new Version(RequiredDotNetVersion)) continue;
                        var requiredFile = framework == "Microsoft.NETCore.App" ? "coreclr.dll" : "Microsoft.AspNetCore.dll";
                        if (File.Exists(Path.Combine(versionFolder, requiredFile))) { found = true; break; }
                    }
                if (!found) return false;
            }
            return true;
        }

        [CustomAction]
        public static ActionResult BrowseEnrollment(Session session)
        {
            session["SITE_ENROLLMENT_SELECTION"] = "";
            string selected = null;
            Exception failure = null;
            var thread = new Thread(() =>
            {
                try
                {
                    using (var dialog = new OpenFileDialog { Filter = "SiteService setup (*.json)|*.json", CheckFileExists = true, Multiselect = false, Title = "Select SiteService setup file" })
                        if (dialog.ShowDialog() == DialogResult.OK) selected = dialog.FileName;
                }
                catch (Exception ex) { failure = ex; }
            });
            thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
            if (failure != null) return Fail(session, "Could not open the file picker. Enter the setup file's full path instead.");
            // A following SetProperty control event updates the visible edit control.
            if (selected != null) session["SITE_ENROLLMENT_SELECTION"] = selected;
            return ActionResult.Success;
        }

        [CustomAction]
        public static ActionResult PrepareServer(Session session)
        {
            var role = session["PackagedRole"];
            session["INSTALL_ROLE"] = role;
            if (role != "App" && role != "SiteService" && role != "WindowsWorker") return Fail(session, "Choose Main App, Site Service or Windows Worker.");
            if (!string.IsNullOrEmpty(session["INSTALLED_ROLE"]) && role != session["INSTALLED_ROLE"])
                return Fail(session, "Repair and upgrades must keep the installed role. Uninstall before changing roles.");
            using (var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var other = root.OpenSubKey(@"SOFTWARE\CloneDeploy\" + (role == "App" ? "SiteService" : "Server")))
            {
                var otherPath = other == null ? null : other.GetValue("Path") as string;
                if (!string.IsNullOrEmpty(otherPath) && session["REMOVE"] != "ALL")
                {
                    var target = Path.GetFullPath(session["APPDIR"]).TrimEnd('\\') + "\\";
                    var existing = Path.GetFullPath(otherPath).TrimEnd('\\') + "\\";
                    if (target.StartsWith(existing, StringComparison.OrdinalIgnoreCase) || existing.StartsWith(target, StringComparison.OrdinalIgnoreCase))
                        return Fail(session, "App and Site Service must use separate, non-overlapping installation folders.");
                }
            }
            // Immediate actions may impersonate an unelevated caller. Use the readable
            // MSI ownership marker here; the elevated action validates private state.
            string registeredPath;
            using (var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var key = root.OpenSubKey(@"SOFTWARE\CloneDeploy\Server"))
                registeredPath = key == null ? null : key.GetValue("Path") as string;
            var existingInstallation = !string.IsNullOrEmpty(registeredPath) &&
                string.Equals(registeredPath.TrimEnd('\\'), session["APPDIR"].TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
            // A major upgrade may already have removed the old MSI registry row.
            // ResolveRole captured it before costing/removal; the elevated script checks ownership again.
            existingInstallation = existingInstallation || session["INSTALLED_ROLE"] == "App";
            if (role == "App" && session["REMOVE"] != "ALL" && !existingInstallation)
            {
                if (session["SERVICE_PASSWORD_OVERRIDE"] == "1" &&
                    (string.IsNullOrWhiteSpace(session["SERVICE_PASSWORD"]) ||
                     session["SERVICE_PASSWORD"] != session["SERVICE_PASSWORD_CONFIRM"]))
                    return Fail(session, "Enter a service password and matching confirmation, or turn off the password override.");
                using (var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                {
                    foreach (var name in new[] { "CloneDeploy-Server", "CloneDeploy-PostgreSQL", "CloneDeploy WinPE Builder" })
                    using (var service = root.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name))
                        if (service != null) return Fail(session, "A service named " + name + " already exists outside this installation. Setup will not modify it.");
                }
                if (session["DB_MODE"] == "Existing" && string.IsNullOrWhiteSpace(session["DB_PASSWORD"]))
                    return Fail(session, "Enter the external database password.");
            }
            // CustomActionData escapes delimiters. Hidden properties + HideTarget keep
            // credentials out of MSI logs; no credential-bearing Registry table rows exist.
            var data = new CustomActionData();
            foreach (var property in Properties) data.Add(property.Key, session[property.Value]);
            data["ServiceUser"] = "CloneDeploySvc";
            if (session["SERVICE_PASSWORD_OVERRIDE"] != "1" || existingInstallation || role != "App")
                data["ServicePassword"] = "";
            session["ConfigureServer"] = data.ToString();
            var path = new CustomActionData();
            path.Add("InstallPath", session["APPDIR"]);
            path.Add("InstallRole", role);
            foreach (var action in new[] { "RollbackServer", "CommitServer", "UninstallServer" })
                session[action] = path.ToString();
            return ActionResult.Success;
        }

        private static ActionResult Fail(Session session, string message)
        {
            using (var record = new Record(1))
            {
                record[0] = message;
                session.Message(InstallMessage.Error, record);
            }
            return ActionResult.Failure;
        }

        [CustomAction] public static ActionResult ConfigureServer(Session session) { return Run(session, "Install"); }
        [CustomAction] public static ActionResult RollbackServer(Session session) { return Run(session, "Rollback"); }
        [CustomAction] public static ActionResult CommitServer(Session session) { return Run(session, "Commit"); }
        [CustomAction] public static ActionResult UninstallServer(Session session) { return Run(session, "Uninstall"); }

        private static ActionResult Run(Session session, string mode)
        {
            try
            {
                var data = session.CustomActionData;
                var site = data.ContainsKey("InstallRole") && (data["InstallRole"] == "SiteService" || data["InstallRole"] == "WindowsWorker");
                var script = Path.Combine(data["InstallPath"], "Setup", site ? "Configure-SiteService.ps1" : "Configure-Server.ps1");
                // Catch failures before the site script's own handler (including parse/import errors).
                // Only numeric error metadata may cross into MSI logs; never print the error record.
                var bootstrap = "$ErrorActionPreference='Stop'; try { & '" + script.Replace("'", "''") + "' -Mode " + mode +
                    " } catch { [Console]::Error.WriteLine(('CDSETUP_DIAGNOSTIC phase=bootstrap line={0} category={1} hresult={2}' -f $_.InvocationInfo.ScriptLineNumber,[int]$_.CategoryInfo.Category,$_.Exception.HResult)); exit 1 }";
                var start = new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe"),
                    Arguments = site
                        ? "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(bootstrap))
                        : "-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + script + "\" -Mode " + mode,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (var process = Process.Start(start))
                {
                    var stdout = process.StandardOutput.ReadToEndAsync();
                    var stderr = process.StandardError.ReadToEndAsync();
                    if (mode == "Install" || site)
                    {
                        var request = new Dictionary<string, string>();
                        if (mode == "Install") foreach (var property in Properties) request.Add(property.Key, data[property.Key]);
                        else { request.Add("InstallPath", data["InstallPath"]); request.Add("InstallRole", data["InstallRole"]); }
                        var serializer = new DataContractJsonSerializer(typeof(Dictionary<string, string>),
                            new DataContractJsonSerializerSettings { UseSimpleDictionaryFormat = true });
                        serializer.WriteObject(process.StandardInput.BaseStream, request);
                    }
                    process.StandardInput.Close();
                    // Initialization and first database migration may be slow. Individual
                    // operations in the script have their own timeouts.
                    process.WaitForExit();
                    if (process.ExitCode != 0)
                    {
                        session.Log("CloneDeploy setup process exit code: " + process.ExitCode + ".");
                        LogDiagnostics(session, stdout.GetAwaiter().GetResult());
                        LogDiagnostics(session, stderr.GetAwaiter().GetResult());
                        session.Log("CloneDeploy setup failed during " + mode + ". See ProgramData\\CloneDeploy\\" + (site ? "SiteServiceSetup" : "ServerSetup") + "\\setup.log.");
                        return ActionResult.Failure;
                    }
                }
                return ActionResult.Success;
            }
            catch (Exception ex)
            {
                session.Log("CloneDeploy server setup could not complete " + mode + ". HRESULT: " + ex.HResult + ". No credentials have been logged.");
                return ActionResult.Failure;
            }
        }

        private static void LogDiagnostics(Session session, string output)
        {
            foreach (Match match in Regex.Matches(output, @"(?m)^CDSETUP_DIAGNOSTIC phase=(bootstrap|script) line=[0-9]{1,6} category=[0-9]{1,3} hresult=-?[0-9]{1,10}\r?$"))
                session.Log(match.Value.TrimEnd('\r'));
        }
    }
}
