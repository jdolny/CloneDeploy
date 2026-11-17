# Microsoft Entra

Configure Microsoft sign-in and its application certificate. An Entra app registration, required administrator consent and a matching redirect URI must exist in the tenant. Directory import settings are maintained on the directory connection.

<div class="ui-controls-spacer"></div>

**Directory (tenant) ID**

Enter the tenant identifier for the application registration.

**Application (client) ID**

Enter the application registration identifier.

**Enable Microsoft sign-in**

Enables sign-in through the configured tenant. Configure Public application URL first so the displayed redirect URI is correct. Sign-in still requires mapped directory-group access.

**Active certificate expiry**

Review the certificate currently used by the application and its expiry warning.

**Generate certificate**

Creates a pending application certificate. CloneDeploy retains the protected private key; only the public certificate is uploaded to the Entra app registration.

**Download pending public certificate**

Downloads the public certificate to register in the tenant.

**Also verify directory read permissions during activation**

Includes directory-read verification when activating the pending certificate. Leave it off for an SSO-only installation that does not need directory synchronization.

**Test and activate / Activate for SSO**

Activates the pending certificate after the corresponding application registration is ready. Review the access verification options before activation.

**Download active public certificate**

Downloads the currently active public certificate.

**Discard pending certificate**

Discards a pending replacement without replacing the active certificate.

**Save Changes**

Saves the application connection and sign-in settings. Certificate changes use their own activation flow.

## Permissions

Administrator access is required.
