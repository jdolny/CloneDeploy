# Security

CloneDeploy separates server administration, device authentication, and end-user access. This page describes the implemented trust boundaries and the controls administrators need to preserve when deploying the system.

## Network boundaries

The main server uses separate listeners and checks which routes each listener may serve. Changing a port does not grant access to another listener's routes.

| Surface | Transport and access |
| --- | --- |
| Management web application | HTTPS management listener. Management requests must originate from loopback, an RFC 1918 IPv4 address, or an IPv6 unique-local address. Authentication and application permissions still apply. |
| Agent, imaging, and Site Service APIs | HTTPS service listener. Each API applies its own device, service, or imaging authentication. Browser administration is not exposed through this listener. |
| Portal sign-in | HTTPS portal listener with a restricted route set. End-user and administrator-tool sign-in have separate session flows. Administrator-tool access also has a configured network allowlist. |
| Initial setup and boot delivery | HTTP permits initial management setup while HTTPS listeners are disabled. Once enabled, HTTP is restricted to boot routes. PXE boot delivery is a separate transport from authenticated agent APIs. |

The agent and Site Service APIs reject HTTP requests. Management network restrictions use the actual connection address rather than accepting a caller-supplied forwarding header as proof of private-network access. A reverse proxy can change the address the application sees: restrict access at the proxy as well, and do not publish the management surface through an unrestricted private-address proxy.

The public application URL supplies the hostname and path. Configured listener ports determine the management, service, and portal URLs. Issuing or renewing the web certificate through an installed server's UI also saves enabled HTTPS listeners; a restart applies those settings. DNS records, firewall rules, certificate trust, and client reachability remain deployment responsibilities.

## Certificate-based trust

Certificates authenticate enrolled devices, service connections, and signed main-server messages. Each role has a separate purpose: a device identity is not a server identity, and a TLS certificate cannot substitute for the server message-signing identity.

Agents must receive installation trust through securely distributed setup material. Device authorization also checks registration, revocation, and current device status; trusting a certificate chain alone does not grant access.

See Certificates for the certificate roles, trust distribution, storage, lifetimes, renewal, and recovery requirements.

## Agent enrollment and device identity

Enrollment uses a shared provisioning key over HTTPS. The server stores a SHA-256 hash for validation and a protected recoverable copy for installer generation. The key remains valid until rotated; it is not a short-lived, single-device invitation. Installer bundles containing it must be treated as credentials.

The agent generates its private key locally and submits a signed certificate signing request (CSR). The server verifies the CSR and supplies the certificate identity and extensions itself. A requested subject name or extension does not grant the device additional privileges. The device private key is not uploaded to the server.

Provisioning rules determine whether a device must already exist and whether administrator approval is required. New enrollment configurations require approval by default. Enrollment retries are bound to the installation identity and public key so a lost response can be recovered without silently creating a different identity.

After enrollment, the certificate identifies the device. Authentication checks include:

- Certificate chain, validity period, and permitted certificate usage.
- Registration by SHA-256 fingerprint and an exact match to the stored certificate bytes.
- Device certificate and issuer revocation state.
- A valid issuer and a provisioned, non-archived device record.

The server derives device identity from this authentication, rather than trusting a device ID in a request body. Live connections recheck authorization during continued operation because an established connection can outlive a revocation or device-status change.

Reset requests require provisioning credentials and a new key. They wait for administrator approval unless **Auto approve reset requests** is enabled and the request meets the enrollment rules. Automatic approval is disabled by default. Explicitly denied requests remain denied; enabling automatic approval does not clear that decision. Approved recovery preserves the matched device record while revoking its previous device certificates when replacement enrollment completes. Protect the shared key especially carefully when automatic reset approval is enabled.

Inactive pending enrollment and reset requests expire according to the configured retention period: 30 days initially, adjustable from 1 to 3,650 days. Cleanup uses the last contact time and does not expire completed device identities or clear denied decisions. Rotating the shared provisioning key blocks use of the old key for enrollment; it does not revoke already enrolled device certificates.

Do not duplicate an enrolled agent's installation identity or private key into a golden image. Treat a compromised endpoint's device identity separately from compromise of the shared provisioning key.

