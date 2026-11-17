# User audiences

Open **Shared Resources → User Audiences** to define reusable policy audiences. Audiences use directory users, groups and OUs from the full-sync connection. They are separate from App User Groups, which control management and imager permissions.

Select **New audience** to create one, or select an existing name to edit it. Save changes after editing any tab. Viewing requires Policy View permission; creating, editing and deleting requires Policy Edit.

<div class="ui-controls-spacer"></div>

**General**

Give the audience a unique name and optional description. You can save an empty draft, but it grants no access. Audience changes affect every policy that references it at the next policy admission; existing admitted runs continue. A policy must release its audience assignment before that audience can be deleted.

**Included members**

Add users, groups or AD OUs. Matching any inclusion makes a user eligible unless an exclusion also matches. Group matching includes effective nested memberships. OU entries can include descendant OUs or only users directly inside that OU. There are no nested audiences or custom rule expressions.

**Excluded members**

Add restricted users, groups or OUs here. Exclusions always override inclusions, including direct user inclusions. For example, include Staff and Contractors and exclude Restricted Users. A user in both Staff and Restricted Users does not qualify.

**Assigning policies**

On a policy's **Targets** tab, select an optional **Automatic login audience** and click **Save login audience**. On its **Self Service** tab, select a **Self-service user audience**. Each purpose accepts one audience. They can be the same audience or different audiences. Direct directory selections are made inside the audience rather than on the policy. Device-only policies need no user audience.

**Any interactive user on targeted devices** remains a separate self-service option. It ignores user-audience membership and exclusions, includes local Windows accounts, and requires matching device targets.

Existing direct policy selections are converted into named audiences during the database migration, preserving the login/self-service purpose, OU scope and unavailable selectors.

Manage login-policy user targets and the independent audience that can run a policy in the portal. Self service requires an active policy.

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

**Resolved users**

Save the audience, then open **Resolved users** to inspect the users resolved from its saved membership selections. Save membership changes before checking the results.


**Name / Description (New audience dialog)**

Enter a required name up to 200 characters and optional description up to 2,000 characters. Create saves the audience and opens it for membership configuration; an empty audience grants no access. Cancel creates nothing. Creation requires User Audience Edit permission.

## Permissions

Access requires User Audience View permission.
