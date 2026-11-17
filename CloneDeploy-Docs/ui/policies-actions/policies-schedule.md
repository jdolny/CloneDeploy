# Triggers & Timing

Choose when a policy becomes eligible. Trigger, frequency and allowed start window are separate restrictions.

<div class="ui-controls-spacer"></div>

**Run when**

Check-in, Startup, and Startup Or Checkin select machine triggers; Startup Or Checkin accepts either. Login runs through the user’s tray with normal user permissions. Machine policies run as SYSTEM. Self-service-only waits for a portal Run request.

**Run as (self-service-only)**

Chooses SYSTEM or the requesting user. Configure an audience on Self Service.

**Frequency**

Ongoing permits every eligible trigger; Once Per Computer stops repeating after completion unless explicitly rerun. Once Per Day/Week/Month use the selected period; Every X Hours/Days uses elapsed time since completion. Choices are restricted by trigger. Ongoing login runs once per Windows logon; reopening the tray does not retrigger it. Once-per-user-per-computer tracks the Windows identity.

**Day of week / Day of month / Every X hours or days**

Parameters for the selected recurring frequency.

**Start date (UTC)**

Earliest configured start date for automatic eligibility.

**If schedule is missed**

Next Opportunity allows a missed weekly/monthly occurrence at a later eligible trigger. Schedule Day Only waits for the selected schedule day. For monthly days beyond the end of a month, the last day is used.

**Limit when a due policy may start**

Adds a device-local time window; it does not replace the trigger/frequency.

**Every day / weekday selections / From / Until**

Defines window opening days and hours. Until earlier than From means overnight. Once per day is counted by opening day; running work can finish after the window closes.

**Save Triggers & Timing**

Saves timing. Manual self-service runs bypass automatic frequency/schedule and start windows.

## Permissions

Access requires Policy View permission.
