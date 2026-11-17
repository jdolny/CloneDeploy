# Dynamic Group Criteria

Open Edit dynamic criteria on a dynamic group to select devices from stored inventory and directory information. The same query editor is used by device report filters.

<div class="ui-controls-spacer"></div>

**Add match group / Category**

Add a group and choose its category: device properties, an inventory category, device-group membership, directory-group membership or directory OU placement. Conditions within one inventory category must match the same inventory record.

**Match**

Choose All match groups (AND) or Any match group (OR) at the root. Within a condition group, choose All conditions (AND) or Any condition (OR).

**Field / Operator / Value**

Choose a field and an operator supported by that field. Supply text, a whole number, a date/time or a listed boolean/enum value as appropriate. Text comparisons are case-insensitive. Is empty does not require a value. Custom-attribute and inventory-script fields select the stored definition before matching its value.

**Device group / Directory group / Organizational unit**

Membership categories select an existing group or imported directory object instead of ordinary field conditions. Search directory objects using the supplied picker. Replace unavailable objects before saving; a group cannot directly select itself.

**Include descendants**

For a directory OU, include devices below its child OUs as well as its direct contents.

**NOT**

Negates a condition or non-root group. Review the enclosing AND/OR choice when combining negated conditions.

**Add condition / Add condition group / Remove**

Add field conditions or nested groups, or remove a condition/group. Nested groups keep their parent category. The editor limits nesting depth; unavailable add actions explain that limit.

**Preview matches**

Shows up to 100 devices matching the pending query without saving it. Existing group references use current recorded membership; recalculation refreshes dependencies. Editing criteria clears the preview.

**Save / Cancel**

Save persists the dynamic query; Cancel discards pending edits. Recalculate members evaluates the saved query. Preview alone does not replace saved group membership.

## Permissions

Device Group View permits opening the group; Device Group Edit is required to save criteria. Device reports enforce their own reporting permission.
