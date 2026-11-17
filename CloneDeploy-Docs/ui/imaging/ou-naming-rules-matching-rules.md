# Matching Rules

Order computer-name matches and their destination OUs. The first matching rule wins, using case-insensitive comparison.

<div class="ui-controls-spacer"></div>

**Add rule / Rule name**

Create and identify a match rule.

**Match type**

Choose wildcard or regular-expression matching. Wildcards match the entire name: * matches any number of characters and ? matches one. Anchor a regex with ^ and $ for whole-name matching.

**Computer name pattern**

Enter the name pattern. Regex evaluation has a 25 ms timeout per rule; a timeout fails resolution instead of selecting a fallback.

**Choose OU**

Select the rule's destination from the imported OU tree.

**Move up / Move down / Remove rule**

Maintain evaluation order or remove a rule.

**Test name**

Evaluates the current form, including unsaved edits and disabled sets. This does not join or move an AD computer.

**Save Changes**

Saves the rule order and matching configuration.

## Permissions

OU Naming Rule View permits testing; OU Naming Rule Edit is required to save changes.
