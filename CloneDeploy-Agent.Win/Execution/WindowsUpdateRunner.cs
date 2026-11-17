using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Agent;

/// <summary>Installs updates through WUA using the device's configured source and reports reboot requirements.</summary>
public static class WindowsUpdateRunner
{
    public static async Task<PolicyProcessResult> RunAsync(
        IPolicyProcess process,
        JsonElement settings,
        CancellationToken ct,
        IReadOnlyList<WindowsUpdateIdentity>? allowedUpdates)
    {
        ct.ThrowIfCancellationRequested();
        var mode = settings.Number("Mode", -1);

        if (mode is not ((int)WindowsUpdateMode.InstallAll or (int)WindowsUpdateMode.InstallApproved))
            throw new NotSupportedException("Unknown Windows Update mode.");

        if (mode == (int)WindowsUpdateMode.InstallApproved && allowedUpdates is null)
            throw new NotSupportedException("Windows Update requires an authorized approval list.");

        if (allowedUpdates?.Any(x => x.UpdateId == Guid.Empty || x.RevisionNumber < 0) == true)
            throw new InvalidDataException("Invalid update approval list.");

        if (mode == (int)WindowsUpdateMode.InstallApproved && allowedUpdates!.Count == 0)
            return new(0, "No approved updates selected; nothing was installed.", "");

        if (settings.Number("Timeout") <= 0)
            throw new InvalidDataException("Windows Update requires a positive timeout.");

        const string script = """
            $ErrorActionPreference='Stop'
            try {
                $approvedOnly=$env:CLONEDEPLOY_APPROVED_UPDATES -ne $null
                $allowed=@{}
                if($approvedOnly){
                    $entries=Get-Content -LiteralPath $env:CLONEDEPLOY_APPROVED_UPDATES -Raw | ConvertFrom-Json
                    foreach($entry in $entries){$allowed[$entry.UpdateId.ToLowerInvariant()+':'+[string]$entry.RevisionNumber]=$true}
                }
                $session=New-Object -ComObject Microsoft.Update.Session
                $session.ClientApplicationID='CloneDeploy'
                $searcher=$session.CreateUpdateSearcher()
                $searcher.ServerSelection=0
                Write-Output 'Searching the configured Windows Update source...'
                $found=$searcher.Search("IsInstalled=0 and IsHidden=0")
                if($found.ResultCode -ne 2){throw 'Windows Update search failed'}
                $updates=New-Object -ComObject Microsoft.Update.UpdateColl
                foreach($update in $found.Updates){
                    $identity=$update.Identity.UpdateID.ToLowerInvariant()+':'+[string]$update.Identity.RevisionNumber
                    if($approvedOnly -and !$allowed.ContainsKey($identity)){continue}
                    if(!$update.EulaAccepted){$update.AcceptEula()}
                    [void]$updates.Add($update)
                }
                if($updates.Count -eq 0){Write-Output 'No applicable updates.';exit 0}
                Write-Output "Downloading $($updates.Count) updates..."
                $downloader=$session.CreateUpdateDownloader();$downloader.Updates=$updates
                if($downloader.Download().ResultCode -ne 2){throw 'Windows Update download failed'}
                $installer=$session.CreateUpdateInstaller();$installer.Updates=$updates
                $installer.AllowSourcePrompts=$false
                if($installer.RebootRequiredBeforeInstallation){Write-Output 'Restart required before installing updates.';exit 3010}
                Write-Output 'Installing updates...'
                $result=$installer.Install()
                for($i=0;$i -lt $updates.Count;$i++){
                    $item=$result.GetUpdateResult($i)
                    Write-Output "$($updates.Item($i).Title): result=$($item.ResultCode), HRESULT=$($item.HResult)"
                }
                if($result.RebootRequired){Write-Output 'Restart required to complete updates.'}
                if($result.ResultCode -ne 2){throw 'One or more updates failed to install'}
                if($result.RebootRequired){exit 3010}
                exit 0
            } catch { Write-Error $_; exit 1 }
            """;
        string? approvalFile = null;

        try
        {
            var start = new ProcessStartInfo(
                PolicyActionRunner.PowerShell,
                $"-NoProfile -NonInteractive -EncodedCommand {Convert.ToBase64String(Encoding.Unicode.GetBytes(script))}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.Environment.Remove("CLONEDEPLOY_APPROVED_UPDATES");

            if (mode == (int)WindowsUpdateMode.InstallApproved)
            {
                approvalFile = Path.Combine(
                    Path.GetTempPath(),
                    $"CloneDeploy-Updates-{Guid.NewGuid().ToString("N")}.json");
                await File.WriteAllTextAsync(approvalFile, JsonSerializer.Serialize(allowedUpdates), ct);
                start.Environment["CLONEDEPLOY_APPROVED_UPDATES"] = approvalFile;
            }

            return await process.RunAsync(start, settings.Number("Timeout"), ct);
        }
        finally
        {
            if (approvalFile is not null) File.Delete(approvalFile);
        }
    }
}