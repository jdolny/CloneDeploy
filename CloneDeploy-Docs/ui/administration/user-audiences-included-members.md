# Included members

Select directory members to include. Exclusions override inclusions. Membership uses the last successful full directory sync; application-access-only connections cannot supply policy audience members.

<div class="ui-controls-spacer"></div>

**Add users / Add groups / Add OUs**

Choose imported users, groups or OUs. Group membership includes effective nested memberships.

**Include descendant OUs**

For an OU, determines whether its descendant locations are included.

**Search members**

Filter the selected member list.

**Remove**

Removes a saved or pending member selection. An unavailable inclusion grants no membership; an unavailable exclusion blocks the audience until resolved.

**Save Changes**

Saves the selections for subsequent policy admission. Changes affect every policy referencing this audience without republishing.


**Search / Add selected members (member picker)**

Search the imported users, groups or OUs and select rows. Add selected members returns the selection to the audience editor; Cancel discards the picker selection. Save Changes on the audience commits membership. The same picker is used for excluded members.

**Include descendant OUs**

Shown for OU selections. Applies descendant inclusion to OUs added from the picker; it is not a user/group option. Excluding an OU with descendants excludes users below it as well.

## Permissions

User Audience View permits viewing; User Audience Edit is required to change membership.
