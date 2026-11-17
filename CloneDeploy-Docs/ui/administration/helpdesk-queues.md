# Queues

Organize Help Desk work into queues. Queue membership and ticket-management permissions determine staff access; notification recipients do not automatically gain access.

<div class="ui-controls-spacer"></div>

**Add queue / Name**

Create a queue or open an existing one to edit its name and membership. The built-in Unassigned queue cannot be renamed or deleted.

**Queue members**

Choose staff for the queue. Members need the grant to manage their tickets and queues, or all tickets.

**Notify these application users**

For Unassigned, chooses email recipients only. This does not grant queue access or assign tickets automatically.

**Enable email notifications for this queue**

Uses saved SMTP settings and staff email addresses. Assigned tickets notify their assignee; unclaimed tickets notify eligible members or Unassigned recipients. Requester follow-ups are batched. Administrators are not automatically subscribed and this sends no email to end users.

**Save changes**

Saves queue settings.

**Delete**

Removes an unused normal queue. References from tickets or rules prevent deletion.


**Create / Cancel (queue dialog)**

Name is required and limited to 100 characters, except that Unassigned keeps its fixed name. Create saves a new queue; Save changes updates an existing queue's name, members and email setting. Cancel discards those edits without changing tickets.

## Permissions

Administrator access is required.
