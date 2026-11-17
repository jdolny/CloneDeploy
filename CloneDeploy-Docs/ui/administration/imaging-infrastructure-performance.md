# Deployment Performance

These settings apply to new deployment sessions. Capture/upload verification is unchanged.

<div class="ui-controls-spacer"></div>

**Verify deployment data**

Checks FFU before apply and WIM while streaming. Turning it off skips deployment hashes and optional WIM integrity checks, but required format, size and disk checks remain.

**Block size (MiB)**

Choose 4, 8 or 16; default 8.

**Parallel reads**

One disables read-ahead; up to four is supported. More reads increase server load.

**Cache per device (MiB)**

Must be a multiple of block size and at least twice block size multiplied by parallel reads. Default 64 MiB.

**Save / Refresh**

Save values for subsequent sessions or reload the saved configuration.

## Permissions

Administrator access is required.
