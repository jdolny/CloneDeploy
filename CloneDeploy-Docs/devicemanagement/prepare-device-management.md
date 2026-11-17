# Prepare for Device Management

Complete these settings before installing the agent on your test computers. Initial Configuration should already have established administrator access, HTTPS and a default device-management storage provider. This walkthrough configures agent features and enrollment, then prepares the deployment bundle.

## 1. Enable the agent features you will use

Open **Administration → Agent Settings → Features**.

1. Enable the features your organization intends to use:
   - **Help desk** provides the agent portal Help tab and ticket notifications. Users need a recognized, synchronized LDAP or Entra identity.
   - **Self service** provides a catalog of policies offered to the user's audience. Policies must also be configured for self service.
   - **WinGet integration** enables catalog synchronization and WinGet actions. Review and acknowledge the unattended deployment terms when enabling it.
2. Select **Save Changes**.

These switches control feature availability. They do not create self-service policies or grant application permissions. Connected agents receive feature display changes through their heartbeat; offline agents receive them after reconnecting.

## 2. Configure Agent Tools access

Open **Administration → Agent Settings → Agent tools**.

1. Add the IPv4 or IPv6 network ranges allowed to use administrator sign-in for the agent tools. Enter CIDRs separated by commas or newlines.
2. Include the networks from which both the agent and the administrator's browser connect. For example, `192.168.10.0/24` permits that subnet; `192.168.20.15/32` permits a single IPv4 address. Replace these examples with your own ranges. A single IPv6 address uses `/128`.
3. Save the changes.
4. Restart the **CloneDeploy-Server** service, then reopen the application at its HTTPS URL.

An empty list blocks direct agent-tools sign-in. Unrestricted `/0` ranges are rejected. The network rules do not replace account authentication or permissions. Connections through a Site Service use that service's access settings.

## 3. Set up branding

Open **Administration → Agent Settings → Branding**.

1. Enter the organization name and optional tagline.
2. Choose the header/sidebar and accent colors.
3. Upload a logo if wanted. Use a PNG no larger than **64 KiB** and **512 × 512 pixels**.
4. Review the preview and save the changes.

Branding applies to the agent portal and message actions that use organization branding. Devices receive saved branding at their next check-in.

## 4. Configure enrollment rules

Enrollment establishes the device's identity with CloneDeploy. A new agent presents the enrollment key, then uses its own certificate after enrollment succeeds.

Open **Administration → Agent Settings → Enrollment** and review the enrollment rules:

| Setting | Effect |
| --- | --- |
| Require an existing device record | Unknown devices wait for a record or an explicit pre-create-and-approve operation. |
| Require approval for pre-created devices | Also requires approval for matching existing records when pre-provisioning is enabled. |
| Require approval | When an existing record is not required, determines whether a valid-key enrollment proceeds automatically or waits for approval. |

Save the rules. If existing records are required, confirm the test computers have been created or imported before installation. A full directory sync can supply device records and directory targeting information, but does not replace agent enrollment.

## 5. Generate the enrollment key

Open **Administration → Agent Settings → Enrollment → Enrollment Key**. If no key has been generated, select **Generate key**.

New agents use this key when enrolling. Treat the key and prepared enrollment bundles as credentials. Rotating the key requires updated installation configurations for agents that have not enrolled; already-enrolled agents retain their certificate identities.

## 6. Prepare and download the agent release

Open **Administration → Agent Settings → Installer & Updates**.

1. If a release has not been prepared, select **Prepare agent release** and wait for it to complete.
2. Verify that the prepared version is shown.
3. Select **Download deployment ZIP** for the test computer's architecture: x64 or ARM64.
4. Save the ZIP in a restricted location and copy it to the test computer.

The ZIP contains the signed MSI and `enrollment.json`. Keep them together: the enrollment file supplies this server's connection and enrollment settings. Protect the bundle because it contains the enrollment key. Download a current bundle after changing enrollment settings that affect new installations.

Preparation is complete. Continue with Install the Agent in the navigation.
