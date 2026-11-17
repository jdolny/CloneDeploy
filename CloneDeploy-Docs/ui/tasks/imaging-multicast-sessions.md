# Multicast

Monitor a shared transfer of a pipable WIM to multiple devices. FFU multicast is not supported. The sender runs on the selected storage provider's host.

<div class="ui-controls-spacer"></div>

**Create multicast session**

Opens the multicast creation review. Verify the image version, provider, expected devices and transfer settings before queuing.

**Session / Sender / Status**

Identify the session, its sender and current recorded state.

**Prepared / Expected devices**

Compare participating devices with the expected membership.

**Cancel**

Stops the selected session.

**Remove session history**

Removes the retained session history.


**Session name / Image / Deployment profile (creation dialog)**

Enter a session name up to 100 characters; the initial name is Multicast. Choose an enabled WIM image and an enabled deployment profile visible in WinPE. FFU is not offered.

**Storage / sender host**

Select a provider eligible to send the selected image. Changing the image clears this selection. The sender runs on that provider's host.

**Expected receivers**

Set 1–1,000 receiving devices; the initial value is 2.

**Maximum rate (Mbit/s)**

Set the sender rate limit from 1–100,000 Mbit/s. The initial value in this dialog is 1,000 Mbit/s.

**Multicast TTL**

Set 1–32; the initial value in this dialog is 32. TTL 1 keeps traffic on the local subnet. Higher TTL does not itself configure routed multicast.

**Create session / Cancel**

Create session validates the image, profile, provider and transfer settings before creating the session. Cancel closes without creating it. Creating a session does not establish successful transfer or deployment.

## Permissions

Imaging-task and multicast operations require their corresponding permissions.
