# Remote viewing

View a remote desktop and, when permitted, use its mouse and keyboard. Access depends on the effective remote-access consent rule and the endpoint session. Sessions end after thirty minutes.

<div class="ui-controls-spacer"></div>

**Desktop session**

Choose a session before connecting. Disconnect before selecting another. Windows can expose console and RDP sessions; macOS uses its available desktop session.

**Refresh sessions**

Reloads available sessions while disconnected.

**Start viewing**

Requests access to the selected session using the effective consent rule shown in the connection panel. When approval is required, the user receives a prompt naming the requester; viewing starts only after acceptance. An unanswered request expires after 60 seconds. macOS also requires the appropriate screen-recording and input permissions.

**Cancel request / Disconnect**

Cancels a pending request or ends the active session. The user can also end sharing from the Endpoint Portal. Sharing can end when the selected session logs off or disconnects, or when the agent loses contact with the portal. Reconnect and obtain any required approval before continuing.

**Mouse and keyboard**

Enables remote input when you have control permission. Click the desktop to focus keyboard input.

**Frame rate / Quality**

Adjust the viewing rate and image quality. Lower settings reduce bandwidth and endpoint load.

**Monitor**

Select the remote monitor to display.

**Ctrl+Alt+Delete**

Sends secure attention to a supported Windows session.

**Send clipboard / Get clipboard**

Transfer text between local and remote clipboards, subject to browser access. The limit is 16,384 characters; files are not transferred.

**Text clipboard / Send text / Copy text locally**

Use the fallback text box when browser clipboard access is unavailable. Send its text remotely or copy received text locally.

**Fullscreen**

Expands the viewer using browser fullscreen.

## Permissions

Device View is required to open the device. Device Remote View permits viewing; Device Remote Control is also required for remote input and clipboard controls.
