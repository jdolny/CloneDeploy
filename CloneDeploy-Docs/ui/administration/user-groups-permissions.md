# Permissions

Group permissions are added to members’ direct permissions and grants from other groups. A No value means this group does not grant the permission; it does not override another grant.

<div class="ui-controls-spacer"></div>

**Permission switches**

Select the access this group grants.

**Save Permissions**

Saves grants. Changes take effect when users next sign in.

The Device category includes Add device, Edit device, Deploy image (PXE and agent), Deploy actions now, Reboot, Shutdown, Terminal, Services, Processes, Checkin now, Inventory now, View remote viewing, Control remote viewing, and Approve enrollment / reset request. Grant View Devices to open device pages.

Edit device covers general details, renaming, custom attribute values, comments, attachments, and device deployment settings. It does not grant permission to deploy or run commands. Remote control includes viewing; view-only sessions cannot send keyboard, mouse, Ctrl+Alt+Delete, or clipboard commands. Services and Processes each include both listing and their corresponding management commands.

Administration remains restricted to administrators. Device archive and delete use Edit device. Wake up and Uninstall software have independent device permissions; uninstall permits immediate removal from one device. These grants appear on both user and user-group permission pages.

Device Groups has five independent grants: View group, Edit group (including creation and deletion), Deploy image, Deploy multicast, and Group power management. Grant View group to open the group pages. Edit group allows general settings, static membership, dynamic criteria and recalculation, policy assignments, and imaging defaults. Neither deployment grant allows editing groups or deploying to individual devices outside the selected group. Group power management permits group wake-up and assigning, editing, or removing power schedules. Scheduled wake-up and shutdown check the configuring user's current group power permission. This does not grant direct power control over individual devices.

Policies and Actions share View and Edit permissions. Existing Policy.View and Policy.Edit assignments carry over to this combined category. View opens both workspaces and their history, configuration, and attached files. Edit includes creating, updating, archiving, restoring, and deleting policies/actions and managing action files. Activate (including Re-run) and Deactivate are independent policy permissions; Edit alone cannot publish or stop a policy. Deploy actions now allows immediate deployment of saved actions to selected devices, groups, or OUs. It does not grant editing, policy activation, software-inventory uninstall, or access to Admin settings. The device-specific Deploy actions now permission remains separate.

Imaging permissions separate image creation, editing (including deletion), viewing, capture, version approval, and deployment of unapproved versions. New captures are unapproved, including the first active version. Selecting an active version does not approve it. Approving a version also makes it eligible for replication. Existing active versions retain approval during the upgrade. Testing an unapproved version requires both the relevant deployment permission and Deploy unapproved version; the device deployment dialog offers a version selector to testers with View images.

Deployment Profiles, Driver Packs, Image Prep Profiles, and OU Naming Rules each have separate View and Edit grants. View opens their pages; Edit includes create, update, and delete. OU Naming Rules View also allows testing rule sets. Using a configured profile, its drivers, or its OU naming rules during an authorized deployment does not require browsing those catalogs.

Access Imager (API) permits operator sign-in; it does not grant capture or deployment. Deploy assigned image from Imager permits manual deployment of the device's effective image and profile, including inherited assignments. Choosing another image, version, or profile also requires the Imager override grant and the appropriate View permissions. Driver overrides require View driver packs. Without image/profile view access the Imager shows only the assigned deployment for confirmation. Already authorized queued tasks continue using their task credentials.

Agent Reboot and capture requires Capture image and View image prep profiles. Selecting an existing capture destination requires View images; creating one requires Create image. Capture alone does not grant bootable USB creation or driver pack creation. Agent and Imager permissions are rechecked against current direct and group grants.

Shared Resources has independent View and Edit permissions for Custom Attributes, Schedules, User Audiences, Self-service Categories, and Tags. View opens the resource pages and exposes their catalogs in selection lists. Edit includes creating, updating, and deleting definitions; category Edit also permits reordering. Grant View as well as Edit for management through the UI.

Assigning an existing resource requires the receiving device, group, policy, or action's editing permission, not permission to edit the shared definition. Changing a device's custom attribute value remains covered by Edit device. Existing assignments and scheduled work continue without resource View access. Policy permissions no longer grant management of User Audiences or Self-service Categories; grant those resource permissions separately.

Reports have separate access permissions for Help Desk, Devices, Users, Windows Updates, and Software. Each covers the report and its built-in features, including saved reports and exports where available. Windows Updates access includes approval changes and test deployments. Software removal all is a separate permission; individual device software removal retains its device permission. Help Desk reports continue to respect ticket visibility grants.

Tasks default to My tasks. Authenticated users can view and cancel tasks they initiated, identified by user ID. View all tasks includes other users and system tasks; Manage all tasks also permits managing them and includes viewing all. These grants cover action deployments, imaging tasks, and multicast sessions. Cancellation retains execution-state safeguards. Task management does not grant deployment or retry permissions. Tasks without a recorded initiating user are only available in All tasks.

Deployment Rules have independent View and Edit grants (Edit includes creating, changing, and deleting rules). Standalone multicast sessions use Deploy multicast. Deploy actions now covers immediate and next-check-in delivery. Dashboard task data follows ownership and task grants; device and Windows Update summaries follow their respective permissions. Administrative diagnostics and settings remain administrator-only.

## Permissions

Administrator access is required.
