# Configuration

Choose source-machine preparation steps.

<div class="ui-controls-spacer"></div>

**Disable hibernation**

Turns off Windows hibernation.

**Turn off BitLocker and wait for decryption**

Decrypts fixed volumes before continuing; allow time to finish.

**CloneDeploy agent**

Choose Keep installed without resetting, Reset for image capture, or Uninstall before image capture. Reset clears identity and execution history so deployed computers enroll fresh identities. Uninstall removes the MSI-installed agent and its enrollment state after preparation succeeds.

**Run Sysprep**

Runs Windows generalize/OOBE.

**Reboot and capture image**

Stages WinPE, then restarts into the imager after successful preparation to upload a new image version. Choose an existing image or create a new WIM/FFU image when starting from agent Administrator tools.

**Save Changes**

Stores steps; execute from agent Administrator tools.

Uninstall requires an updated, MSI-installed agent. It runs after preparation and capture staging, using a detached helper, before Sysprep starts. Automatic capture remains available: the helper uninstalls the agent before arming the WinPE restart. A failed or incomplete uninstall leaves Windows running and records the reason in `image-prep-finalization.log` and `image-prep-uninstall.log` under `%ProgramData%\CloneDeploy\AgentLogs`; do not capture until corrected. Open logs with an elevated administrator editor or PowerShell. A pending capture can be cancelled from Imaging Tasks. The helper retains diagnostic logs but removes the agent identity/configuration databases.

Automatic capture requires an updated agent and Imager package, an available matching/default WinPE build, native architecture inventory, and an Ethernet connection to the server. WinPE builds that download Imager at boot use the published package; rebuild older media that embeds Imager. The Windows volume must be fully decrypted; FFU also requires a GPT disk and unencrypted capture volumes.

The capture task appears in **Tasks → Imaging Tasks** during preparation. Cancelling it prevents capture. Preparation scripts already running are not rolled back. Existing image versions are retained; the first version becomes active automatically, while subsequent versions follow the normal activation workflow. With this option off, successful preparation shuts down the computer for manual capture.

## Permissions

Access requires Image Prep Profile View permission.
