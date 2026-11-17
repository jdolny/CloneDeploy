using CloneDeploy_Agent.Contracts;
using CloneDeploy_Server.Backend.Data.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;

namespace CloneDeploy_Server.Backend.Data;

public class CloneDeployDbContext(DbContextOptions<CloneDeployDbContext> options)
    : IdentityDbContext<AppUser, IdentityRole<int>, int>(options)
{
    public DbSet<ImageUpload> ImageUploads => Set<ImageUpload>();
    public DbSet<WindowsUpdateAutoApprovalRule> WindowsUpdateAutoApprovalRules => Set<WindowsUpdateAutoApprovalRule>();
    public DbSet<WinPeDefinition> WinPeDefinitions => Set<WinPeDefinition>();
    public DbSet<WinPeBuild> WinPeBuilds => Set<WinPeBuild>();
    public DbSet<WinPeToolchain> WinPeToolchains => Set<WinPeToolchain>();
    public DbSet<WinPeSetupJob> WinPeSetupJobs => Set<WinPeSetupJob>();
    public DbSet<DeviceCheckinLease> DeviceCheckinLeases => Set<DeviceCheckinLease>();
    public DbSet<ActionDeployment> ActionDeployments => Set<ActionDeployment>();
    public DbSet<DeviceTask> DeviceTasks => Set<DeviceTask>();
    public DbSet<ScheduledTask> SystemTasks => Set<ScheduledTask>();
    public DbSet<ScheduledTaskExecution> SystemTaskRuns => Set<ScheduledTaskExecution>();
    public DbSet<EntraConnection> EntraConnections => Set<EntraConnection>();
    public DbSet<DirectoryObject> DirectoryObjects => Set<DirectoryObject>();
    public DbSet<DirectoryGroupMembership> DirectoryGroupMemberships => Set<DirectoryGroupMembership>();
    public DbSet<DirectoryEffectiveGroupMembership> DirectoryEffectiveGroupMemberships => Set<DirectoryEffectiveGroupMembership>();
    public DbSet<DirectorySource> DirectorySources => Set<DirectorySource>();
    public DbSet<LdapDirectorySettings> LdapDirectorySettings => Set<LdapDirectorySettings>();
    public DbSet<DirectorySyncState> DirectorySyncStates => Set<DirectorySyncState>();
    public DbSet<DirectoryOuPolicy> DirectoryOuPolicies => Set<DirectoryOuPolicy>();
    public DbSet<ActionTag> ActionTags => Set<ActionTag>();
    public DbSet<ActiveCompiledPolicy> ActiveCompiledPolicies => Set<ActiveCompiledPolicy>();
    public DbSet<ActiveImagingTask> ActiveImagingTasks => Set<ActiveImagingTask>();
    public DbSet<AppCertificate> AppCertificates => Set<AppCertificate>();
    public DbSet<SiteServiceRegistration> SiteServices => Set<SiteServiceRegistration>();
    public DbSet<AgentCertificate> AgentCertificates => Set<AgentCertificate>();
    public DbSet<AgentProvisioningRequest> AgentProvisioningRequests => Set<AgentProvisioningRequest>();
    public DbSet<Attachment> Attachments => Set<Attachment>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<HelpTicket> HelpTickets => Set<HelpTicket>();
    public DbSet<HelpTicketMessage> HelpTicketMessages => Set<HelpTicketMessage>();
    public DbSet<Certificate> Certificates => Set<Certificate>();
    public DbSet<CommandAction> CommandActions => Set<CommandAction>();
    public DbSet<Comment> Comments => Set<Comment>();
    public DbSet<CustomAttribute> CustomAttributes => Set<CustomAttribute>();
    public DbSet<Device> Devices => Set<Device>();
    public DbSet<ActionReference> ActionReferences => Set<ActionReference>();
    public DbSet<DeviceAntiVirus> DeviceAntiVirus => Set<DeviceAntiVirus>();
    public DbSet<DeviceAttachment> DeviceAttachments => Set<DeviceAttachment>();
    public DbSet<DeviceBios> DeviceBios => Set<DeviceBios>();
    public DbSet<DeviceBitLocker> DeviceBitLocker => Set<DeviceBitLocker>();
    public DbSet<DeviceCertificate> DeviceCertificates => Set<DeviceCertificate>();
    public DbSet<DeviceComment> DeviceComments => Set<DeviceComment>();
    public DbSet<DeviceComputerSystem> DeviceComputerSystems => Set<DeviceComputerSystem>();
    public DbSet<DeviceCustomAttribute> DeviceCustomAttributes => Set<DeviceCustomAttribute>();
    public DbSet<DeviceCustomInventory> DeviceCustomInventory => Set<DeviceCustomInventory>();
    public DbSet<DeviceFirewall> DeviceFirewalls => Set<DeviceFirewall>();
    public DbSet<DeviceGpu> DeviceGpus => Set<DeviceGpu>();
    public DbSet<DeviceGroup> DeviceGroups => Set<DeviceGroup>();
    public DbSet<DeviceGroupPowerSchedule> DeviceGroupPowerSchedules => Set<DeviceGroupPowerSchedule>();
    public DbSet<DeviceGroupMembership> DeviceGroupMemberships => Set<DeviceGroupMembership>();
    public DbSet<DeviceHardDrive> DeviceHardDrives => Set<DeviceHardDrive>();
    public DbSet<DeviceImagingLog> DeviceImagingLogs => Set<DeviceImagingLog>();
    public DbSet<DeviceLogicalVolume> DeviceLogicalVolumes => Set<DeviceLogicalVolume>();
    public DbSet<DeviceLogin> DeviceLogins => Set<DeviceLogin>();
    public DbSet<DeviceNic> DeviceNics => Set<DeviceNic>();
    public DbSet<DeviceOs> DeviceOs => Set<DeviceOs>();
    public DbSet<DevicePrinter> DevicePrinters => Set<DevicePrinter>();
    public DbSet<DeviceProcessor> DeviceProcessors => Set<DeviceProcessor>();
    public DbSet<DeviceSoftware> DeviceSoftware => Set<DeviceSoftware>();
    public DbSet<DeviceTag> DeviceTags => Set<DeviceTag>();
    public DbSet<DeviceWindowsUpdate> DeviceWindowsUpdates => Set<DeviceWindowsUpdate>();
    public DbSet<StoredFile> StoredFiles => Set<StoredFile>();
    public DbSet<FileAction> FileActions => Set<FileAction>();
    public DbSet<DevicePolicy> DevicePolicies => Set<DevicePolicy>();
    public DbSet<GroupPolicy> GroupPolicies => Set<GroupPolicy>();
    public DbSet<DeviceGroupTag> DeviceGroupTags => Set<DeviceGroupTag>();
    public DbSet<DriverPack> DriverProfiles => Set<DriverPack>();
    public DbSet<DriverFile> DriverFiles => Set<DriverFile>();
    public DbSet<DriverModelLink> DriverModelLinks => Set<DriverModelLink>();
    public DbSet<DeploymentProfile> ImageProfiles => Set<DeploymentProfile>();
    public DbSet<ImagePrepProfile> ImagePrepProfiles => Set<ImagePrepProfile>();
    public DbSet<OuNamingRuleSet> OuNamingRuleSets => Set<OuNamingRuleSet>();
    public DbSet<Image> Images => Set<Image>();
    public DbSet<ImageVersion> ImageVersions => Set<ImageVersion>();
    public DbSet<DeploymentDriverRule> DeploymentDriverRules => Set<DeploymentDriverRule>();
    public DbSet<DeploymentPolicy> DeploymentPolicies => Set<DeploymentPolicy>();
    public DbSet<ImageTag> ImageTags => Set<ImageTag>();
    public DbSet<ManagedCredential> ManagedCredentials => Set<ManagedCredential>();
    public DbSet<DeviceNotificationReceipt> DeviceNotificationReceipts => Set<DeviceNotificationReceipt>();
    public DbSet<NotificationAction> NotificationActions => Set<NotificationAction>();
    public DbSet<Policy> Policies => Set<Policy>();
    public DbSet<PolicyAction> PolicyActions => Set<PolicyAction>();
    public DbSet<PolicyHashHistory> PolicyHashHistories => Set<PolicyHashHistory>();
    public DbSet<PolicyHistory> PolicyHistories => Set<PolicyHistory>();
    public DbSet<PolicyTag> PolicyTags => Set<PolicyTag>();
    public DbSet<WindowsUpdateAction> WindowsUpdateActions { get; set; }
    public DbSet<DomainJoinAction> DomainJoinActions => Set<DomainJoinAction>();
    public DbSet<PrinterAction> PrinterActions => Set<PrinterAction>();
    public DbSet<Schedule> Schedules => Set<Schedule>();
    public DbSet<ScriptAction> ScriptActions => Set<ScriptAction>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();
    public DbSet<SocketConnection> SocketConnections => Set<SocketConnection>();
    public DbSet<SoftwareAction> SoftwareActions => Set<SoftwareAction>();
    public DbSet<Software> Software => Set<Software>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<ActionFile> ActionFiles => Set<ActionFile>();
    public DbSet<AppUser> AppUsers => Set<AppUser>();
    public DbSet<AppUserGroup> AppUserGroups => Set<AppUserGroup>();
    public DbSet<AppUserGroupMembership> AppUserGroupMemberships => Set<AppUserGroupMembership>();
    public DbSet<AppUserGroupRight> AppUserGroupRights => Set<AppUserGroupRight>();
    public DbSet<AppUserOptions> AppUserOptions => Set<AppUserOptions>();
    public DbSet<AppUserGridColumn> AppUserGridColumns => Set<AppUserGridColumn>();
    public DbSet<AppUserRight> AppUserRights => Set<AppUserRight>();
    public DbSet<WindowsUpdate> WindowsUpdates => Set<WindowsUpdate>();
    public DbSet<WinGetInstallerManifest> WinGetInstallerManifests => Set<WinGetInstallerManifest>();
    public DbSet<WinGetLocaleManifest> WinGetLocaleManifests => Set<WinGetLocaleManifest>();
    public DbSet<WinGetManifestDownload> WinGetManifestDownloads => Set<WinGetManifestDownload>();
    public DbSet<WinGetVersionManifest> WinGetVersionManifests => Set<WinGetVersionManifest>();
    public DbSet<StorageProvider> StorageProviders => Set<StorageProvider>();
    public DbSet<ScheduledTask> ScheduledTasks => Set<ScheduledTask>();
    public DbSet<ScheduledTaskExecution> ScheduledTaskExecutions => Set<ScheduledTaskExecution>();
   
    private void AddActionReferences()
    {
        // Save the shared principal with new actions, including imports and seed data.
        foreach (var entry in ChangeTracker.Entries().Where(x => x.State == EntityState.Added).ToArray())
        {
            ActionType? type = entry.Entity switch
            {
                SoftwareAction => ActionType.Software,
                CommandAction => ActionType.Command,
                FileAction => ActionType.File,
                NotificationAction => ActionType.Notification,
                WindowsUpdateAction => ActionType.WindowsUpdate,
                DomainJoinAction => ActionType.DomainJoin,
                PrinterAction => ActionType.Printer,
                ScriptAction => ActionType.Script,
                _ => null
            };
            if (type is null || entry.Reference("ActionReference").CurrentValue is not null) continue;
            var guid = (Guid)entry.Property("Guid").CurrentValue!;
            var reference = ActionReferences.Local.FirstOrDefault(x => x.ActionGuid == guid);
            if (reference is null)
            {
                reference = new ActionReference { ActionGuid = guid, ActionType = type.Value };
                ActionReferences.Add(reference);
            }
            entry.Reference("ActionReference").CurrentValue = reference;
        }
    }

    private Guid[] NewActionGuids() => ChangeTracker.Entries()
        .Where(x => x.State == EntityState.Added && x.Metadata.FindNavigation("ActionReference") is not null
            && x.Metadata.FindProperty("Guid") is not null)
        .Select(x => (Guid)x.Property("Guid").CurrentValue!).Distinct().ToArray();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        if (ArchivedPolicyReferences() is { } archivedReferences && archivedReferences.Any())
            throw new InvalidOperationException("Archived actions cannot be assigned to policies or used as conditions.");
        var guids = NewActionGuids();
        if (guids.Length > 0) ActionReferences.Where(x => guids.Contains(x.ActionGuid)).Load();
        AddActionReferences();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        if (ArchivedPolicyReferences() is { } archivedReferences && await archivedReferences.AnyAsync(cancellationToken))
            throw new InvalidOperationException("Archived actions cannot be assigned to policies or used as conditions.");
        var guids = NewActionGuids();
        if (guids.Length > 0)
            await ActionReferences.Where(x => guids.Contains(x.ActionGuid)).LoadAsync(cancellationToken);
        AddActionReferences();
        return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private IQueryable<ActionReference>? ArchivedPolicyReferences()
    {
        var assignments = ChangeTracker.Entries<PolicyAction>()
            .Where(x => x.State is EntityState.Added or EntityState.Modified).Select(x => x.Entity).ToArray();
        var policies = ChangeTracker.Entries<Policy>()
            .Where(x => x.State is EntityState.Added or EntityState.Modified).Select(x => x.Entity).ToArray();
        var guids = assignments.Select(x => x.ActionGuid)
            .Concat(assignments.Where(x => x.ConditionNextAction.HasValue).Select(x => x.ConditionNextAction!.Value))
            .Distinct().ToArray();
        var conditionIds = assignments.Select(x => x.ConditionId).Concat(policies.Select(x => x.ConditionId))
            .Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToArray();
        if (guids.Length == 0 && conditionIds.Length == 0) return null;
        return ActionReferences.Where(x => x.ArchivedAtUtc != null &&
            (guids.Contains(x.ActionGuid) || ScriptActions.Any(s => s.Guid == x.ActionGuid && conditionIds.Contains(s.Id))));
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);
        optionsBuilder.ReplaceService<IMigrationsModelDiffer, CloneDeployModelDiffer>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        // Audit actor identity is a historical snapshot, independent of the live account.
        modelBuilder.Entity<AuditLog>().Ignore(x => x.AppUser);
        modelBuilder.Entity<AuditLog>().HasIndex(x => x.AppUserId);
        modelBuilder.Entity<AgentRequestNonce>(entity =>
        {
            entity.ToTable("AgentRequestNonces");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasMaxLength(64);
            entity.HasIndex(x => x.ExpiresAtUtc);
        });
        modelBuilder.HasPostgresExtension("citext");
        PostgreSqlSchema.Configure(modelBuilder);

        modelBuilder.Entity<AppUser>().ToTable("AppUsers");
        modelBuilder.Entity<IdentityRole<int>>().ToTable("AppRoles");
        modelBuilder.Entity<IdentityUserRole<int>>().ToTable("AppUserRoles");
        modelBuilder.Entity<IdentityUserClaim<int>>().ToTable("AppUserClaims");
        modelBuilder.Entity<IdentityUserLogin<int>>().ToTable("AppUserLogins");
        modelBuilder.Entity<IdentityRoleClaim<int>>().ToTable("AppRoleClaims");
        modelBuilder.Entity<IdentityUserToken<int>>().ToTable("AppUserTokens");

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(CloneDeployDbContext).Assembly);
    }
}
