# Deploy Actions to a Device

Select saved actions for a one-time deployment from the device connection panel. This creates action deployments, rather than changing a policy.

<div class="ui-controls-spacer"></div>

**Search actions / Action type**

Filter the available action catalog. Filters do not remove actions already selected.

**Add / Selected actions**

Add actions or drag them from Available actions to Selected actions. Select at most 100. Each selected action becomes a separate deployment.

**Move up / Move down / Remove**

Reorder the selected list using arrows or drag handles, or remove a pending selection. The displayed order is submission order; separate deployments are not a policy sequence with dependency guarantees.

**Review actions**

Validates the selected saved actions and target before submission. Changing the selected list clears the review.

**Deploy Actions Now**

Queues the reviewed actions. Connected agents are notified to fetch work; offline devices receive it later. Use the result links to follow individual deployments. Queuing is not execution success.

**Cancel / Close**

Cancel closes without submitting. After queuing, Close dismisses the result without cancelling those tasks.

## Permissions

The operator needs access to the device and permission to deploy the selected actions. Server-side admission checks still apply.
