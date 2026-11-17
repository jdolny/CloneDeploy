# Prepare for Imaging

Complete these steps before your first capture and deployment. Initial Configuration should already have established administrator access, a reachable public application URL, HTTPS and a default imaging storage provider with a **Ready** mount status. For a Linux Main App, Windows Worker should already be installed, approved and enabled using the Linux installation instructions.

This walkthrough prepares WinPE, network boot using the Main App's TFTP and PXE Proxy services, and an image preparation profile for your reference computer. If you will boot only from ISO or USB, complete steps 1 and 2, then continue with steps 5 and 6.

## 1. Install the WinPE prerequisites

Open **Administration → Imaging Settings → WinPE Builder**.

1. Confirm that the Windows builder is online. Builds run on the Windows Main App, or on Windows Worker for a Linux Main App.
2. Select **Install prerequisites**. Review the required Windows ADK and WinPE versions and Microsoft's license information, then select **Accept and install**.
3. Follow the installation in **History** and resolve any reported failures.
4. If a restart is required, restart the Windows builder host and wait for it to reconnect.
5. Confirm that **Status** reports the required toolchain as ready. If prerequisites were already installed during Windows Worker setup and the builder is ready, continue without reinstalling them.

## 2. Create and build a boot environment

Open **Administration → Imaging Settings → WinPE Boot Environments**.

1. Select **Create boot environment**, enter a name such as `Default WinPE`, and select **Save Changes**.
2. On **Configuration**, choose the keyboard layout and **Network boot method**: **iPXE** for the managed iPXE flow, or **Native PXE** for Windows network boot files. Review **Use Windows UEFI 2023 CA boot media** against the test computer's firmware trust, then save the tab.
3. On **Model matching**, select **Default x64 boot environment when no model rule matches** for your initial x64 environment. Select the ARM64 default as well if you will boot ARM64 computers. Save the tab. Model rules are not required for the initial default environment.
4. On **Drivers**, select any prepared WinPE network or storage driver packs required by your test hardware and save the tab. These drivers are separate from drivers installed into the deployed Windows image.
5. Open the actions dropdown beside **Save Changes** and select **Build**.
6. On **General**, wait for the build, storage verification and boot-file preparation to finish successfully. The boot image becomes available automatically; no separate publish step is required.

New builds capture the configured public application URL. If that URL changes, rebuild the boot environment before using it. For ISO/USB boot, download the appropriate **x64 ISO** or **ARM64 ISO** from General and prepare your boot media.

## 3. Prepare boot files and configure TFTP

Open **Administration → Imaging Settings → TFTP & Boot Files → Settings**.

1. From the actions dropdown, select **Prepare / Update PXE files**. Wait for the iPXE and wimboot downloads and verification to complete.
2. Check that the displayed **Boot script server** uses the intended server URL. Prepare the files again after changing that URL.
3. Enable **Enable TFTP** and choose the **Listen IPv4 address** for the boot network, or all interfaces if appropriate.
4. Select **Save Changes**, wait about five seconds and confirm that TFTP is running.
5. Allow UDP 69 and TFTP transfer traffic through the firewall between the boot clients and the server.

Enabling TFTP alone does not prepare the boot files. Use the preparation action even when you intend to serve the files through a Site Service.

## 4. Configure PXE Proxy

> [!IMPORTANT]
> **Remove DHCP options 66 and 67 for clients using ProxyDHCP**, including options inherited from server defaults, scopes, reservations or policies. DHCP-supplied boot instructions can cause clients to bypass the proxy. For clients on another subnet, retain the existing DHCP helpers and add a helper for the intended CloneDeploy PXE responder on the client VLAN's gateway. Understanding ProxyDHCP and IP Helpers in the navigation explains the discovery flow, network requirements and troubleshooting.

Open **Administration → Imaging Settings → PXE Proxy → Settings**.

