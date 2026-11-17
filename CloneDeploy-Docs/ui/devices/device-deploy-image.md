# Deploy an Image

Review the saved image assignment before queuing imaging for a device. Deploy image queues work for WinPE; Deploy image through agent also stages WinPE and restarts a supported connected Windows device. Either can overwrite the destination disk.

<div class="ui-controls-spacer"></div>

**Image / Deployment profile / Source**

Shows the effective saved assignment, including group inheritance. These values are reviewed here rather than edited in the dialog. Correct missing, conflicting or disabled assignments before continuing.

**Image version**

In the ordinary deployment dialog, authorized testers can select a version, including an unapproved test version. Leave empty to use the active version. This selector requires Image View and Image Deploy Unapproved permission.

**Send wake-up**

Shown when you have Device Wake permission and initially selected. Requests wake-up after queuing ordinary deployment; it does not guarantee the device boots or enters WinPE.

**Reload review**

Reloads the saved assignment and validation. In the ordinary dialog it also clears the pending version selection. Reload after changing an image/profile elsewhere; queuing verifies the reviewed assignment again.

**Queue deployment**

Queues the ordinary deployment for the reviewed device and optional version. A successful queue does not confirm imaging completed. Follow the task and boot the device into the intended WinPE environment.

**WinPE boot environment / Build**

In the agent-assisted dialog, shows the selected environment, architecture, matching reason and build. The readiness check must succeed before submission. The dialog does not offer a boot-environment selector.

**Deploy and restart**

In the agent-assisted dialog, downloads WinPE and requests a restart after a 60-second countdown. The device then enters imaging. Save user work first and follow Imaging Tasks. A failed readiness check blocks this action.

**Cancel**

Closes without submitting. Closing a successful review/result does not cancel imaging already queued.

## Permissions

Device Deploy Image is required for ordinary deployment. Agent-assisted imaging requires Device Deploy Image Through Agent. Optional wake-up and unapproved-version testing require their separate grants.