## Signed requests, responses, and downloads

TLS protects the connection. CloneDeploy also verifies application-level proofs so a TLS-terminating Site Service cannot simply replace an authenticated main-server command.

Enrolled agents sign requests with their device key. Verification binds the proof to the HTTP method, path and query, body hash, certificate identity, and selected security-relevant headers. The server verifies the body before controller side effects. Expiring request nonces are recorded atomically in PostgreSQL, providing replay rejection across server processes and restarts while the proof remains valid.

Ordinary agent API responses carry a main-server signature. The signed context binds the response to the request nonce, request details, and requesting device. The agent verifies the signature, response status, and body hash. Unsigned or altered responses are rejected. Large downloads use a different path: the expected SHA-256 comes from verified control data, and the complete file must be checked before use. A bare hash from the same untrusted download source would not provide this protection.

Signed messages have a maximum two-minute lifetime and are bound to their request context. Keep server and endpoint clocks synchronized. Signed WebSocket frames additionally bind message sequence and connection context. The main server holds the signing identity; Site Services do not receive its private key. See server message-signing certificates for its cryptographic profile and renewal behavior.

Forwarded device certificates are accepted only through the trusted Site Service proxy path. The server validates the proxy's own TLS identity and configured authorization, then applies the normal device certificate checks to the forwarded identity. An arbitrary certificate header is not sufficient to impersonate an agent.

Agent message signatures provide origin and integrity, not additional encryption. A proxy terminating TLS can read agent payloads that have no separate application encryption. It can also interrupt or delay service. Site Services remain security-sensitive infrastructure even though they do not hold the main server's message-signing key.

## Imaging sessions and content

Imager's protected API exchange uses an encrypted session between Imager and the Main App, in addition to HTTPS. The handshake establishes separate request and response keys, and authenticated encryption protects packet contents. Session sequence checks reject replayed requests before the enclosed operation is dispatched. These session keys are not issued to the Site Service.

Sessions last up to 24 hours and are held in Main App memory; restarting that process invalidates them. Establishing this transport session does not itself grant imaging access. Imaging login, deployment authorization and scoped content access remain separate checks.

This protection should not be read as end-to-end encryption of every content path. Direct storage downloads, SMB access and multicast have their own transport, authorization and integrity behavior. HTTP/TFTP boot files and multicast image traffic are not encrypted merely because the control APIs use HTTPS. Limit access to the imaging network and the storage paths used by each workflow.

## Policy execution and local privilege boundaries

The agent runs privileged operations through its service, while the desktop portal runs as the signed-in user. A portal request does not by itself authorize arbitrary privileged execution.

Server-side policy admission checks assignments and the compiled policy snapshot. File and credential requests are scoped to the authenticated device and its admitted, unfinished execution. System-context and user-context policy execution remain distinct; signing into the portal does not convert user actions into elevated actions.

On Windows, the portal communicates with the installed service over a local named pipe. The service obtains the caller's identity from Windows rather than trusting a submitted username. The portal verifies the pipe server against the running agent service. Network pipe access is denied. Remembered work-account tokens are held in service-private, DPAPI-protected storage; the portal receives identity metadata rather than the token.

On macOS, portal IPC checks operating-system peer credentials and the peer executable identity. The privileged daemon and logged-in user portal remain separate processes. Desktop permissions such as screen capture and accessibility remain subject to macOS controls.

Remote viewing and control require their respective application permissions as well as the effective user-consent rule. An unattended-access rule skips a user prompt; it does not grant an operator permission. Interactive terminals run with the agent's system privileges—LocalSystem on Windows and root on macOS—so access to these tools must be assigned accordingly.

These boundaries do not make an endpoint safe after a local administrator or root compromise. A privileged attacker can control the machine on which the agent runs. Likewise, a CloneDeploy administrator authorized to deploy scripts has substantial control over targeted devices; protect administrative accounts accordingly.

## End-user portal sign-in

Portal work-account identity is separate from web administrator identity. The server resolves permitted directory users and rejects disabled, missing, or ambiguous identities. Portal tokens are not accepted as web administration credentials.