1. Enable **Enable ProxyDHCP**.
2. Choose the **Listen IPv4 address** on the boot network.
3. Set **Server identifier override** to the server IPv4 address reachable by boot clients when listening on all interfaces. For a specific listener address, that address can be used without an override.
4. Set **TFTP server IPv4 address** to the address of the TFTP server configured in step 3.
5. Select **Save Changes**, wait about five seconds and confirm that ProxyDHCP is running.
6. Allow the required DHCP/PXE traffic through the firewall, including UDP 67 and 4011. If clients are on another subnet, configure DHCP relays/IP helpers to reach the boot services as well as the DHCP server.

The existing DHCP server still assigns client IP addresses. PXE Proxy supplies boot information; it is separate from the Site Service HTTPS reverse proxy. If DHCP already uses UDP 67 on the same host, resolve the listener conflict before testing network boot.

For a remote site's boot services, use an approved Site Service's **PXE / TFTP** tab instead of enabling the Main App's listeners for that site. Enable its ProxyDHCP and read-only TFTP roles, select its boot listening IPv4 address and TFTP address, save, and verify the reported status. Boot files and published WinPE builds are prepared on the Main App and synchronized automatically. Configure that site's DHCP relays and firewall access separately.

## 5. Verify a test boot

1. Boot a test computer from the network, or from the ISO/USB media prepared in step 2.
2. Confirm that it starts the intended WinPE environment and reaches the CloneDeploy Imager sign-in screen.
3. Sign in and verify that WinPE can reach CloneDeploy and detect the test computer's network adapter and storage. If drivers are missing, add the appropriate WinPE driver pack and rebuild the boot environment.
4. For network boot failures, inspect **Live Activity** under **PXE Proxy** and **TFTP & Boot Files**, or the Site Service's reported boot status.

## 6. Create an image preparation profile

An image preparation profile defines how CloneDeploy prepares the reference computer before capture, including agent identity handling, Sysprep and the Windows answer file. Create the profile now so it is ready when you prepare your first reference computer.

Open **Imaging → Image Prep Profiles**.

1. Select **New Image Prep Profile**, enter a descriptive name such as `Windows Reference Preparation`, and select **Create**.
2. On **General**, confirm that **Enabled** is selected.
3. On **Configuration**, review the preparation steps. Enable **Disable hibernation** and **Turn off BitLocker on fixed volumes and wait for decryption** as needed for the reference computer. Allow encrypted volumes to finish decrypting before capture.
4. For a reusable image that retains the agent, set **CloneDeploy agent** to **Reset for image capture** so deployed computers enroll with fresh identities. Choose **Uninstall before image capture** if the agent should be removed instead.
5. Enable **Run Sysprep (generalize and OOBE)** for a generalized Windows reference image. Choose whether to enable **Reboot and capture image**: enabled prepares and restarts into WinPE for automatic capture; disabled shuts down after successful preparation for manual capture. Automatic capture requires a compatible ready WinPE environment and an Ethernet connection to the server. Choose the destination image when launching preparation.
6. On **Answer File**, select **Generate answer file** to configure the architecture, regional settings, first-boot options and any required accounts or domain join. Save the generator dialog, then review the resulting XML. You can also paste your own answer file, or leave it blank to run Sysprep without a profile answer file. Use managed credentials for generated account and domain-join settings.
7. Review **Pre-Sysprep Script** and **SetupComplete**. Keep only the commands required for your reference image; scripts are optional. Do not restart or shut down the computer from these scripts.
8. Select **Save Changes**. Saving the profile does not run preparation.

Run the saved profile later from **Administrator tools** in the CloneDeploy agent on the reference computer. The agent must be installed and enrolled first; complete Prepare for Device Management and Install the Agent if needed. Finish configuring the reference computer before launching preparation, since successful preparation shuts it down or restarts it into capture.

Preparation is complete. Continue with Your First Capture and Deployment in the navigation to apply the saved image preparation profile, capture an image and configure a deployment profile. Multicast configuration is only needed if you plan to use multicast deployment.
