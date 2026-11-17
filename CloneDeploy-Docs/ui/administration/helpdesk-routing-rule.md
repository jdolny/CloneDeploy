# Help Desk Routing Rule

The same rule dialog is opened from Categorization and Queue Assignment. Rules affect newly created tickets. Each stage uses its first enabled matching rule in ascending order, then rule ID.

<div class="ui-controls-spacer"></div>

**Rule name / Order / Enabled**

Name identifies the rule and accepts up to 100 characters. Order ranges from 0 to 100,000. Enabled determines participation. New rules initially suggest an order after existing rules in that stage.

**Match conditions**

Choose whether all conditions or any condition must match. An empty condition list is a catch-all; place it after specific rules.

**Add condition / Remove**

Add up to 20 conditions, or remove an existing condition. Each condition has its own field, operator and value.

**Field / Operator**

Select ticket text, device name, inventory IP, directory user/device group or OU; queue assignment can also match category. Text supports Contains, Starts with, Equals or Matches regex. IP conditions use In subnet/range; directory conditions use equality.

**Category**

For a category condition in queue assignment, select the category that must match.

**OU (includes child OUs) / Group (includes nested members)**

Select the imported object using the searchable directory picker. Matching uses directory ancestry or effective nested group membership, rather than a typed display-name comparison.

**Subnet or start-end range**

For inventory IP matching, enter a subnet or address range. This matches stored device inventory information.

**Regular expression (case insensitive) / Value (case insensitive)**

Enter the text criterion for the selected operator, up to 500 characters.

**Sample text / Test match**

For regex conditions, enter sample text up to 4,000 characters and test the pattern. This test does not create or reroute a ticket.

**Set category / Assign to queue**

Select the result for the current stage: a category in Categorization, or a queue in Queue Assignment.

**Save rule / Cancel**

Save rule validates and persists the rule. Errors leave the editor open for correction. Cancel discards the edits; neither operation reroutes existing tickets.

## Permissions

Administrator access is required.
