# Model matching

Rules select a ready boot environment from reported manufacturer/model. Multiple matching boot environments conflict; no match uses the ready default.

A ready default configured for Native PXE also serves first-boot devices without inventory. Model rules are not required. BIOS/x64 clients use the standard default; ARM64 clients require a ready ARM64 default. EFI IA32 retains its configured boot file. Known devices with an iPXE selection, conflicting rules, or ambiguous MAC ownership do not fall back to another native image. Main-server and updated Site Service boot listeners use the same selection.

<div class="ui-controls-spacer"></div>

**Use as default**

Replaces the previous default when saved. A build must complete storage verification and boot preparation before selection.

**Add model from inventory / Exact manufacturer and model**

Adds an exact rule.

**Manufacturer regex / Model regex**

Case-insensitive after trimming; filled fields in a rule must both match. Rules are alternatives.

**Add rule / Remove rule**

Changes pending rules.

**Save model matching / Save changes**

Commits matching without rebuilding.

**Test manufacturer / Test model / Test match**

Tests saved rules across ready boot environments; save first.

## Permissions

Administrator access is required.
