# Initial Configuration

Complete these steps in order after installing the Main App to establish administrator access, HTTPS and content storage. Agent enrollment and release preparation are covered in Prepare for Device Management.

## 1. Create the first administrator

1. Open the server's initial HTTP address in a browser, using the port selected during installation.
2. Complete the first-administrator form. Use a password of at least **15 characters** with the default password policy, and satisfy any additional requirements shown by the form.
3. Create the account and sign in.

All administrator web sign-ins require MFA. When prompted, scan the QR code with your authenticator app, or enter the displayed setup key manually. Enter a current code and select **Verify authenticator**. Once verification succeeds, select **Sign in again** and sign in with your password and authenticator code.

Read the installation welcome and license information. Scroll to the bottom and select **Continue** to proceed, or **Sign out** to leave setup.

## 2. Set the server addresses

> [!IMPORTANT]
> **Set the correct public application URL before proceeding with setup or deploying agents.** Agents use this URL to connect to CloneDeploy, whether they are on the internal network or using an internal or external Site Service. Choose the final fully qualified DNS name and ensure it resolves correctly and is reachable from every network where agents will connect. An incorrect or unreachable URL can prevent agents from connecting, even when a Site Service is available.

Open **Administration → Server Settings → General**.

1. Set **Public application URL** to the server's HTTPS address, for example `https://clonedeploy.example.org/`. Use the fully qualified DNS name clients will use.
2. If clients connect from multiple sites or externally, ensure this hostname resolves to the appropriate reachable address from each network. Configure DNS, routing and firewall access separately; saving the URL does not configure them.
3. Under **Site Service connections**, enter the **Main server IP** reachable by Site Services and Windows Worker.
4. Select **Save Changes**.

## 3. Create the certificate authority and HTTPS certificate

Open **Administration → Server Settings → Certificates**.

1. Enter your organization name and select **Generate CA**.
2. Select **Server HTTPS**.
3. Check the certificate DNS names. The application hostname should already be populated from the public application URL. Names must match the addresses used by clients; enter names or IP addresses without URL schemes, ports or paths.
4. For the initial setup, leave **Also use this certificate for the Web UI and portal** selected. Review the management, agent/imaging and portal HTTPS ports.
5. Open the actions dropdown and select **Issue / renew HTTPS certificate**.

Issuance saves the HTTPS settings. The server must be restarted before the new certificate and listener settings take effect; finish the remaining setup steps first.

## 4. Create the main storage provider

Open **Administration → Server Settings → Storage Providers**.

1. Select **New storage provider**.
2. Enter a name such as **Main Storage** and select local storage on the Main App.
3. Select **Make default for imaging** and **Make default for device management** for the features you use.
4. Enter a local content path, for example `C:\CloneDeployStorage`. Ensure the server service account can access this location and the volume has room for software, images and other deployment content.
5. Select **Create**.
6. Verify that the provider's mount status is **Ready** before continuing.

## 5. Restart and verify HTTPS

1. Restart the **CloneDeploy-Server** Windows service.
2. Open the configured public application URL over HTTPS, for example `https://clonedeploy.example.org/`.
3. Confirm that the hostname and HTTPS port are correct, then sign in and verify that the main storage provider remains **Ready**.

A browser trust warning is expected if the server uses a CloneDeploy-issued certificate and that browser does not trust the CloneDeploy CA. Resolve this by distributing the verified public CA certificate to the clients' **Trusted Root Certification Authorities** store, or by configuring a certificate from a trusted CA for the Web UI and portal. A hostname mismatch or expired certificate needs correction rather than a trust-store change.

The agent/imaging listener continues to require its CloneDeploy-issued certificate even when the browser listeners use an external certificate. Restart after changing browser certificate settings.

Basic server setup is complete. Continue with Prepare for Device Management before installing agents, or Prepare for Imaging under Imaging & Deployment to configure WinPE and the appropriate boot services.

Before broad deployment, use a small test group to confirm enrollment, inventory, a harmless policy and a full capture/deployment workflow for the features you use. Check certificate trust and server URL reachability from the client networks, and configure additional administrator access for recovery.

## Additional Recommended Configuration

After basic server setup, configure the integrations your organization uses. Email, Microsoft sign-in and directory synchronization are optional. The directory walkthrough below assumes a hybrid environment where Active Directory supplies device and policy data and Entra supplies application-access identities.

### 1. Configure email

Open **Administration → Server Settings → Email**.

1. Enable email delivery.
2. Enter the SMTP host, port, sender address and sender name. Supply credentials if the relay requires authentication.
3. Select the transport security supported by your relay. STARTTLS is the default; port 587 is typical. Implicit TLS on port 465 and OAuth authentication are not supported.
4. Select **Save Changes**.
5. Enter a test recipient and select **Send Test Email**. Verify receipt in the mailbox.

Notification links use the public application URL configured during server setup.

### 2. Register CloneDeploy in Microsoft Entra

Complete this section if you plan to use Microsoft sign-in or synchronize an Entra directory.

