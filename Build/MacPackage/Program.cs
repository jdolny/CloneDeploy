using CloneDeploy_Server.Backend.Services;

if (args.Length != 3)
{
    Console.Error.WriteLine("Usage: MacPackage <archive.tar.gz> <output.pkg> <version>");
    return 1;
}
var archive = Path.GetFullPath(args[0]);
var destination = Path.GetFullPath(args[1]);
if (!File.Exists(archive) || string.Equals(archive, destination, StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("Provide an existing archive and a different output package path.");
    return 1;
}
Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
try
{
    await MacInstallerPackage.CreateAsync(archive, temporary, args[2], default);
    File.Move(temporary, destination, overwrite: true);
    Console.WriteLine("Created Mac installer: " + destination);
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine("Mac installer creation failed: " + ex.Message);
    return 1;
}
finally { if (File.Exists(temporary)) File.Delete(temporary); }
