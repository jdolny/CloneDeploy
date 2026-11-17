using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace CloneDeploy_Server.Backend.Services;

/// <summary>Reads approved MSI architecture and version without modifying or executing the installer.</summary>
[SupportedOSPlatform("windows")]
public static class AgentMsiEditor
{
    public const string UpgradeCode = "{D75325AF-F72B-4039-9B5D-DBAA9E8AB0F5}";

    public static string ReadArchitecture(string path)
    {
        dynamic installer = Activator.CreateInstance(Type.GetTypeFromProgID("WindowsInstaller.Installer", true)!)!;
        try
        {
            dynamic db = installer.OpenDatabase(path, 0);
            try
            {
                dynamic summary = db.SummaryInformation(0);
                string platform;
                try { platform = ((string)summary.Property[7]).Split(';')[0]; }
                finally { Marshal.FinalReleaseComObject(summary); }
                var architecture = platform switch { "x64" => "x64", "Arm64" => "arm64", _ => throw new InvalidDataException("Unsupported agent MSI platform.") };
                if (Read(db, "SELECT `Value` FROM `Property` WHERE `Property` = 'UpgradeCode'") != CloneDeploy_Agent.Contracts.WindowsArchitecture.UpgradeCode(architecture))
                    throw new InvalidDataException("Agent MSI platform and upgrade identity do not match.");
                return architecture;
            }
            finally { Marshal.FinalReleaseComObject(db); }
        }
        finally { Marshal.FinalReleaseComObject(installer); }
    }

    public static string ReadVersion(string path)
    {
        var architecture = ReadArchitecture(path);
        dynamic installer = Activator.CreateInstance(Type.GetTypeFromProgID("WindowsInstaller.Installer", true)!)!;
        try
        {
            dynamic db = installer.OpenDatabase(path, 0);
            try
            {
                if (Read(db, "SELECT `Value` FROM `Property` WHERE `Property` = 'UpgradeCode'") != CloneDeploy_Agent.Contracts.WindowsArchitecture.UpgradeCode(architecture))
                    throw new InvalidDataException("This MSI is not a CloneDeploy agent package.");
                return CloneDeploy_Agent.Contracts.AgentUpdateOffer.ParseVersion(
                    Read(db, "SELECT `Value` FROM `Property` WHERE `Property` = 'ProductVersion'") ?? "").ToString();
            }
            finally { Marshal.FinalReleaseComObject(db); }
        }
        finally { Marshal.FinalReleaseComObject(installer); }
    }

    private static string? Read(dynamic db, string sql)
    {
        dynamic view = db.OpenView(sql);
        try
        {
            view.Execute();
            dynamic? record = view.Fetch();
            if (record is null) return null;
            try { return (string)record.StringData[1]; }
            finally { Marshal.FinalReleaseComObject(record); }
        }
        finally { view.Close(); Marshal.FinalReleaseComObject(view); }
    }
}
