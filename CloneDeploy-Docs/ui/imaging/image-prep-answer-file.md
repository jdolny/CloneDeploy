# Answer File

Edit the Sysprep answer-file XML, or open Generate answer file to build it from common Windows settings. Generating replaces the XML in the editor; Save Changes on the profile persists the result. Leave XML blank to run Sysprep without a profile answer file.

<div class="ui-controls-spacer"></div>

**Sysprep answer file (XML)**

Paste or edit the answer file used during image preparation. Editing the text does not execute Sysprep. Computer naming follows the deployment profile; the generator does not add CopyProfile or driver-preservation settings.

**Generate answer file**

Opens a separate copy of the saved generator options. Save in the dialog replaces the editor XML with newly generated XML. This can replace manual edits; review the current XML first. Cancel discards dialog edits and leaves the editor untouched.

**Architecture**

Choose x64 or ARM64 for the Windows image. New generator settings default to x64.

**Windows display language**

Choose an installed image language. The generator does not install language packs. New settings default to en-US.

**Keyboard layout**

Select an input layout, use Windows default to omit it, or Custom value to enter a Windows locale/layout identifier. A custom value can contain semicolon-separated layouts; the first is the default.

**System locale / Regional format**

Sets the language for non-Unicode applications and the date/time/number/currency formatting. New regional settings use en-US. Windows default omits a setting; Custom value accepts another Windows identifier.

**Time zone**

Choose a time zone or clear the selection to keep the Windows default.

**Hide license agreement page / Hide OEM registration**

Controls these first-boot screens. New settings initially hide OEM registration and leave the license-agreement option cleared.

**Hide Microsoft account screens / Hide wireless network setup**

Controls the corresponding OOBE screens. Both are initially cleared. Available screens and behavior depend on Windows version and edition.

**Privacy / express settings**

Optionally choose the Windows express-settings value: 1, 2 or 3 (do not use express settings). Clear the selection to omit it.

**Add local account / Local account credential**

Add up to 20 account entries and select a managed credential for each. The credential username becomes the Windows local account name. The account password is resolved during an authorized deployment, not inserted as plaintext in the captured image.

**Administrator / Remove local account**

Select Administrator to add that local account to the administrators group; clear it for a standard user. New account entries initially have this selected. Remove deletes the pending account entry and clears its automatic-logon selection if applicable.

**Built-in Administrator password credential**

Optionally select a managed credential whose username is Administrator to set its password. Setting this password does not by itself guarantee that Windows enables the built-in account.

**Automatic logon account (optional) / Automatic logon count**

Select an account already configured above, including the built-in Administrator credential. Leave empty to disable automatic logon. Count ranges from 1 to 999 and initially equals 1.

**Domain DNS name / Computer organizational unit (optional)**

Set the domain and optional OU distinguished name for Windows to join on first boot. Leave blank if a policy action handles domain join. Deployment-profile OU rules or end-of-imaging selection can supply the final OU.

**Domain join credential**

Choose a managed credential authorized to join computers to the selected domain. This selection does not grant the account permissions in Active Directory.

**Registered owner / Registered organization / Product key (optional)**

Optional Windows identity and licensing values. Leave blank to omit the corresponding setting.

**Save / Cancel (generator dialog)**

Save validates the selections and returns XML and generator settings to the profile editor. Cancel keeps the previous editor contents. Neither button saves the profile or starts image preparation.

**Save Changes**

Persists the current XML and generator options on the profile. Run the saved profile from Agent Administrator tools when the reference computer is ready.

Run the saved profile from the agent to prepare the reference image. The agent stages password placeholders and server-protected credential references. During deployment, Imager requests the current passwords over its authenticated HTTPS deployment session and inserts them into the deployed answer files before Windows boots. The captured image remains free of those passwords. Exported XML containing managed-password placeholders requires this image-prep workflow; it is not a standalone Windows installation answer file.

After Windows finishes setup and OOBE, a SYSTEM task removes the staged answer files from the supported Sysprep, Panther, and root locations. It retries at startup and does not depend on SetupComplete. Automatic logon uses Windows' own logon credentials and count independently of answer-file cleanup.

Password rotation takes effect on subsequent deployments without recapturing. Changing a credential's username requires recapture. Disabling or deleting the image prep profile, or removing its credential selection, prevents older captures from resolving those credentials. Credentials referenced by a profile cannot be deleted.

This feature requires the updated server, agent, and Imager. Existing custom answer files retain their contents after the database migration.

## Permissions

Image Prep Profile View permits viewing. Image Prep Profile Edit is required to generate or save changes.