1. In the Microsoft Entra admin center, open **App registrations** and select **New registration**.
2. Enter **CloneDeploy** as the name, select accounts in your organizational directory only, and select **Register**.
3. On the registration's **Overview** page, copy the **Directory (tenant) ID** and **Application (client) ID**.
4. In CloneDeploy, open **Administration → Server Settings → Microsoft Entra**. Paste the IDs into their corresponding fields and select **Save Changes**. Leave Microsoft sign-in disabled until the certificate and access mappings are ready.
5. From the actions dropdown, select **Generate certificate**, then **Download pending public certificate**.
6. In the Entra registration, open **Certificates & secrets → Certificates → Upload certificate**. Select the downloaded `clonedeploy-entra-pending.cer` file and select **Add**.

CloneDeploy retains the private key. Upload only the downloaded public certificate; a client secret is not needed. These steps follow Microsoft's application registration and certificate credential workflow. [Microsoft registration reference](https://learn.microsoft.com/en-us/graph/auth-register-app-v2).

### 3. Register the sign-in callbacks

In the Entra app registration, open **Authentication** and add the redirect URIs as **Web** platform entries. Depending on the portal layout, use **Add a platform** or **Add redirect URI**.

For a server named `clonedeploy.example.org` using the default ports, the callbacks are:

| Purpose | Web redirect URI |
| --- | --- |
| Management web sign-in | `https://clonedeploy.example.org/signin-entra` |
| Endpoint portal sign-in | `https://clonedeploy.example.org:8444/signin-entra-portal` |
| Agent administrator sign-in, when used | `https://clonedeploy.example.org:8444/signin-entra-agent-admin` |

Replace the example hostname, ports and any application path with your actual configuration. Register each callback needed by your deployment and save the Web platform configuration. The browser must be able to reach the corresponding HTTPS listener and trust its certificate.

### 4. Grant Microsoft Graph permissions

In the registration, select **API permissions → Add a permission → Microsoft Graph**.

1. Under **Delegated permissions**, select **User.Read** if it is not already present, then add the permission.
2. Add **Application permissions** for **User.Read.All** and **GroupMember.Read.All**. These allow unattended directory synchronization of users and group memberships.
3. If Entra will perform **Full directory sync**, also add **Device.Read.All**. It is not needed for the application-access-only Entra connection used in this hybrid walkthrough.
4. Select **Grant admin consent** for your tenant using an account authorized to grant those permissions. Verify that the required application permissions show granted status.
5. Return to CloneDeploy's **Microsoft Entra** settings. Select **Activate for SSO** after the certificate upload. To validate Graph access during activation as well, select **Also verify directory read permissions during activation**, then **Test and activate**.

Certificate activation alone does not grant users access to CloneDeploy. Complete the directory sync and group mappings below before relying on Microsoft sign-in.

### 5. Add the LDAP directory

Open **Administration → Server Settings → Directory Connections**.

1. Select **Add LDAP directory**, give the connection a descriptive name, and leave **Portal / application access only** unchecked. Select **Create**.
2. On **Configuration**, enter the domain controller hostname and LDAPS port. The controller certificate must match that hostname and be trusted by the CloneDeploy server.
3. Select or create a managed read-only AD credential. Enter search base DNs if you want to limit the imported domain scope; leave them empty to import the whole domain.
4. Enable the connection and save the changes.
5. Use the back arrow to return to the list and select **Sync now** for the connection.
6. Wait for a successful run, then verify the imported users, groups, devices and organizational units.

Only one directory can perform full directory sync. In this arrangement, LDAP supplies the AD computer records and OU information used for device management and targeting.

### 6. Add the Entra directory

In **Directory Connections**, open the dropdown beside **Add LDAP directory** and select **Add Entra directory**.

1. Enter a descriptive name, select **Portal / application access only**, and select **Create**.
2. Open **Configuration**, enable directory sync, and save the connection. It uses the Entra application IDs and active certificate configured earlier.
3. Return to the directory list and select **Sync now**.
4. Verify that the run succeeds and that the expected users and groups appear.

Application-access-only mode imports users, groups and memberships for sign-in permissions without supplying managed devices or policy targets. For an Entra-only organization, configure Entra for full directory sync instead and grant **Device.Read.All**; do not also configure LDAP as a full-sync source.

### 7. Grant application access and enable Microsoft sign-in

1. Open **Administration → App User Groups** and create or select a group for the intended users.
2. Configure its **Permissions** for their work in CloneDeploy.
3. On **Directory groups**, add the appropriate imported AD or Entra groups and save.
4. If this is an administrator group, enable **Grant full administrator access to directory members** on **General**. Keep a separate local administrator for recovery.
5. Return to **Administration → Server Settings → Microsoft Entra**, enable **Microsoft sign-in**, and select **Save Changes**.
6. Test Microsoft sign-in with an intended user in a separate browser session. Verify both access and the assigned permissions before ending the local administrator session.

An Entra app registration or tenant administrator role alone does not grant CloneDeploy access. The user must match a configured application group through the imported directory memberships. Administrator web sign-ins also require CloneDeploy's MFA flow.

### 8. Schedule directory synchronization

Open **Administration → Server Settings → Task Scheduler** and select **Sync Directories**.

1. Enable **Automatic scheduling**.
2. Select an existing schedule or create a new one with the required interval.
3. Save the task settings.
4. Verify the next-run information and check the result after the scheduled run.

CloneDeploy evaluates directory access using the last successful membership sync. Schedule regular synchronization so directory membership changes reach the application.

