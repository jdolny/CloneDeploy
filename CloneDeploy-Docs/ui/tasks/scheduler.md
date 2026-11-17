# Task Scheduler

Control recurring server maintenance and import jobs. These are server tasks, separate from an agent policy’s trigger and timing.

<div class="ui-controls-spacer"></div>

**Tasks**

Review last/next-run and result information for each task before changing its schedule.

**Run Now**

Requests one execution of that server task using saved settings.

**Automatic scheduling**

Enables or disables recurring execution of the task.

**Schedule**

Selects a shared schedule for recurring runs.

**New schedule**

Creates a reusable schedule; interval schedules have a five-minute minimum.

**Save**

Saves the task’s enabled state and schedule. A saved schedule does not itself run the task immediately.

**Email digest delivery** checks user subscriptions through a five-minute interval schedule. See Application user Options — scheduling and delivery status.

**Check device group power schedules** checks wake-up and shutdown assignments every five minutes by default. Configure the actual action times on each group's **Power schedules** tab. **Run Now** evaluates due assignments without forcing future ones. See Device groups.


**Task dialog / Run now / Close**

Opening a task shows its schedule settings and recent run results. Run now queues a run using saved settings, without saving pending scheduling edits, and is unavailable while a run is queued or running. Save Changes persists Automatic scheduling and Schedule; enabling scheduling requires a selected schedule. Close discards unsaved dialog edits and does not cancel an already queued run. Creating a New schedule is a separate saved operation.

## Permissions

Administrator access is required.
