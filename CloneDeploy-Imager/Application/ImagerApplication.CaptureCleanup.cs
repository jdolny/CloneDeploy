using CloneDeploy_Imager.Discovery;

namespace CloneDeploy_Imager;

internal sealed partial class ImagerApplication
{
    private void ClearCapturedAnswerFiles(WindowsVolume source)
    {
        try { CaptureAnswerFileCleanup.Clear(source, log); }
        catch (Exception ex)
        {
            // Publication already succeeded; do not abort or misreport that image.
            log.Failure("Post-capture source answer-file cleanup", ex);
            ui.Line("WARNING: Image uploaded, but source answer-file cleanup failed. Review the Imager log before booting this computer into Windows.");
        }
    }

    private async Task UploadCompletedCaptureLog(Api.ImagingClient client)
    {
        if (await client.TryUploadCaptureLogAsync())
            ui.Line("Capture diagnostic log uploaded to the device's Imaging logs.");
        else
            ui.Line("WARNING: Image uploaded, but capture diagnostics could not be uploaded. The log remains available locally.");
    }
}