Browser-assisted sign-in uses a five-minute pending request, a state value, and a proof-key challenge. The callback is restricted to an HTTP loopback address at `127.0.0.1`, an unprivileged port, and the expected callback path. This loopback exchange stays on the user's computer; it is not an exception allowing HTTP authentication to the server.

The one-time code is exchanged using the enrolled device identity and the matching proof-key verifier. The resulting session is bound to the device and local user identity. Remembered sessions are stored as token hashes on the server, and validation rechecks the directory identity. Their lifetime is configurable from 1 to 365 days. Signing out revokes the session and removes the locally retained token.

Work-account identity controls access to user-facing features. Policy actions still execute under their configured local system or user context.

## Administrator tools in the agent

Administrator-tool sign-in is a separate browser handoff with its own one-time code and proof key. It requires application-account authorization, and applies the configured authentication and MFA rules. A browser cookie alone is not an agent API credential.

Tool sessions last 15 minutes and are bound to the enrolled device, local user SID, and session identifier. The server keeps token hashes in memory and rechecks account status, security stamp, and current tool capabilities. Restarting the server invalidates these sessions. The Windows agent also ends local access on sign-out, session lock/disconnection, or agent restart.

Each protected operation still requires its tool capability. End-user portal sign-in does not implicitly grant these capabilities.

## Web authentication and authorization

The management application uses ASP.NET Core Identity and server-side permission checks. Hidden navigation is a usability measure, not the authorization boundary. Administrative services and endpoints check their required permissions independently.

For local accounts, the default password policy requires 15 characters. The default account lockout is five failed attempts for 15 minutes. These settings are configurable, so installation policy may differ. Browser login also has per-source-address rate limiting. Enrollment and portal flows have their own rate limits; application-instance limits are not a replacement for network-wide protection.

Local browser sessions use an eight-hour sliding expiration. Passive session-status checks do not renew them indefinitely. Identity security-stamp validation has no interval delay, and directory-managed application access is rechecked during cookie validation. Entra browser sessions use a separate, non-sliding lifetime capped at one hour by the application.

Web MFA is required for application administrators in the local authentication flow; account and directory-group policies can require it for additional users. Imaging MFA has a separate policy. CloneDeploy does not retain a remembered-machine MFA exemption. Entra authentication follows the configured identity-provider flow; do not assume local-account MFA and Entra policy are interchangeable.

Browser form and UI operations use the application's antiforgery facilities, including the `X-CSRF-TOKEN` header for applicable UI requests. This is separate from the certificate and signed-request authentication used by agents. Not every endpoint uses antiforgery: protocols without browser-cookie authorization have their own controls, and logout intentionally permits clearing a session without a valid antiforgery token.

## Stored secrets and recovery

Recoverable server secrets use ASP.NET Core Data Protection. On installed Windows servers, a dedicated machine certificate protects the database password and Data Protection keys. Its private-key access and the service account are part of the security boundary. Manual and Linux deployments must protect configuration and key material through their deployment environment; they do not automatically inherit Windows certificate protection.

Protect the database, server configuration, data directory, Data Protection keys, and required encryption-certificate private key as a coordinated recovery set. Database backup alone may not recover protected credentials or CA material. Encryption at rest does not protect against an attacker who controls the running service and can invoke its decryption operations.

Administrative changes and sensitive workflows record audit events, but application audit records are not an immutable external security log. Restrict database access and retain appropriate external logs and backups.

## Deployment responsibilities and limits

- Keep management access on trusted administrative networks, with controlled proxy and firewall exposure.
- Distribute provisioning bundles and CA trust securely. Rotate exposed provisioning keys and revoke compromised device identities as separate actions.
- Keep certificates valid and clocks synchronized. Server-message certificate renewal does not imply automatic renewal of every certificate type.
- Protect storage and deployment content. Approved policies and images are trusted administrative input, not sandboxed third-party programs.
- Treat HTTP/TFTP boot delivery and the imaging network as separate trust boundaries. HTTPS protection for agent APIs does not encrypt PXE bootstrap traffic or multicast image transport.
- Verify DNS, certificate names, client trust, network routing, and access controls from real client networks. A saved configuration is not a successful end-to-end security test.

Continue with Certificates, first-time setup, agent enrollment, and server settings.
