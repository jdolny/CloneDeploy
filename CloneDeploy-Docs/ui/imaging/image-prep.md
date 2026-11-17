# Image preparation profiles

Run preparation from Administrator tools in the agent UI on the source computer. Successful preparation shuts it down, or restarts into WinPE to capture automatically when **Reboot and capture image** is enabled. Choose the destination image at launch; each successful capture creates a new version.

With an updated agent and server, automatic capture reads architecture and manufacturer/model directly from the source computer; a prior inventory run is not required. WinPE selection uses model rules or the compatible ready default. Missing model information uses the default. Older agents still require stored architecture inventory or an agent update.

Create and locate reusable preparation definitions.

<div class="ui-controls-spacer"></div>

**New Image Prep Profile**

Opens the name and description dialog. Create saves the profile before opening its configuration.

**Search / Include disabled**

Finds profiles for use or maintenance.

**Delete profile**

Requests removal after confirmation.

**Save Profile**

Saves all edited tabs without running preparation.


**Name / Description (creation dialog)**

Enter a required name for the image preparation profile, up to 200 characters, and an optional description up to 2,000 characters.

**Create / Cancel**

Create saves the image preparation profile and opens its configuration. It does not start a build, preparation or deployment. Cancel closes without creating it. Creation requires Image Prep Profile Edit.

## Permissions

Access requires Image Prep Profile View permission.
