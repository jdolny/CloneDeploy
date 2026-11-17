# Install the Agent

Install on one or two test computers before deploying to a larger group. The computer must be able to resolve and reach the server's configured HTTPS address.

## Windows

### 1. Extract the deployment bundle

Extract the deployment ZIP downloaded during preparation into a local folder on the test computer. Use the package matching the computer's architecture. Keep `enrollment.json` beside the MSI for automatic discovery, or supply its path when running the installer.

The enrollment file includes the server address, enrollment key and public CA certificate. A separate certificate file is not required.

### 2. Run the installer

For an interactive installation, run the MSI with administrator privileges and complete the installer.

For an unattended x64 installation, run this command from an elevated command prompt in the extracted folder:

```cmd
msiexec.exe /i "CloneDeploy-Agent-x64.msi" /qn /norestart
```

On a fresh installation, the MSI looks for `enrollment.json` in the MSI's source folder. To use a file in another location, pass its full local path through `ENROLLMENT_FILE`:

```cmd
msiexec.exe /i "CloneDeploy-Agent-x64.msi" ENROLLMENT_FILE="C:\Deploy\enrollment.json" /qn /norestart
```

The enrollment file must be on a local disk. UNC paths and mapped network drives are rejected; copy the file locally before installing.

For ARM64, use `CloneDeploy-Agent-arm64.msi` with the exact filename supplied in the bundle. Deployment software can run the installer as administrator or SYSTEM. For a fresh installation, provide `enrollment.json` beside the MSI or through `ENROLLMENT_FILE`.

Repairs and upgrades preserve existing enrollment unless an explicit `ENROLLMENT_FILE` path is supplied. Reinstalling an enrolled agent is not the same as enrolling a new identity.

## macOS

On an Apple Silicon Mac, extract the prepared deployment ZIP and keep `enrollment.json` beside the PKG. Open the PKG to install, or run it as root through your deployment tooling:

```bash
sudo installer -pkg "/path/to/CloneDeploy-Agent-macOS-arm64.pkg" -target /
```

### Deploy through MDM

If your MDM distributes only the PKG, use its preinstall script to place the prepared `enrollment.json` at:

```text
/Library/Application Support/CloneDeploy/Agent/EnrollmentPending/enrollment.json
```

Create the staging directory as root with permissions `700`, and write the file with root ownership and permissions `600`. The CloneDeploy directories containing it must also be owned by root and must not be writable by other users. Use the enrollment file from the prepared ZIP; it already includes the server address, enrollment key and CA certificate.

The installer uses an enrollment file beside the PKG first. If none is supplied, it preserves an existing agent configuration. Only a fresh installation checks the MDM staging location. Invalid enrollment files fail installation instead of silently switching to another source.

After a successful installation using the staged file, the installer deletes that file and removes the empty staging directory. A failed installation retains the file for another attempt. Keep only `enrollment.json` in this directory: automatic updates refuse to run while `EnrollmentPending` exists. If you distribute a PKG to an already configured Mac, do not stage a new enrollment file.

Automatic updates download only the PKG and retain the agent's saved configuration and identity.

## Review enrollment

1. In CloneDeploy, open **Devices → Enrollment Requests**.
2. If the test computer is waiting for approval, verify the request belongs to that computer and approve it. If your rules require an existing device record, create or match the record through the available approval workflow.
3. Open the device in **Devices** after enrollment completes.

Automatic enrollment may complete without a pending request when your saved rules allow it. A denied request remains blocked until explicitly addressed; changing a rule alone does not clear a prior denial.

## Verify the connection

Confirm that the device record shows a recent check-in and that its connection status reflects the running test computer. Review its inventory, and use the device's check-in or inventory action when you need an immediate update.

If the device does not appear or remains offline, check pending enrollment requests, the agent service, DNS, HTTPS connectivity and agent logs. Resolve enrollment and connectivity before deploying policies.

## Verify the user experience

Open the agent portal on the test computer and confirm the saved branding. Check the features enabled during preparation. Help desk and self-service availability also depend on the user's directory identity, audience and permissions; enabling a global switch alone does not populate them.

If you will use administrator tools, test sign-in from an allowed network with an authorized account.

The test device is now ready for Create Your First Policy. Once the pilot succeeds, use the same deployment bundle and enrollment process with your organization's deployment tooling for additional devices.
