# Prepare an Image for Capture

Build and customize a Windows reference computer before capturing it for deployment. Complete Prepare for Imaging first so a ready WinPE environment and an enabled image preparation profile are available.

## 1. Build the reference computer in Hyper-V

A Hyper-V virtual machine is a useful starting point for a reference image. It lets you maintain a working copy and take a checkpoint before final preparation, so you can return to that state to make changes or retry a failed preparation. Physical reference computers can also be used.

Install the Windows version and applications required by your organization. Keep a checkpoint of the finished reference installation **before running image preparation**, rather than relying on the captured image as your editable source.

## 2. Customize Windows in Audit Mode

At the initial Windows out-of-box experience (OOBE), press **Ctrl+Shift+F3** to restart into Audit Mode. Use this environment to install applications, apply Windows updates and configure the reference installation before completing end-user setup. Microsoft describes this workflow in [Boot Windows to Audit Mode or OOBE](https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/boot-windows-to-audit-mode-or-oobe).

Finish application installation and required restarts before final preparation. Settings made only in the current user's profile do not automatically become defaults for every deployed user; apply shared settings through the appropriate Windows or application configuration.

Install and enroll the CloneDeploy agent on the reference computer using the deployment bundle prepared for your server. Confirm that it can connect to CloneDeploy. If needed, complete Prepare for Device Management and Install the Agent before continuing.

The reference computer is ready for capture. Continue with Your First Capture and Deployment to run image prep from Agent Administrator tools, capture the image and test its deployment.
