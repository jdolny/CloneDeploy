# Root CA

The CA private key stays on the server. Recovery requires both the application database and server keys folder; a public certificate download cannot restore the signing key.

<div class="ui-controls-spacer"></div>

**Organization / Generate CA**

Creates the installation CA, valid for 15 years. The page does not replace an existing CA, including an expired or revoked one.

**Download public certificate**

Exports the public CA for trust installation. It contains no signing key.

**Subject, serial, validity and fingerprint**

Read-only identity and lifetime information. Compare fingerprints when establishing trust.

## Permissions

Administrator access is required.
