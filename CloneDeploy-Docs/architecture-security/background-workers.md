# Background workers

CloneDeploy performs background work in the Main App, endpoint agents, and optional services. This page explains what runs, what starts it, and how often it checks for work. Implementation names are included so administrators can match an entry to its log messages.

The intervals below describe the current implementation. Unless a setting is named, the interval is built into the application rather than an administrator-editable schedule.

Jump to [Main App workers](#main-app-startup-and-health), [scheduled jobs](#main-app-scheduled-jobs), [separate services](#separate-services), [endpoint agents](#endpoint-agents), or [troubleshooting](#where-to-look-when-work-appears-stuck).

## Reading the timings

- **Polling interval** is how often a worker looks for work. It is not a guarantee that a queued operation starts or finishes within that time.
- **Idle delay** applies when a queue is empty or a cycle fails. A busy queue may process the next item immediately.
- **After each cycle** means the time spent doing work is added to the delay. Periodic timers also do not start overlapping copies of a worker's own loop when a cycle runs long.
- **Event-driven** work waits for a request, message, or database notification. It does not repeatedly scan for work on a fixed schedule.
- **Startup-only** work runs once per process start. Restarting the application runs it again.

Queue depth, network access, storage speed, configured concurrency limits, and retry delays can all affect completion time. Separate workers can operate concurrently; the scheduler's serial queue does not serialize every background operation in the application.

## Main App: startup and health

These workers run in the main server process.

| Worker | What it does | When it runs |
| --- | --- | --- |
| Server initialization (`ServerStartup`) | Checks database migration compatibility, applies migrations, initializes support state, loads the service HTTPS certificate and agent CA trust, and creates missing scheduler task definitions. | Once during startup, before startup completes. A failure can prevent the server from starting. |
| Agent release preparation (`AgentReleaseStartup`) | Attempts to prepare the bundled Windows x64, Windows ARM64, and macOS agent releases when necessary. Skips current/newer prepared releases and logs configuration or preparation failures. | Once after the application starts. Use **Prepare agent release** to retry after fixing a failure; there is no periodic retry loop. |
| Enrollment rate settings (`AgentEnrollmentRateSettings`) | Refreshes the cached enrollment request limit, avoiding a database query for every anonymous enrollment request. Retains the last limit if refresh fails. | Immediately, then every **10 seconds**. |
| Agent connection cleanup (`AgentConnectionCleanup`) | Removes stale live-connection registrations. Also removes registrations owned by this process during orderly shutdown. | Every **30 seconds**. Presence has a **90-second** freshness window; cleanup frequency and the online/offline threshold are different. |
| Storage connection checks (`StorageMountWorker`) | Checks enabled storage providers and reconciles their mounted/accessible state. A failed provider can be reported without preventing the Web UI from starting. | Immediately, then **300 seconds after each cycle** by default. Configurable through `StorageMounts:CheckIntervalSeconds`; administrators can also request a check from Storage settings. |
| .NET runtime update checks (`RuntimeUpdateWorker`) | Checks whether supported .NET / ASP.NET Core runtime updates are available. This reports availability; it does not install an update. | Immediately, then **24 hours after each cycle**, including failed checks. Successful lookup results are cached for **24 hours**; lookup errors for **15 minutes** for other callers. |
| Professional support refresh (`ProfessionalSupportWorker`) | Refreshes professional-support verification/status when applicable. | After application startup, then every **24 hours**. |

## Main App: deployment, download, and replication queues

| Worker | What it does | When it checks |
| --- | --- | --- |
| Device task maintenance (`DeviceTaskRunner`) | Maintains deployment queue state and notifies connected agents when work is available. Execution remains subject to task admission and concurrency limits. | Immediately, then every **10 seconds**. Repeat wake-up notifications for waiting work are limited to approximately **30 seconds per device**. |
| External file downloads (`ExternalDownloadWorker`) | Downloads queued URL-backed files, calculates hashes, publishes completed files, and recovers interrupted download/hash work on startup. | **15-second idle/error delay**; processes the next queued item immediately after work finishes. Also checks for abandoned temporary downloads at startup and at least **5 minutes** apart between jobs; the temporary-file age cutoff is **1 hour**. |
| Driver preparation (`DriverPreparationWorker`) | Extracts and prepares queued driver packages for use in imaging; returns interrupted extraction jobs to the queue after startup. | **15-second idle/error delay**; continues immediately when another item can be processed. Checks for expired, unreferenced preparation artifacts at startup and roughly **hourly** between jobs, retaining referenced artifacts. |
| Shared content replication (`SharedReplicationWorker`) | Reconciles required shared-content replicas and copies eligible content to storage providers handled by the main server. Honors replication settings and per-item retry times. | Immediately, then every **15 seconds**. A scan does not recopy already-ready content. |
| Storage moves (`StorageMoveWorker`) | Processes requested storage moves and associated source cleanup. This is separate from choosing a default write destination or maintaining replicas. Failed move jobs retain their error for review. | Immediately, then a **10-second** periodic timer. A long move can occupy a pass; this does not start concurrent copies of the same worker loop. |
| Image replication (`ImageReplicationWorker`) | Reconciles image replicas and processes eligible local-provider copies. Site-managed destinations are handled by the Site Service. | Immediately, then every **15 seconds**, subject to per-item retry times and storage availability. |
| WinPE transfer and activation (`WinPeTransferWorker`) | Moves completed local WinPE builds to configured storage and attempts pending boot-environment activation. | Immediately, then **15 seconds after each cycle**. The build itself is performed by a builder service or Windows Worker. |
| Windows build dispatch (`WindowsWinPeBridge`) | Maintains the remote Windows work queue, dispatches WinPE/setup work to the assigned Windows Worker, and brings returned artifacts into the local build spool. | Immediately, then **15 seconds after each cycle**. A dispatched operation can keep a cycle occupied; its monitor checks every **2 seconds**. |
| Main-server multicast sender (`MulticastSenderWorker`) | Claims and runs multicast image sender jobs assigned to the main server. | Every **2 seconds**, with up to **8** sender jobs running concurrently. |
| Database backup (`DatabaseBackupService`) | Runs requested PostgreSQL backups outside the browser request and reports status/results. | **Event-driven**, when a backup request enters its channel. It does not automatically take a backup every few minutes. |

The download and preparation queues are separate from scheduled refresh checks. A schedule can decide that a file needs checking and enqueue it; the download worker then performs the transfer when it can claim that work.

## Main App: cleanup

| Worker | What it removes | When it checks |
| --- | --- | --- |
| Chunked upload cleanup (`ChunkedUploadStore`) | Inactive upload sessions, expired completion bookkeeping, and abandoned partial upload files. | Every **5 minutes**, using a **1-hour inactivity cutoff**. This does not delete published action files simply because they are old. |
| Pending enrollment cleanup (`AgentEnrollmentRequestCleanup`) | Deletes inactive requests waiting for initial approval, a device record, or reset approval. Denied, approved and completed enrollment records are excluded; device certificates are not revoked by this cleanup. | Immediately, then **1 hour after each cycle**. Retention is **30 days initially**, configurable from **1–3,650 days**, measured from the request's last contact time. |
| Imaging upload cleanup (`ImageUploadCleanupWorker`) | Expired, incomplete image-upload sessions and their associated temporary work. | Immediately, then every **15 minutes**. New capture upload sessions receive a **24-hour** expiry; cleanup can also be requested by marking work expired. |
| Image content deletion (`ImageContentCleanupWorker`) | Processes explicit image-content deletion records after the associated image version/upload is no longer registered. Failed deletions remain available for retry. | Immediately, then every **30 seconds**, considering up to **100 queued records** per pass. It is not an age-based purge of valid image versions. |

## Main App: network boot and live notifications

| Worker | What it does | When it checks or reacts |
| --- | --- | --- |
| ProxyDHCP/PXE (`PxeWorker`) | Applies PXE settings, supervises UDP listeners, and answers boot requests when enabled. | Settings and listener health: immediately, then **5 seconds after each cycle**. Boot packets are handled as they arrive, not only once every five seconds. |
| TFTP (`TftpWorker`) | Applies TFTP settings and supervises the read-only boot-file server. | Settings and listener health: immediately, then **5 seconds after each cycle**. Transfers are request-driven. |
| Native PXE reservations (`NativePxeReservationWorker`) | Updates the cached mapping from devices/models to published native WinPE boot environments. Keeps database work off the UDP receive path. | Drains change notifications every **2 seconds**. Performs a full reconciliation initially and approximately every **10 minutes**, or sooner when requested/needed after an error. Unchanged polls do not reload all devices. |
| Device activity notifications (`DeviceActivityNotifications`) | Listens for PostgreSQL device-activity changes and refreshes live device activity feeds. | **Event-driven** through PostgreSQL `LISTEN`. Batches related changes for **100 milliseconds**; reconnects/retries after **5 seconds** on failure and refreshes after reconnect. |
| Help Desk live notifications (`HelpUpdateNotifications`) | Listens for ticket changes and updates Help Desk presence/notification consumers. | **Event-driven** through PostgreSQL `LISTEN`, with the same **100-millisecond** batching and **5-second** reconnect/retry delay. |
| Help Desk email (`HelpEmailWorker`) | Processes due ticket-email notifications and retries eligible failed deliveries. This is separate from daily/weekly digest email. | Immediately, then **15 seconds after each cycle**. Ordinary requester updates wait for **2 minutes of quiet**, capped at **10 minutes** from the first pending update and subject to a **5-minute** post-send cooldown. Definite-failure retries wait **5 minutes**. Ambiguous SMTP acceptance is not blindly retried. |

The live database listeners keep connections open while waiting. An idle listener is not equivalent to a tight database polling loop.

## Main App: scheduled jobs

`SchedulerHostedService` has two loops: it checks for due schedules immediately and then **once per minute**; its queue processor runs jobs **one at a time**, checking every **5 seconds when idle** and moving straight to the next job when work remains.

The scheduler tick, a task's assigned schedule, and the operation's own schedule can be different. For example, the external-file scheduler checks whether individual action/driver schedules are due; it does not redownload every file on each tick.

| Scheduled task | Purpose | Initial scheduling behavior |
| --- | --- | --- |
| Sync Directories (`DirectorySyncTaskHandler`) | Synchronizes enabled Active Directory and Entra directory connections. | Administrator-assigned schedule or manual run. |
| Recalculate Dynamic Groups (`DynamicGroupTaskHandler`) | Re-evaluates membership of saved dynamic device groups. | Administrator-assigned schedule or manual run. |
| Check device group power schedules (`GroupPowerScheduleTaskHandler`) | Evaluates assigned wake-up and shutdown schedules. | Newly created task is enabled with a **5-minute** check schedule. Actual operations follow the group's assigned schedule; offline shutdowns and occurrences over ten minutes late are skipped. |
| Check external file schedules (`ScheduledExternalFilesHandler`) | Checks action-file and driver-profile refresh schedules and queues eligible work. | Newly created task is enabled with a **5-minute** check schedule. Individual file/profile schedules still determine what is due. |
| Import WinGet manifests (`WinGetManifestImportTaskHandler`) | Refreshes the WinGet package catalog from Microsoft's manifest repository. | Administrator-assigned schedule or manual run. |
| Windows Update auto-approval (`WindowsUpdateAutoApprovalTaskHandler`) | Approves inventoried update revisions matching enabled rules. It does not install updates. | Newly created task is enabled with a **5-minute** schedule. |
| Email digest delivery (`EmailDigestTaskHandler`) | Checks opted-in daily/weekly email subscriptions and sends due reports in each user's timezone. | Newly created task is enabled with a **5-minute** check schedule. Each subscriber's delivery time and selected categories determine when/what is sent. |

Startup seeds these defaults only for missing task definitions. It preserves existing administrator scheduling choices. Review schedules, enabled state, queued runs, and execution history under Scheduler. A long-running scheduled job can delay other scheduled jobs; it does not stop the independent workers above.

## Separate services

These loops run only where the corresponding service is installed. Site Service roles must also be enabled and have an accepted configuration.

| Process and worker | Purpose | Timing |
| --- | --- | --- |
| Site Service — management (`ManagedSiteService`) | Enrolls, reports status, receives configuration/certificate updates, and applies proxy/boot/storage/multicast configuration. | **10 seconds** between enrollment attempts; **60 seconds** between configured status/update cycles. Reports newly accepted configuration immediately after enrollment. |
| Site Service — boot (`SiteBootWorker`) | Synchronizes boot content and native reservations, applies PXE/TFTP configuration, and supervises local boot listeners. | **5 seconds after each cycle**. Synchronization can take longer; listener changes wait for active TFTP transfers where required. Network requests are handled continuously while listeners are active. |
| Site Service — storage (`SiteStorageWorker`) | Synchronizes assigned storage manifests/content into its local cache and reports replication errors. | **15 seconds after each cycle**. Content transfers can extend a cycle. |
| Site Service — multicast (`SiteMulticastWorker`) | Claims site multicast work and sends the selected cached content. | **2-second** claim checks, with up to **8** senders. During content preparation, status/retry checks use **5 seconds**. |
| Windows Worker — management (`ManagedWindowsWorker`) | Enrolls and receives its role/configuration while reporting status. It does not create an inbound web listener. | **10 seconds** while enrolling; **60 seconds** between configured cycles. |
| Windows Worker — jobs (`WindowsWorkWorker`) | Claims Windows-only jobs and runs the configured work engine, including WinPE work. | **5 seconds after each cycle**; running jobs report progress approximately every **10 seconds**. |
| Local WinPE builder (`BuilderWorker`) | Validates and recovers its workspace at startup, processes prerequisite setup and image-build requests, and publishes health. | **3 seconds after each queue pass**; operation monitors also use **3 seconds**. Jobs run serially. Local execution pauses when the main server selects a remote Windows Worker. |

## Endpoint agents

Agent timing is separate from server queue maintenance. An agent's live connection can wake work before its next scheduled check-in.

| Agent work | Purpose | Timing |
| --- | --- | --- |
| Check-in and policy cycle — Windows `Worker`, macOS daemon main loop | Enrolls/checks in, receives update offers, and evaluates eligible startup/check-in policies. | Server-supplied interval: **60 minutes by default**, configurable from **1–1,440 minutes** in Agent administration. **Check In Now** can wake the wait. Windows connection failures normally retry after **60–74 seconds**; macOS after **60 seconds** plus up to **3 seconds** of wait jitter. Server-directed pending/rate-limit responses can specify another delay. |
| Live connection — `AgentRealtimeClient` / `MacRealtimeClient` | Receives immediate requests and reports presence. | Heartbeats approximately every **15 seconds** while connected; connection recovery has its own retry behavior. A heartbeat is not a full inventory collection or policy run. |
| Direct task worker — `AgentTaskWorker` / `MacTaskWorker` | Processes standalone deployment work when signaled by check-in or a live request. | **Event-driven**. Contention/retry responses use randomized **15–45-second** retries instead of waiting for the next normal check-in. |
| Login tracking — `UserLoginTracker` / `MacLoginTracker` | Samples logged-in sessions and keeps durable login history for reporting. | Approximately every **10 seconds**, with additional sampling during check-in. |
| Activity reporting — `AgentActivityWorker` / macOS `RunActivityAsync` | Sends changed execution/activity state over the live connection. | **Event-driven**, as local activity changes. |
| Portal notification receipts — `PortalNotificationReporter` / `MacNotificationReporter` | Uploads pending notification receipts and applies acknowledgments received from the server. Offline receipts remain queued. | Immediately, then **30 seconds after each cycle**; sends only when there is reportable work. |
| Windows login policies (`LoginPolicyService`) | Accepts authenticated local login-policy requests and runs eligible user work. | **Event-driven** through the local pipe; listener failures retry after **5 seconds**. |
| macOS work-account policies (`MacWorkIdentity.RunPoliciesAsync`) | Detects the active console user's authenticated work session and starts eligible user policies when that identity changes. | **15 seconds after each cycle**. It does not rerun all policies every 15 seconds for an unchanged session. |
| Windows agent imaging (`AgentImagingBoot`) | Handles a requested imaging boot, prepares the environment, and follows the admitted deployment plan. | **Event-driven**; an active plan is checked every **5 seconds**. This is not a continuous fleet-wide imaging scan. |
| Local portal services — Windows `AgentPortalService`; macOS portal/status/authentication and remote IPC loops | Accept requests from the local portal or remote-desktop helper through protected local IPC. | **Request-driven**, not periodic server polling. Individual operations have their own timeouts and short-lived caches. |

Policies, software installers, inventory collection, and updates can take longer than a check interval. Their admission, trigger, schedule, and version checks still apply; a background loop is not permission to execute every action on each tick.

## Work that does not have a dedicated periodic worker

| Operation | What actually triggers it |
| --- | --- |
| Agent CA trust (`AgentTrustRefresh`) | Loaded once during server startup. Creating the initial CA through the UI updates in-memory trust immediately after the database transaction commits. There is no periodic refresh. Manual CA changes require a restart; registered certificate, issuer revocation, expiry, and device status are still checked during authentication. |
| Main-server HTTPS certificate renewal | An administrator issues/renews it and restarts the server. CA trust loading does not renew or reload the HTTPS listener certificate. |
| Agent identity certificate renewal/key rotation | Authenticated check-in when 30 days or less remain. An expired identity cannot use automatic renewal. |
| Server message-signing certificate renewal | A signing request when the current short-lived certificate needs replacement. |
| Site Service / Windows Worker certificate renewal | Authenticated service status/configuration exchange when renewal is due. |
| Certificate-expiration notices | Dashboard data loading and subscribed administrator Security digest generation. These inspect certificate state; they do not renew certificates. |
| CloneDeploy application update lookup (`ServerUpdateChecker`) | A requesting dashboard/digest operation. Successful results are cached for **24 hours**, errors for **15 minutes**; forced checks still have a **1-minute** cache window. There is no separate application-update installer worker. |
| Terminal, remote-desktop, and content-transfer sessions | An authorized request starts the session. Per-session heartbeat, timeout, and lease-renewal tasks live only as long as the operation requires. |

## Where to look when work appears stuck

Start with the relevant queue or status page: Scheduler, Tasks, Storage, WinPE, or Site Services. Scheduled-task history covers scheduler jobs; it is not a history of every hosted worker.

For independent workers, inspect the owning process's logs using the implementation name shown above. Check whether the role is enabled, whether the database/server is reachable, and whether storage or a required builder is available. A worker can be running normally while there is no eligible work, or while a job is waiting for a retry time, approval, connection, or concurrency slot.

Changes to `server.json` generally require the owning service to restart. Database-backed schedules and settings may be read on a later cycle. Restarting does not guarantee that an interrupted action is safe to repeat; review its recorded state and error before manually requeueing it.

<!-- Maintenance: audited against Main App Program.cs, AgentApiSetup.cs and ImagingApiSetup.cs registrations;
individual ExecuteAsync loops; SchedulerAdministration.InitializeScheduledTasksAsync; Site Service,
Windows Worker, WinPE builder and Windows/macOS agent entry points. Update cadence and trigger together
when a worker changes; keep polling periods distinct from cache lifetimes and job retry/expiry times. -->
