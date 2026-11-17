# Server HTTPS

Create and renew the **Agent / imaging service certificate** here. The agent and imaging listener always requires this CloneDeploy-issued certificate, even when the Web UI and portal use an external certificate.

<div class="ui-controls-spacer"></div>

**Certificate DNS names or IP addresses**

One name or IP per line. Include the names clients use to reach the server, without ports or URL paths. Changes take effect on issuance or renewal.

**Also use this certificate for the Web UI and portal**

Selected by default. Clear it to use one shared external certificate for both browser listeners. A valid external certificate and private key must already be configured.

**Management / Agent-imaging / Portal HTTPS ports**

Selects the separate listener ports. The agent/imaging listener always uses the CloneDeploy-issued certificate.

**Save HTTPS settings**

Saves the browser certificate choice and listener ports without issuing a certificate. Restart the server to apply them.

**Issue / renew HTTPS certificate**

Issues a certificate with the supplied names and saves the displayed HTTPS settings. Restart afterward to load it.

The browser certificate choice and listener controls are available for installed servers with `server.json`. Development hosting continues to use its configured settings.

**External browser certificate**

Both the Web UI and portal use the certificate configured under `Kestrel:Certificates:Default` in `server.json`. For a PFX file, merge this section into the existing configuration:

```json
{
  "Kestrel": {
    "Certificates": {
      "Default": {
        "Path": "C:\\Certificates\\browser.pfx",
        "Password": "your-pfx-password"
      }
    }
  }
}
```

The server account needs access to the file and private key. Protect the configuration file. Certificate-store and PEM configurations are also supported. Clear **Also use this certificate for the Web UI and portal**, save, then restart. This saves `Certificates:UseManagedHttps` as `false` without changing the external certificate configuration. Both browser hostnames must be covered if they differ; separate ports do not require separate certificates.

**Renewing the service certificate**

Renewing under the **same root CA**, with the existing client hostname still covered, requires no agent configuration changes, re-enrollment, or WinPE rebuild. Clients trust the CA, not the individual HTTPS certificate. Restart the server after renewal. Replacing the CA or changing client addresses is a separate operation and may require distributing updated trust or configuration.

## Permissions

Administrator access is required.
