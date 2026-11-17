# Email

<div class="ui-controls-spacer"></div>

**Server setup**

Open **Administration > Server Settings > Email**. Enter the SMTP host, port, sender address and sender name. Supply a username and password if the relay requires authentication. Enable email delivery and select **Save Changes**.

STARTTLS is enabled by default and requires the SMTP server to offer TLS with a trusted certificate. Port 587 is typical. Implicit TLS on port 465 and OAuth authentication are not supported. **None** is available for a trusted unencrypted relay. Stored passwords use the application's existing data-protection encryption; blank password fields preserve the saved secret. Back up the application's data-protection keys with the database.

**Send Test Email** uses the saved settings and sends a simple configuration test to the entered address. An accepted SMTP submission does not guarantee final mailbox delivery. Configure the **Public application URL** on General so links in notification emails point to the correct HTTPS host and application path.

## Permissions

Administrator access is required.
