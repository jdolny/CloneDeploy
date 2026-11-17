# Deployment Settings

Set the image and deployment profile used when deploying Windows to this device. You can assign them directly or inherit them from device groups.

<div class="ui-controls-spacer"></div>

**Effective saved deployment settings**

Read-only summary of the saved image, deployment profile and assignment source. Unsaved changes are not reflected here. Check this summary after saving to confirm which settings apply.

A saved device assignment takes precedence over group defaults. When both fields are cleared, the device inherits the defaults configured on its groups' **Imaging** tabs. All contributing groups must specify the same image and profile; conflicting or incomplete assignments block inheritance. Correct the group assignments or save a device override before deploying. Groups have no imaging priority.

**Image**

Choose the default Windows image to deploy. Disabled images appear in the list but cannot be selected.

**Deployment profile**

Choose the profile that controls how the image is deployed. OU naming and prompts are configured in this profile. Disabled profiles cannot be selected.

**Save changes**

Saves the selected image and profile, then refreshes the assignment summary. Select both fields to assign device defaults, or clear both to inherit group defaults. Leaving only one populated is not allowed.

**Saving does not start a deployment or change work already queued.** Use **Deploy image** from the device's actions to review and queue a deployment.

Assignments are checked again when queuing a deployment. If the image or profile has changed since you opened the deployment review, reload the review before continuing.

## Permissions

Editing fields and saving changes require **Device Edit** permission.
