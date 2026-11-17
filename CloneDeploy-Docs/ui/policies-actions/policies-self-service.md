# Self Service

Manage login-policy user targets and the independent audience that can run a policy in the portal. Self service requires an active policy.

<div class="ui-controls-spacer"></div>

**Offer this policy in self service**

Makes the policy eligible for portal presentation to its saved audience.

**Portal category / display name / description / icon**

Controls portal organization and presentation. Empty display name uses the policy name.

**Custom PNG / Remove custom icon**

Uploads a logo up to 64 KiB and 512 by 512 pixels, or returns to the built-in icon.

**Self-service audience**

Choose a reusable user audience or any signed-in Windows user. Any-user mode includes local accounts and requires matching device targets.

**Restrict self service to device targets**

Limits where eligible users can launch the policy.

**Automatic login audience**

Optional single audience for automatic login policies. When device targets also exist, both must match.

**Self-service user audience**

Required single audience when offering directory-based self service. It may differ from the login audience.

**Create audience / Manage audiences**

Opens Shared Resources → User Audiences in another tab. Add included and excluded users, groups and OUs there.

**Refresh audience choices**

Loads newly created audiences without discarding unsaved policy settings.

**Check selected user**

Previews eligibility against saved/current directory information.

**Reload user access / Save user access**

Reloads or saves user targeting. Existing admitted runs continue; changes affect later admission.

**Execution behavior**

Manual runs retain execution permissions, conditions and completion actions but bypass automatic schedules/frequency. Empty directory audience grants no self-service access.

Audience exclusions override inclusions. A missing or unavailable exclusion causes its audience to grant no access until resolved. Audience changes affect all policies using it, without republishing. The any-user option ignores the directory audience and continues to require matching device targets.


**Audience review dialog**

The activation review can display the resolved automatic-login and self-service users, or the any-interactive-user mode. This is a read-only preview of current saved membership. Close returns to the review without changing the audience.

## Permissions

Access requires Policy View permission.
