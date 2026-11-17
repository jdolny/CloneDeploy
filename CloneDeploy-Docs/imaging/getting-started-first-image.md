# Your first capture and deployment

Complete Initial Configuration, Prepare for Imaging and Prepare an Image for Capture before starting. The reference computer should be customized with its CloneDeploy agent installed and enrolled. Use a destination test computer whose existing data can be replaced.

## 1. Run image prep from Agent Administrator tools

Use the image preparation profile created in Prepare for Imaging as the final step before capture. The profile controls agent identity handling, preparation scripts, the answer file and whether Sysprep runs.

If the agent will remain in the reusable image, confirm that the profile uses **Reset for image capture** so deployed computers enroll with fresh identities. Use **Uninstall before image capture** if the agent should be removed instead.

1. Finish customizing the reference computer and save your work. For a VM, take a checkpoint before proceeding.
2. Open the CloneDeploy agent on the reference computer, open **Administrator tools**, and sign in with an account permitted to run image preparation.
3. Select **Run image prep**, then **Refresh profiles** and choose the enabled profile prepared for this image.
4. Review the selected profile's settings before starting. For this workflow, enable **Reboot and capture image** in the profile's **Configuration** tab in the Web UI. Confirm that a compatible WinPE environment is ready and the reference computer has an Ethernet connection to the server.
5. Select **Prepare and capture**. Choose an existing destination image, or create one if your permissions allow it, then confirm preparation.
6. Approve the Windows administrator prompt for the progress viewer and follow the preparation status. Resolve any failure before capturing the computer.

With **Reboot and capture image** enabled in the profile, successful preparation restarts into WinPE and begins capture automatically.

## 2. Review the captured image

Wait for capture to complete, then review the resulting image version and imaging task status before deploying it.

## 3. Deploy to a test computer

1. Configure a deployment profile, including any required drivers.
2. Confirm that the test computer can start CloneDeploy Imager using network boot or ISO/USB media.
3. Deploy the image to the test computer. Deployment can overwrite the destination disk.
4. Check imaging tasks and verify that the destination boots and has working storage and network drivers.
