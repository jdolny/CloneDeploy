# Storage Provider Configuration

Create or edit a storage provider from **New storage provider** or a provider's name in **Administration → Server Settings → Storage Providers → Storage Providers**. The same dialog is available from a Site Service's **Storage** tab. The selected storage host accesses the files; paths and permissions must be valid on that host.

<div class="ui-controls-spacer"></div>

**Name**

Required, unique provider name used in storage selections. Renaming a provider does not move its files.

**Storage host**

Leave empty to use the Main App, or select a registered Site Service. Windows Worker is not a storage host. The host can only be selected when creating the provider; to change hosts later, create another provider. A Site Service must be approved before its provider can be enabled.

Selecting the Main App initially enables a new provider. Selecting a Site Service initially disables it so its configuration can be completed before enabling replication.

**Enabled**

Allows the provider to participate in storage operations. A default provider must remain enabled; choose another default before disabling it. Existing content, active moves or cleanup can prevent changes that would disconnect stored files.

**Imaging**

Allows imaging content: images, current WinPE boot images, boot files and drivers. Selected by default for new providers. Move or remove existing content for this purpose before clearing it.

**Device management**

Allows software installers and action/policy files. Selected by default for new providers. At least one purpose must be selected. Move or remove existing content for this purpose before clearing it.

**Make default for Imaging**

Available for enabled Main App providers with Imaging selected. Makes this provider the destination for new imaging content, replacing the previous imaging default. Existing files stay on their current providers. The first enabled eligible Main App provider becomes the default automatically if no imaging default exists.

**Make default for Device Management**

Available for enabled Main App providers with Device management selected. Makes this provider the destination for new management content, replacing the previous management default. This is independent of the imaging default and does not move existing files. The first enabled eligible Main App provider becomes the default automatically if no management default exists.

**Preferred client networks**

IPv4 or IPv6 CIDRs separated by lines or commas, for example `192.168.20.0/24` or `2001:db8:20::/64`. More-specific matching networks take priority; equally specific eligible copies are balanced by active transfers. Selection requires an available, verified copy of the requested content.

Main App storage remains the fallback. Site storage requires a matching network; blank site ranges allow replication without making that provider a preferred client download source. Connecting through a Site Service does not automatically give its storage priority.

**Type**

Choose **Local** for a directory on the selected host or **SMB** for a network share. Changing type does not migrate content. Changes that would disconnect existing content can be rejected until that content is moved.

**Path**

Shown for Local storage. Enter an absolute directory on the selected host, such as `C:\CloneDeployStorage` on Windows or `/var/lib/clonedeploy/storage` on Linux. The host's service account needs read and write access. This is a filesystem location, not a URL. The same host cannot have duplicate provider paths.

**UNC share path**

Shown for SMB storage. Enter a UNC path such as `\\fileserver\images` or `\\fileserver\images\clonedeploy`. The selected host must be able to access that folder. Share and filesystem permissions must allow the operations you intend to perform.

**Linux mount path**

For SMB on Linux, first mount the share through the operating system, then enter the absolute mount location, such as `/mnt/images`. Keep SMB selected and supply both the UNC share path and the Linux mount path for the same directory. Linux mount credentials are configured on Linux, rather than by this dialog.

The field is required when the selected host reports Linux. If a Site Service has not yet reported its operating system, the dialog displays guidance until its heartbeat supplies that information. Windows hosts cannot use a Linux mount path. Configure mount/service dependencies so the service does not write into an unmounted local directory.

**Imaging direct connect**

Available for Main App SMB storage. With Imaging selected, allows WIM and FFU capture and deployment directly over SMB from WinPE. When disabled, imaging uses HTTPS. The selected managed credential is delivered over the authenticated HTTPS imaging session for the SMB connection; WinPE must also be able to reach and access the share.

Site-hosted providers serve clients over HTTPS and do not offer direct SMB imaging. The dialog clears direct connect when saving a site-hosted provider, a non-SMB provider or a provider without the Imaging purpose.

**WinPE SMB path override**

Shown when Imaging direct connect and Imaging are enabled for Main App SMB storage. Optionally enter a UNC path WinPE should use instead of the provider's UNC share path. Leave blank to use the provider path. Both paths must reach the same share and folder using the same credentials; the override is not a separate content location.

**Managed Credentials**

Shown for SMB. Select an account authorized to access the share. On Windows, leave empty to use the storage host's service identity. On Linux, the operating system manages mount credentials; the selected credential can still be supplied to an authorized Imager for Main App direct SMB access.

**New Credential**

Opens the managed-credential dialog. Enter a **Name**, **Username** and **Password**, then select **Create**. The new credential is selected in the provider dialog. The account still needs permissions on the share; creating a managed credential does not grant them. Cancelling the provider dialog does not delete a credential already created separately.

**Create / Save**

Creates a provider or saves edits after validation. Required settings, unique names and paths, valid networks and eligible defaults are checked. If credentials or storage hosts could not be loaded, reopen the dialog after resolving the error. Saving settings does not confirm storage readiness; review the provider's mount status or use its Test action afterward.

**Cancel**

Closes without saving provider edits. Content moves and credentials already created through their own actions are separate operations.

**Move existing content**

Shown when editing an existing provider. Main App providers can move content to another enabled Main App provider. Site providers hold replicas; change replication targets to relocate those copies instead.

**Destination provider**

Select another enabled Main App provider. It must be ready and support every purpose selected for the move.

**Content to move**

Choose **Imaging**, **Device management** or **All content**. All content is the initial selection. Blocking references or another pending move/cleanup involving either provider must be resolved first.

**Move content**

Starts a separate background operation using the saved provider settings, not unsaved dialog edits. Files are copied and verified before ownership changes. Originals are cleaned up after active transfers finish and a two-hour grace period. New-content defaults remain unchanged. Closing or cancelling the dialog does not cancel a started move.

**Move progress**

Shows the source, destination, selected purposes, state, percentage and any error. Status refreshes approximately every three seconds. After ownership moves, cleanup can remain pending while transfers or the grace period finish.

**Retry / Keep remaining originals and finish**

Retry is available for a failed move or cleanup. After correcting its reported error, resume the failed operation.

Keep remaining originals and finish is available only when ownership has moved and cleanup failed. It completes the job while retaining remaining original files on the source. These files require separate administrator cleanup if no longer needed.

## Permissions

Administrator access is required to create, edit, test and move storage providers.
