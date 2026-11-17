# Create Your First Policy

Use an inventory policy to verify targeting, scheduling and execution on your enrolled test computers. Begin with a small explicit target list so the first policy cannot unintentionally reach the entire environment.

## 1. Create an inventory policy

1. Open **Policies & Actions → Policies** and select **New Policy**.
2. Enter **Collect Inventory** as the name and a description such as **Collect inventory weekly from test devices**.
3. Create the policy and open its **Actions** tab.
4. Enable the built-in **Run inventory** function and save the built-in functions. No separate software or command action is needed for this policy.
5. Leave **Remote access** set to **Not Configured**. The other values in that policy control are not supported by the current agent.

## 2. Set the trigger and frequency

Open **Triggers & Timing**.

1. Set **Run when** to **Check-in**.
2. Select **Once Per Week** and the desired day.
3. Select **Next Opportunity** for a missed schedule so a device can run the due policy at a later eligible check-in.
4. Save **Triggers & Timing**.

A trigger determines when the agent evaluates the policy. Frequency and schedule determine whether it is due. Publishing the policy does not itself run it immediately.

## 3. Target the test computers

1. Open **Targets** and select **Add devices**.
2. Add only the enrolled test computers.
3. Use **Preview devices** to verify the resolved target list.
4. Select **Save Targets**.

You can use groups or imported organizational units for broader deployment later. Overlapping assignments do not cause a device to be targeted twice.

## 4. Keep results visible

On **Execution**, retain result reporting while testing. Leave **Skip sending results to server** disabled and use a log level that provides the detail needed to verify the pilot. Save any changes.

## 5. Activate and verify

1. Select **Activate**, review the publication and confirm it.
2. Allow the test device to check in, or request a check-in from its device controls. The policy runs when its trigger and schedule make it eligible.
3. Review the policy's **History → Client results** and the device's policy history.
4. Confirm that inventory appears or updates on the device's **Inventory** tabs.

If the policy has not run, check its active state, saved targets, trigger, due date and the device's connectivity before changing the scope.

## 6. Expand after the pilot

Once the results are correct, assign the policy to an appropriate device group or OU and preview the affected devices before saving. Keep schedules appropriate for your environment.

For software or configuration work, create the appropriate action first, add it to a policy's **Actions** sequence, and test against the same pilot devices. Save each edited tab and activate the policy to publish its saved configuration. Deactivate an active policy before editing its execution configuration; target assignments remain editable while active.
