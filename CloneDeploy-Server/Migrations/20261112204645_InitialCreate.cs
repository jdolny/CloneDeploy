using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace CloneDeploy_Server.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:citext", ",,");

            migrationBuilder.CreateTable(
                name: "ActionDeployments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ActionGuid = table.Column<Guid>(type: "uuid", nullable: false),
                    ActionName = table.Column<string>(type: "text", nullable: false),
                    ActionJson = table.Column<string>(type: "text", nullable: false),
                    ContentHash = table.Column<string>(type: "text", nullable: false),
                    TargetsJson = table.Column<string>(type: "text", nullable: false),
                    Timing = table.Column<int>(type: "integer", nullable: false),
                    Source = table.Column<int>(type: "integer", nullable: false),
                    RequestedByUserId = table.Column<int>(type: "integer", nullable: false),
                    RequestedBy = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActionDeployments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AgentInventoryLease",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<int>(type: "integer", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentInventoryLease", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AgentPortalSession",
                columns: table => new
                {
                    TokenHash = table.Column<string>(type: "character varying(43)", maxLength: 43, nullable: false),
                    DeviceGuid = table.Column<Guid>(type: "uuid", nullable: false),
                    LocalUser = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    DirectorySourceId = table.Column<int>(type: "integer", nullable: false),
                    ObjectGuid = table.Column<Guid>(type: "uuid", nullable: false),
                    DomainGuid = table.Column<Guid>(type: "uuid", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentPortalSession", x => x.TokenHash);
                });

            migrationBuilder.CreateTable(
                name: "AgentRequestNonces",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentRequestNonces", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AppCertificates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Serial = table.Column<string>(type: "text", nullable: false),
                    SubjectName = table.Column<string>(type: "text", nullable: false),
                    PfxBlob = table.Column<byte[]>(type: "bytea", nullable: false),
                    PasswordEncrypted = table.Column<string>(type: "text", nullable: false),
                    NotBeforeUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    NotAfterUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsRevoked = table.Column<bool>(type: "boolean", nullable: false),
                    RevokedDateUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Type = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppCertificates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AppRoles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    NormalizedName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppRoles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AppSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Value = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AppUserGroups",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    GrantAdministrator = table.Column<bool>(type: "boolean", nullable: false),
                    RequireWebMfa = table.Column<bool>(type: "boolean", nullable: false),
                    RequireImagingMfa = table.Column<bool>(type: "boolean", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppUserGroups", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AppUsers",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Membership = table.Column<string>(type: "text", nullable: false),
                    EnableMfa = table.Column<bool>(type: "boolean", nullable: false),
                    EnableImagingMfa = table.Column<bool>(type: "boolean", nullable: false),
                    UserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    NormalizedUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    NormalizedEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    EmailConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    PasswordHash = table.Column<string>(type: "text", nullable: true),
                    SecurityStamp = table.Column<string>(type: "text", nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "text", nullable: true),
                    PhoneNumber = table.Column<string>(type: "text", nullable: true),
                    PhoneNumberConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    TwoFactorEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    LockoutEnd = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LockoutEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    AccessFailedCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppUsers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Attachments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false),
                    AttachedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    DirectoryGuid = table.Column<Guid>(type: "uuid", nullable: false),
                    UserName = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Attachments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AuditLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AppUserId = table.Column<int>(type: "integer", nullable: false),
                    AuditType = table.Column<int>(type: "integer", nullable: false),
                    ObjectType = table.Column<string>(type: "text", nullable: false),
                    ObjectId = table.Column<int>(type: "integer", nullable: false),
                    ObjectName = table.Column<string>(type: "text", nullable: false),
                    LoggedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UserName = table.Column<string>(type: "text", nullable: false),
                    ObjectJson = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditLogs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Certificates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Store = table.Column<string>(type: "text", nullable: false),
                    Subject = table.Column<string>(type: "text", nullable: false),
                    FriendlyName = table.Column<string>(type: "text", nullable: true),
                    Thumbprint = table.Column<string>(type: "text", nullable: false),
                    Serial = table.Column<string>(type: "text", nullable: false),
                    Issuer = table.Column<string>(type: "text", nullable: false),
                    NotBeforeUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    NotAfterUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Certificates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Comments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CommentText = table.Column<string>(type: "text", nullable: false),
                    Username = table.Column<string>(type: "text", nullable: false),
                    CommentedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Comments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CustomAttributes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "citext", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    ClientImagingAvailable = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CustomAttributes", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DeviceCheckinLeases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceGuid = table.Column<Guid>(type: "uuid", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceCheckinLeases", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EmailServerSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Host = table.Column<string>(type: "text", nullable: false),
                    Port = table.Column<int>(type: "integer", nullable: false),
                    StartTls = table.Column<bool>(type: "boolean", nullable: false),
                    UserName = table.Column<string>(type: "text", nullable: false),
                    PasswordEncrypted = table.Column<string>(type: "text", nullable: false),
                    FromAddress = table.Column<string>(type: "text", nullable: false),
                    FromName = table.Column<string>(type: "text", nullable: false),
                    Revision = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailServerSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EntraConnections",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    TenantId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClientId = table.Column<Guid>(type: "uuid", nullable: false),
                    SsoEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    CertificateEncrypted = table.Column<string>(type: "text", nullable: true),
                    PendingCertificateEncrypted = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EntraConnections", x => x.Id);
                    table.CheckConstraint("CK_EntraConnection_Singleton", "\"Id\" = 1");
                });

            migrationBuilder.CreateTable(
                name: "HelpCategory",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HelpCategory", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "HelpQueue",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    EmailEnabled = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HelpQueue", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ImagePrepProfiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "citext", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    AnswerFile = table.Column<string>(type: "text", nullable: true),
                    GenerateAnswerFile = table.Column<bool>(type: "boolean", nullable: false),
                    GeneratorJson = table.Column<string>(type: "text", nullable: true),
                    SetupComplete = table.Column<string>(type: "text", nullable: true),
                    PreSysprepScript = table.Column<string>(type: "text", nullable: true),
                    DisableHibernate = table.Column<bool>(type: "boolean", nullable: false),
                    DisableBitLocker = table.Column<bool>(type: "boolean", nullable: false),
                    ResetAgent = table.Column<bool>(type: "boolean", nullable: false),
                    UninstallAgent = table.Column<bool>(type: "boolean", nullable: false),
                    RunSysprep = table.Column<bool>(type: "boolean", nullable: false),
                    RebootAndCapture = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImagePrepProfiles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Images",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Format = table.Column<int>(type: "integer", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    LastUploadGuid = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "citext", nullable: false),
                    Protected = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Images", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ManagedCredentials",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "citext", nullable: false),
                    Username = table.Column<string>(type: "text", nullable: false),
                    PasswordEncrypted = table.Column<string>(type: "text", nullable: false),
                    Guid = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ManagedCredentials", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Schedules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "citext", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    IntervalMinutes = table.Column<int>(type: "integer", nullable: true),
                    StartsAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Monday = table.Column<bool>(type: "boolean", nullable: false),
                    Tuesday = table.Column<bool>(type: "boolean", nullable: false),
                    Wednesday = table.Column<bool>(type: "boolean", nullable: false),
                    Thursday = table.Column<bool>(type: "boolean", nullable: false),
                    Friday = table.Column<bool>(type: "boolean", nullable: false),
                    Saturday = table.Column<bool>(type: "boolean", nullable: false),
                    Sunday = table.Column<bool>(type: "boolean", nullable: false),
                    Time = table.Column<TimeSpan>(type: "interval", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Schedules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SecurityActivityEvent",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OccurredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Category = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Account = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    SourceIp = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: false),
                    Target = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SecurityActivityEvent", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SelfServiceCategory",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Icon = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    Revision = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SelfServiceCategory", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SharedContent",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    LogicalKey = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    SourceProviderId = table.Column<int>(type: "integer", nullable: true),
                    SourcePath = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                    ModifiedTicks = table.Column<long>(type: "bigint", nullable: false),
                    Current = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SharedContent", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Software",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: true),
                    Version = table.Column<string>(type: "text", nullable: true),
                    Major = table.Column<int>(type: "integer", nullable: false),
                    Minor = table.Column<int>(type: "integer", nullable: false),
                    Build = table.Column<int>(type: "integer", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Software", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Tags",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "citext", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Tags", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserAudience",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Revision = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserAudience", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "UserNotificationAcknowledgments",
                columns: table => new
                {
                    NotificationId = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    UserKey = table.Column<string>(type: "character varying(220)", maxLength: 220, nullable: false),
                    AcknowledgedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AcknowledgedOnDeviceId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserNotificationAcknowledgments", x => new { x.NotificationId, x.UserKey });
                });

            migrationBuilder.CreateTable(
                name: "WindowsUpdates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ApprovedRevisionNumber = table.Column<int>(type: "integer", nullable: true),
                    ApprovedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ApprovedBy = table.Column<string>(type: "text", nullable: true),
                    ApprovedByRuleId = table.Column<int>(type: "integer", nullable: true),
                    AutoApprovalBlockedRevision = table.Column<int>(type: "integer", nullable: true),
                    RevisionFirstSeenAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RevisionNumber = table.Column<int>(type: "integer", nullable: true),
                    KBArticleIds = table.Column<string[]>(type: "text[]", nullable: true),
                    CategoriesJson = table.Column<string>(type: "jsonb", nullable: true),
                    MsrcSeverity = table.Column<string>(type: "text", nullable: true),
                    Type = table.Column<string>(type: "text", nullable: true),
                    Title = table.Column<string>(type: "text", nullable: true),
                    Category = table.Column<string>(type: "text", nullable: true),
                    UpdateId = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WindowsUpdates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WindowsWorkerJob",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SiteId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    State = table.Column<string>(type: "text", nullable: false),
                    RequestEncrypted = table.Column<string>(type: "text", nullable: false),
                    InputsJson = table.Column<string>(type: "text", nullable: false),
                    OutputsJson = table.Column<string>(type: "text", nullable: false),
                    Result = table.Column<string>(type: "text", nullable: true),
                    Error = table.Column<string>(type: "text", nullable: true),
                    Lease = table.Column<Guid>(type: "uuid", nullable: false),
                    LeaseUntilUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    Progress = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WindowsWorkerJob", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WinGetInstallerManifests",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PackageIdentifier = table.Column<string>(type: "text", nullable: true),
                    PackageVersion = table.Column<string>(type: "text", nullable: true),
                    Scope = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WinGetInstallerManifests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WinGetLocaleManifests",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PackageIdentifier = table.Column<string>(type: "text", nullable: true),
                    PackageVersion = table.Column<string>(type: "text", nullable: true),
                    Locale = table.Column<string>(type: "text", nullable: true),
                    Publisher = table.Column<string>(type: "text", nullable: true),
                    PublisherUrl = table.Column<string>(type: "text", nullable: true),
                    PackageName = table.Column<string>(type: "text", nullable: true),
                    PackageUrl = table.Column<string>(type: "text", nullable: true),
                    License = table.Column<string>(type: "text", nullable: true),
                    ShortDescription = table.Column<string>(type: "text", nullable: true),
                    ManifestType = table.Column<string>(type: "text", nullable: true),
                    Tags = table.Column<string>(type: "text", nullable: true),
                    Moniker = table.Column<string>(type: "text", nullable: true),
                    Major = table.Column<int>(type: "integer", nullable: false),
                    Minor = table.Column<int>(type: "integer", nullable: false),
                    Build = table.Column<int>(type: "integer", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WinGetLocaleManifests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WinGetManifestDownloads",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Url = table.Column<string>(type: "text", nullable: true),
                    Progress = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true),
                    DownloadedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WinGetManifestDownloads", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WinGetVersionManifests",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PackageIdentifier = table.Column<string>(type: "text", nullable: true),
                    PackageVersion = table.Column<string>(type: "text", nullable: true),
                    DefaultLocale = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WinGetVersionManifests", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "WinPeToolchains",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "citext", maxLength: 200, nullable: false),
                    Version = table.Column<string>(type: "text", nullable: false),
                    AdkUrl = table.Column<string>(type: "text", nullable: false),
                    AdkSha256 = table.Column<string>(type: "text", nullable: false),
                    WinPeUrl = table.Column<string>(type: "text", nullable: false),
                    WinPeSha256 = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WinPeToolchains", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DeviceTasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DeploymentId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<int>(type: "integer", nullable: false),
                    DeviceGuid = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceName = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    LeaseToken = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ManagedWinGetActionsJson = table.Column<string>(type: "text", nullable: true),
                    StartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FinishedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastNotifiedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Result = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceTasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeviceTasks_ActionDeployments_DeploymentId",
                        column: x => x.DeploymentId,
                        principalTable: "ActionDeployments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SiteServices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TokenExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    BackendAddress = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    BackendUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    PublicUrl = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    ListenAddress = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    WindowsWorkerRoles = table.Column<int>(type: "integer", nullable: false),
                    IsWindowsWorker = table.Column<bool>(type: "boolean", nullable: false),
                    ProxyEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    BootSettingsJson = table.Column<string>(type: "text", nullable: false),
                    BootStatusJson = table.Column<string>(type: "text", nullable: true),
                    IdentityCsr = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: true),
                    HttpsCsr = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: true),
                    KeyFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Hostname = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    HostOs = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    Version = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Addresses = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: true),
                    IssuerId = table.Column<int>(type: "integer", nullable: false),
                    IdentityCertificate = table.Column<byte[]>(type: "bytea", nullable: true),
                    HttpsCertificate = table.Column<byte[]>(type: "bytea", nullable: true),
                    CertificateSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PreviousCertificateSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    PreviousCertificateUntilUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CertificateExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastContactAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AppliedRevision = table.Column<int>(type: "integer", nullable: false),
                    ProxyRunning = table.Column<bool>(type: "boolean", nullable: false),
                    LastError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SiteServices", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SiteServices_AppCertificates_IssuerId",
                        column: x => x.IssuerId,
                        principalTable: "AppCertificates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AppRoleClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RoleId = table.Column<int>(type: "integer", nullable: false),
                    ClaimType = table.Column<string>(type: "text", nullable: true),
                    ClaimValue = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppRoleClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AppRoleClaims_AppRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AppRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AppUserGroupRights",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AppUserGroupId = table.Column<int>(type: "integer", nullable: false),
                    Right = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppUserGroupRights", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AppUserGroupRights_AppUserGroups_AppUserGroupId",
                        column: x => x.AppUserGroupId,
                        principalTable: "AppUserGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AppUserClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    ClaimType = table.Column<string>(type: "text", nullable: true),
                    ClaimValue = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppUserClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AppUserClaims_AppUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AppUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AppUserGridColumns",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AppUserId = table.Column<int>(type: "integer", nullable: false),
                    GridKey = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    ColumnKey = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Visible = table.Column<bool>(type: "boolean", nullable: false),
                    Order = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppUserGridColumns", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AppUserGridColumns_AppUsers_AppUserId",
                        column: x => x.AppUserId,
                        principalTable: "AppUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AppUserGroupMemberships",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AppUserGroupId = table.Column<int>(type: "integer", nullable: false),
                    AppUserId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppUserGroupMemberships", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AppUserGroupMemberships_AppUserGroups_AppUserGroupId",
                        column: x => x.AppUserGroupId,
                        principalTable: "AppUserGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AppUserGroupMemberships_AppUsers_AppUserId",
                        column: x => x.AppUserId,
                        principalTable: "AppUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AppUserLogins",
                columns: table => new
                {
                    LoginProvider = table.Column<string>(type: "text", nullable: false),
                    ProviderKey = table.Column<string>(type: "text", nullable: false),
                    ProviderDisplayName = table.Column<string>(type: "text", nullable: true),
                    UserId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppUserLogins", x => new { x.LoginProvider, x.ProviderKey });
                    table.ForeignKey(
                        name: "FK_AppUserLogins_AppUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AppUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AppUserPreferences",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AppUserId = table.Column<int>(type: "integer", nullable: false),
                    ColorMode = table.Column<int>(type: "integer", nullable: false),
                    Language = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppUserPreferences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AppUserPreferences_AppUsers_AppUserId",
                        column: x => x.AppUserId,
                        principalTable: "AppUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AppUserRights",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Right = table.Column<string>(type: "text", nullable: false),
                    AppUserId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppUserRights", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AppUserRights_AppUsers_AppUserId",
                        column: x => x.AppUserId,
                        principalTable: "AppUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AppUserRoles",
                columns: table => new
                {
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    RoleId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppUserRoles", x => new { x.UserId, x.RoleId });
                    table.ForeignKey(
                        name: "FK_AppUserRoles_AppRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AppRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AppUserRoles_AppUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AppUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AppUserTokens",
                columns: table => new
                {
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    LoginProvider = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Value = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppUserTokens", x => new { x.UserId, x.LoginProvider, x.Name });
                    table.ForeignKey(
                        name: "FK_AppUserTokens_AppUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AppUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EmailDigestDelivery",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AppUserId = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<string>(type: "text", nullable: false),
                    Period = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Detail = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailDigestDelivery", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EmailDigestDelivery_AppUsers_AppUserId",
                        column: x => x.AppUserId,
                        principalTable: "AppUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SavedDeviceReport",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AppUserId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "citext", maxLength: 200, nullable: false),
                    QueryJson = table.Column<string>(type: "text", nullable: false),
                    ColumnsJson = table.Column<string>(type: "text", nullable: false),
                    Revision = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SavedDeviceReport", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SavedDeviceReport_AppUsers_AppUserId",
                        column: x => x.AppUserId,
                        principalTable: "AppUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SavedUserDeviceReport",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AppUserId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "citext", maxLength: 200, nullable: false),
                    QueryJson = table.Column<string>(type: "text", nullable: false),
                    ColumnsJson = table.Column<string>(type: "text", nullable: false),
                    Revision = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SavedUserDeviceReport", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SavedUserDeviceReport_AppUsers_AppUserId",
                        column: x => x.AppUserId,
                        principalTable: "AppUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserEmailSubscription",
                columns: table => new
                {
                    AppUserId = table.Column<int>(type: "integer", nullable: false),
                    Daily = table.Column<bool>(type: "boolean", nullable: false),
                    Weekly = table.Column<bool>(type: "boolean", nullable: false),
                    SkipEmpty = table.Column<bool>(type: "boolean", nullable: false),
                    Approvals = table.Column<bool>(type: "boolean", nullable: false),
                    Imaging = table.Column<bool>(type: "boolean", nullable: false),
                    Policies = table.Column<bool>(type: "boolean", nullable: false),
                    Actions = table.Column<bool>(type: "boolean", nullable: false),
                    DirectorySync = table.Column<bool>(type: "boolean", nullable: false),
                    DeviceWarnings = table.Column<bool>(type: "boolean", nullable: false),
                    ServerUpdates = table.Column<bool>(type: "boolean", nullable: false),
                    HelpDesk = table.Column<bool>(type: "boolean", nullable: false),
                    Security = table.Column<bool>(type: "boolean", nullable: false),
                    TimeZoneId = table.Column<string>(type: "text", nullable: false),
                    DeliveryMinute = table.Column<int>(type: "integer", nullable: false),
                    WeeklyDay = table.Column<int>(type: "integer", nullable: false),
                    EnabledAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Revision = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserEmailSubscription", x => x.AppUserId);
                    table.ForeignKey(
                        name: "FK_UserEmailSubscription_AppUsers_AppUserId",
                        column: x => x.AppUserId,
                        principalTable: "AppUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WindowsUpdateAutoApprovalRules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Severities = table.Column<string[]>(type: "text[]", nullable: false),
                    ClassificationIds = table.Column<string[]>(type: "text[]", nullable: false),
                    DelayDays = table.Column<int>(type: "integer", nullable: false),
                    AppUserId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WindowsUpdateAutoApprovalRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WindowsUpdateAutoApprovalRules_AppUsers_AppUserId",
                        column: x => x.AppUserId,
                        principalTable: "AppUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "HelpQueueMember",
                columns: table => new
                {
                    QueueId = table.Column<int>(type: "integer", nullable: false),
                    UserId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HelpQueueMember", x => new { x.QueueId, x.UserId });
                    table.ForeignKey(
                        name: "FK_HelpQueueMember_AppUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AppUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_HelpQueueMember_HelpQueue_QueueId",
                        column: x => x.QueueId,
                        principalTable: "HelpQueue",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "HelpRoutingRule",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Stage = table.Column<int>(type: "integer", nullable: false),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    MatchAll = table.Column<bool>(type: "boolean", nullable: false),
                    ConditionsJson = table.Column<string>(type: "text", nullable: false),
                    CategoryId = table.Column<int>(type: "integer", nullable: true),
                    QueueId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HelpRoutingRule", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HelpRoutingRule_HelpCategory_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "HelpCategory",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_HelpRoutingRule_HelpQueue_QueueId",
                        column: x => x.QueueId,
                        principalTable: "HelpQueue",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "HelpTickets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CommandId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequesterKeys = table.Column<string[]>(type: "text[]", nullable: false),
                    RequesterName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Subject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DeviceId = table.Column<int>(type: "integer", nullable: false),
                    DeviceName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    AssignedToId = table.Column<int>(type: "integer", nullable: true),
                    CategoryId = table.Column<int>(type: "integer", nullable: true),
                    QueueId = table.Column<int>(type: "integer", nullable: true),
                    RoutingSummary = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastStaffMessageId = table.Column<long>(type: "bigint", nullable: false),
                    ReadThroughMessageId = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HelpTickets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HelpTickets_AppUsers_AssignedToId",
                        column: x => x.AssignedToId,
                        principalTable: "AppUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_HelpTickets_HelpCategory_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "HelpCategory",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_HelpTickets_HelpQueue_QueueId",
                        column: x => x.QueueId,
                        principalTable: "HelpQueue",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ActionReferences",
                columns: table => new
                {
                    ActionGuid = table.Column<Guid>(type: "uuid", nullable: false),
                    ActionType = table.Column<int>(type: "integer", nullable: false),
                    ArchivedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ExternalFileUpdateScheduleId = table.Column<int>(type: "integer", nullable: true),
                    ExternalFilesLastEvaluatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActionReferences", x => x.ActionGuid);
                    table.ForeignKey(
                        name: "FK_ActionReferences_Schedules_ExternalFileUpdateScheduleId",
                        column: x => x.ExternalFileUpdateScheduleId,
                        principalTable: "Schedules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DirectorySources",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Provider = table.Column<int>(type: "integer", nullable: false),
                    ApplicationAccessOnly = table.Column<bool>(type: "boolean", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    ScheduleId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DirectorySources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DirectorySources_Schedules_ScheduleId",
                        column: x => x.ScheduleId,
                        principalTable: "Schedules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DriverProfiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DirectoryGuid = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "citext", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    ManufacturerRegex = table.Column<string>(type: "text", nullable: true),
                    ModelRegex = table.Column<string>(type: "text", nullable: true),
                    UpdateScheduleId = table.Column<int>(type: "integer", nullable: true),
                    LastEvaluatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DriverProfiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DriverProfiles_Schedules_UpdateScheduleId",
                        column: x => x.UpdateScheduleId,
                        principalTable: "Schedules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ScheduledTask",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    ScheduleId = table.Column<int>(type: "integer", nullable: true),
                    LastEvaluatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduledTask", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScheduledTask_Schedules_ScheduleId",
                        column: x => x.ScheduleId,
                        principalTable: "Schedules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ImageTags",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ImageId = table.Column<int>(type: "integer", nullable: false),
                    TagId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImageTags", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ImageTags_Images_ImageId",
                        column: x => x.ImageId,
                        principalTable: "Images",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ImageTags_Tags_TagId",
                        column: x => x.TagId,
                        principalTable: "Tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserAudienceMember",
                columns: table => new
                {
                    UserAudienceId = table.Column<int>(type: "integer", nullable: false),
                    DirectorySourceId = table.Column<int>(type: "integer", nullable: false),
                    ObjectGuid = table.Column<Guid>(type: "uuid", nullable: false),
                    Exclude = table.Column<bool>(type: "boolean", nullable: false),
                    IncludeDescendants = table.Column<bool>(type: "boolean", nullable: false),
                    DisplayName = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserAudienceMember", x => new { x.UserAudienceId, x.DirectorySourceId, x.ObjectGuid, x.Exclude });
                    table.ForeignKey(
                        name: "FK_UserAudienceMember_UserAudience_UserAudienceId",
                        column: x => x.UserAudienceId,
                        principalTable: "UserAudience",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "WinPeSetupJobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ToolchainId = table.Column<int>(type: "integer", nullable: false),
                    RequestJson = table.Column<string>(type: "text", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WinPeSetupJobs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WinPeSetupJobs_WinPeToolchains_ToolchainId",
                        column: x => x.ToolchainId,
                        principalTable: "WinPeToolchains",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StorageProviders",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "citext", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    Path = table.Column<string>(type: "text", nullable: false),
                    LinuxMountPath = table.Column<string>(type: "text", nullable: true),
                    IsImagingDefault = table.Column<bool>(type: "boolean", nullable: false),
                    IsManagementDefault = table.Column<bool>(type: "boolean", nullable: false),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    ImagingDirectConnect = table.Column<bool>(type: "boolean", nullable: false),
                    WinPeSmbPath = table.Column<string>(type: "text", nullable: true),
                    Purposes = table.Column<int>(type: "integer", nullable: false, defaultValue: 3),
                    PreferredClientNetworks = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: false, defaultValue: ""),
                    SiteServiceId = table.Column<Guid>(type: "uuid", nullable: true),
                    ManagedCredentialId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StorageProviders", x => x.Id);
                    table.CheckConstraint("CK_StorageProviders_DefaultEnabled", "(NOT \"IsImagingDefault\" OR (\"IsEnabled\" AND \"SiteServiceId\" IS NULL AND (\"Purposes\" & 1) = 1)) AND (NOT \"IsManagementDefault\" OR (\"IsEnabled\" AND \"SiteServiceId\" IS NULL AND (\"Purposes\" & 2) = 2))");
                    table.CheckConstraint("CK_StorageProviders_Purposes", "\"Purposes\" BETWEEN 1 AND 3");
                    table.ForeignKey(
                        name: "FK_StorageProviders_ManagedCredentials_ManagedCredentialId",
                        column: x => x.ManagedCredentialId,
                        principalTable: "ManagedCredentials",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_StorageProviders_SiteServices_SiteServiceId",
                        column: x => x.SiteServiceId,
                        principalTable: "SiteServices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "HelpEmailDelivery",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TicketId = table.Column<int>(type: "integer", nullable: false),
                    BatchId = table.Column<Guid>(type: "uuid", nullable: false),
                    Address = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    Subject = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Body = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HelpEmailDelivery", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HelpEmailDelivery_HelpTickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "HelpTickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "HelpEmailState",
                columns: table => new
                {
                    TicketId = table.Column<int>(type: "integer", nullable: false),
                    FirstPendingUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DueAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastSentUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Immediate = table.Column<bool>(type: "boolean", nullable: false),
                    Updates = table.Column<int>(type: "integer", nullable: false),
                    Reason = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HelpEmailState", x => x.TicketId);
                    table.ForeignKey(
                        name: "FK_HelpEmailState_HelpTickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "HelpTickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "HelpTicketMessages",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TicketId = table.Column<int>(type: "integer", nullable: false),
                    CommandId = table.Column<Guid>(type: "uuid", nullable: false),
                    Author = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    StaffUserId = table.Column<int>(type: "integer", nullable: true),
                    FromStaff = table.Column<bool>(type: "boolean", nullable: false),
                    Body = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Screenshot = table.Column<byte[]>(type: "bytea", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HelpTicketMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HelpTicketMessages_HelpTickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "HelpTickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "HelpTicketStatusEvent",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TicketId = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    OccurredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HelpTicketStatusEvent", x => x.Id);
                    table.ForeignKey(
                        name: "FK_HelpTicketStatusEvent_HelpTickets_TicketId",
                        column: x => x.TicketId,
                        principalTable: "HelpTickets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ActionTags",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ActionGuid = table.Column<Guid>(type: "uuid", nullable: false),
                    TagId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActionTags", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ActionTags_ActionReferences_ActionGuid",
                        column: x => x.ActionGuid,
                        principalTable: "ActionReferences",
                        principalColumn: "ActionGuid",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ActionTags_Tags_TagId",
                        column: x => x.TagId,
                        principalTable: "Tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CommandActions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Guid = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Command = table.Column<string>(type: "text", nullable: false),
                    Arguments = table.Column<string>(type: "text", nullable: true),
                    Timeout = table.Column<int>(type: "integer", nullable: false),
                    WorkingDirectory = table.Column<string>(type: "text", nullable: true),
                    RedirectStdOut = table.Column<bool>(type: "boolean", nullable: false),
                    RedirectStdError = table.Column<bool>(type: "boolean", nullable: false),
                    SuccessCodes = table.Column<string>(type: "text", nullable: false),
                    ManagedCredentialId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CommandActions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CommandActions_ActionReferences_Guid",
                        column: x => x.Guid,
                        principalTable: "ActionReferences",
                        principalColumn: "ActionGuid",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_CommandActions_ManagedCredentials_ManagedCredentialId",
                        column: x => x.ManagedCredentialId,
                        principalTable: "ManagedCredentials",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "FileActions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Guid = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Destination = table.Column<string>(type: "text", nullable: false),
                    DecompressAfterCopy = table.Column<bool>(type: "boolean", nullable: false),
                    OverwriteExisting = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FileActions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FileActions_ActionReferences_Guid",
                        column: x => x.Guid,
                        principalTable: "ActionReferences",
                        principalColumn: "ActionGuid",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "NotificationActions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Guid = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Title = table.Column<string>(type: "text", nullable: false),
                    Body = table.Column<string>(type: "text", nullable: false),
                    Timeout = table.Column<int>(type: "integer", nullable: false),
                    RequireAcknowledgment = table.Column<bool>(type: "boolean", nullable: false),
                    NoticeId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RemindAtLogin = table.Column<bool>(type: "boolean", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    Branding = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationActions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_NotificationActions_ActionReferences_Guid",
                        column: x => x.Guid,
                        principalTable: "ActionReferences",
                        principalColumn: "ActionGuid",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PrinterActions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Guid = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    NetworkPath = table.Column<string>(type: "text", nullable: false),
                    Action = table.Column<int>(type: "integer", nullable: false),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    RestartSpooler = table.Column<bool>(type: "boolean", nullable: false),
                    WaitForEnumeration = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PrinterActions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PrinterActions_ActionReferences_Guid",
                        column: x => x.Guid,
                        principalTable: "ActionReferences",
                        principalColumn: "ActionGuid",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ScriptActions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Guid = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    ScriptType = table.Column<int>(type: "integer", nullable: false),
                    Arguments = table.Column<string>(type: "text", nullable: true),
                    ScriptContents = table.Column<string>(type: "text", nullable: true),
                    Timeout = table.Column<int>(type: "integer", nullable: false),
                    WorkingDirectory = table.Column<string>(type: "text", nullable: true),
                    RedirectStdOut = table.Column<bool>(type: "boolean", nullable: false),
                    RedirectStdError = table.Column<bool>(type: "boolean", nullable: false),
                    SuccessCodes = table.Column<string>(type: "text", nullable: false),
                    AddInventoryCollection = table.Column<bool>(type: "boolean", nullable: false),
                    IsCondition = table.Column<bool>(type: "boolean", nullable: false),
                    ManagedCredentialId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScriptActions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScriptActions_ActionReferences_Guid",
                        column: x => x.Guid,
                        principalTable: "ActionReferences",
                        principalColumn: "ActionGuid",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ScriptActions_ManagedCredentials_ManagedCredentialId",
                        column: x => x.ManagedCredentialId,
                        principalTable: "ManagedCredentials",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "WindowsUpdateActions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Guid = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Mode = table.Column<int>(type: "integer", nullable: false),
                    Timeout = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WindowsUpdateActions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WindowsUpdateActions_ActionReferences_Guid",
                        column: x => x.Guid,
                        principalTable: "ActionReferences",
                        principalColumn: "ActionGuid",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AppUserGroupDirectoryMapping",
                columns: table => new
                {
                    AppUserGroupId = table.Column<int>(type: "integer", nullable: false),
                    DirectorySourceId = table.Column<int>(type: "integer", nullable: false),
                    ObjectGuid = table.Column<Guid>(type: "uuid", nullable: false),
                    DisplayName = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AppUserGroupDirectoryMapping", x => new { x.AppUserGroupId, x.DirectorySourceId, x.ObjectGuid });
                    table.ForeignKey(
                        name: "FK_AppUserGroupDirectoryMapping_AppUserGroups_AppUserGroupId",
                        column: x => x.AppUserGroupId,
                        principalTable: "AppUserGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AppUserGroupDirectoryMapping_DirectorySources_DirectorySour~",
                        column: x => x.DirectorySourceId,
                        principalTable: "DirectorySources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DirectoryObjects",
                columns: table => new
                {
                    DirectorySourceId = table.Column<int>(type: "integer", nullable: false),
                    ObjectGuid = table.Column<Guid>(type: "uuid", nullable: false),
                    ParentGuid = table.Column<Guid>(type: "uuid", nullable: true),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    DistinguishedName = table.Column<string>(type: "text", nullable: false),
                    AccountName = table.Column<string>(type: "text", nullable: true),
                    UserPrincipalName = table.Column<string>(type: "text", nullable: true),
                    Email = table.Column<string>(type: "text", nullable: true),
                    DnsHostName = table.Column<string>(type: "text", nullable: true),
                    ObjectSid = table.Column<string>(type: "text", nullable: true),
                    Disabled = table.Column<bool>(type: "boolean", nullable: false),
                    LastSeenAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsMissing = table.Column<bool>(type: "boolean", nullable: false),
                    IsScopeAncestor = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DirectoryObjects", x => new { x.DirectorySourceId, x.ObjectGuid });
                    table.ForeignKey(
                        name: "FK_DirectoryObjects_DirectorySources_DirectorySourceId",
                        column: x => x.DirectorySourceId,
                        principalTable: "DirectorySources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DirectorySyncStates",
                columns: table => new
                {
                    DirectorySourceId = table.Column<int>(type: "integer", nullable: false),
                    RootObjectGuid = table.Column<Guid>(type: "uuid", nullable: false),
                    DomainObjectGuid = table.Column<Guid>(type: "uuid", nullable: true),
                    LastSuccessfulSyncAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DirectorySyncStates", x => x.DirectorySourceId);
                    table.ForeignKey(
                        name: "FK_DirectorySyncStates_DirectorySources_DirectorySourceId",
                        column: x => x.DirectorySourceId,
                        principalTable: "DirectorySources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "LdapDirectorySettings",
                columns: table => new
                {
                    DirectorySourceId = table.Column<int>(type: "integer", nullable: false),
                    Host = table.Column<string>(type: "text", nullable: false),
                    Port = table.Column<int>(type: "integer", nullable: false),
                    BaseDns = table.Column<string[]>(type: "text[]", nullable: false),
                    ManagedCredentialId = table.Column<int>(type: "integer", nullable: false),
                    PageSize = table.Column<int>(type: "integer", nullable: false),
                    TimeoutSeconds = table.Column<int>(type: "integer", nullable: false),
                    MaxObjects = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LdapDirectorySettings", x => x.DirectorySourceId);
                    table.ForeignKey(
                        name: "FK_LdapDirectorySettings_DirectorySources_DirectorySourceId",
                        column: x => x.DirectorySourceId,
                        principalTable: "DirectorySources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_LdapDirectorySettings_ManagedCredentials_ManagedCredentialId",
                        column: x => x.ManagedCredentialId,
                        principalTable: "ManagedCredentials",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "OuNamingRuleSets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "citext", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    DirectorySourceId = table.Column<int>(type: "integer", nullable: true),
                    DomainName = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Fallback = table.Column<int>(type: "integer", nullable: false),
                    FallbackOuGuid = table.Column<Guid>(type: "uuid", nullable: true),
                    RulesJson = table.Column<string>(type: "text", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OuNamingRuleSets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OuNamingRuleSets_DirectorySources_DirectorySourceId",
                        column: x => x.DirectorySourceId,
                        principalTable: "DirectorySources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DriverModelLinks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DriverProfileId = table.Column<int>(type: "integer", nullable: false),
                    Manufacturer = table.Column<string>(type: "citext", nullable: false),
                    Model = table.Column<string>(type: "citext", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DriverModelLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DriverModelLinks_DriverProfiles_DriverProfileId",
                        column: x => x.DriverProfileId,
                        principalTable: "DriverProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DriverPackRegexRules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DriverPackId = table.Column<int>(type: "integer", nullable: false),
                    ManufacturerRegex = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ModelRegex = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DriverPackRegexRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DriverPackRegexRules_DriverProfiles_DriverPackId",
                        column: x => x.DriverPackId,
                        principalTable: "DriverProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DriverProfileTag",
                columns: table => new
                {
                    DriverProfileId = table.Column<int>(type: "integer", nullable: false),
                    TagId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DriverProfileTag", x => new { x.DriverProfileId, x.TagId });
                    table.ForeignKey(
                        name: "FK_DriverProfileTag_DriverProfiles_DriverProfileId",
                        column: x => x.DriverProfileId,
                        principalTable: "DriverProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DriverProfileTag_Tags_TagId",
                        column: x => x.TagId,
                        principalTable: "Tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ScheduledTaskExecution",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TaskId = table.Column<int>(type: "integer", nullable: false),
                    TargetId = table.Column<int>(type: "integer", nullable: true),
                    TargetName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    ScheduledForUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    QueuedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FinishedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Summary = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ScheduledTaskExecution", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ScheduledTaskExecution_ScheduledTask_TaskId",
                        column: x => x.TaskId,
                        principalTable: "ScheduledTask",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ImageContentDeletion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ImageId = table.Column<int>(type: "integer", nullable: false),
                    StorageProviderId = table.Column<int>(type: "integer", nullable: false),
                    QueuedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImageContentDeletion", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ImageContentDeletion_StorageProviders_StorageProviderId",
                        column: x => x.StorageProviderId,
                        principalTable: "StorageProviders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ImageReplica",
                columns: table => new
                {
                    ImageVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    StorageProviderId = table.Column<int>(type: "integer", nullable: false),
                    ImageId = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RetryAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImageReplica", x => new { x.ImageVersionId, x.StorageProviderId });
                    table.ForeignKey(
                        name: "FK_ImageReplica_StorageProviders_StorageProviderId",
                        column: x => x.StorageProviderId,
                        principalTable: "StorageProviders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ImageReplicationTarget",
                columns: table => new
                {
                    ImageId = table.Column<int>(type: "integer", nullable: false),
                    StorageProviderId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImageReplicationTarget", x => new { x.ImageId, x.StorageProviderId });
                    table.ForeignKey(
                        name: "FK_ImageReplicationTarget_Images_ImageId",
                        column: x => x.ImageId,
                        principalTable: "Images",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ImageReplicationTarget_StorageProviders_StorageProviderId",
                        column: x => x.StorageProviderId,
                        principalTable: "StorageProviders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ImageVersions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ImageId = table.Column<int>(type: "integer", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    UploadedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    StorageProviderId = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    IsApproved = table.Column<bool>(type: "boolean", nullable: false),
                    ReplicationApproved = table.Column<bool>(type: "boolean", nullable: false),
                    Format = table.Column<int>(type: "integer", nullable: true),
                    Layout = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    FileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    Pipable = table.Column<bool>(type: "boolean", nullable: true),
                    Optimized = table.Column<bool>(type: "boolean", nullable: true),
                    MinimumDiskSizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    DeviceId = table.Column<int>(type: "integer", nullable: true),
                    DeviceName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImageVersions", x => x.Id);
                    table.CheckConstraint("CK_ImageVersions_MinimumDiskSize", "\"MinimumDiskSizeBytes\" IS NULL OR \"MinimumDiskSizeBytes\" > 0");
                    table.CheckConstraint("CK_ImageVersions_Number", "\"Number\" > 0");
                    table.CheckConstraint("CK_ImageVersions_Size", "\"SizeBytes\" >= 0");
                    table.ForeignKey(
                        name: "FK_ImageVersions_Images_ImageId",
                        column: x => x.ImageId,
                        principalTable: "Images",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ImageVersions_StorageProviders_StorageProviderId",
                        column: x => x.StorageProviderId,
                        principalTable: "StorageProviders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SharedContentReplica",
                columns: table => new
                {
                    ContentId = table.Column<string>(type: "character varying(64)", nullable: false),
                    StorageProviderId = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RetryAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SharedContentReplica", x => new { x.ContentId, x.StorageProviderId });
                    table.ForeignKey(
                        name: "FK_SharedContentReplica_SharedContent_ContentId",
                        column: x => x.ContentId,
                        principalTable: "SharedContent",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SharedContentReplica_StorageProviders_StorageProviderId",
                        column: x => x.StorageProviderId,
                        principalTable: "StorageProviders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StorageMoves",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceId = table.Column<int>(type: "integer", nullable: false),
                    DestinationId = table.Column<int>(type: "integer", nullable: false),
                    Purposes = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    BytesCompleted = table.Column<long>(type: "bigint", nullable: false),
                    BytesTotal = table.Column<long>(type: "bigint", nullable: false),
                    FilesJson = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CleanupAfterUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StorageMoves", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StorageMoves_StorageProviders_DestinationId",
                        column: x => x.DestinationId,
                        principalTable: "StorageProviders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_StorageMoves_StorageProviders_SourceId",
                        column: x => x.SourceId,
                        principalTable: "StorageProviders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "StorageReadLease",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ContentKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    StorageProviderId = table.Column<int>(type: "integer", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StorageReadLease", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StorageReadLease_StorageProviders_StorageProviderId",
                        column: x => x.StorageProviderId,
                        principalTable: "StorageProviders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StoredFiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    StorageProviderId = table.Column<int>(type: "integer", nullable: true),
                    DirectoryGuid = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "citext", nullable: false),
                    SourceType = table.Column<int>(type: "integer", nullable: false),
                    Size = table.Column<long>(type: "bigint", nullable: true),
                    Sha256Hash = table.Column<string>(type: "text", nullable: true),
                    Md5Hash = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    AvailableAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true),
                    Url = table.Column<string>(type: "text", nullable: true),
                    ExpectedSha256Hash = table.Column<string>(type: "text", nullable: true),
                    AutoRefresh = table.Column<bool>(type: "boolean", nullable: false),
                    LastCheckedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Progress = table.Column<string>(type: "text", nullable: true),
                    LastDownloadAttemptAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StoredFiles", x => x.Id);
                    table.CheckConstraint("CK_StoredFiles_Source", "(\"SourceType\" = 0 AND \"Url\" IS NULL) OR (\"SourceType\" = 1 AND \"Url\" IS NOT NULL AND length(trim(\"Url\")) > 0)");
                    table.ForeignKey(
                        name: "FK_StoredFiles_StorageProviders_StorageProviderId",
                        column: x => x.StorageProviderId,
                        principalTable: "StorageProviders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Policies",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Guid = table.Column<Guid>(type: "uuid", nullable: false),
                    Hash = table.Column<string>(type: "text", nullable: true),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    ExecutionPriority = table.Column<int>(type: "integer", nullable: false, defaultValue: 100),
                    ExecutionRevision = table.Column<long>(type: "bigint", nullable: false),
                    Trigger = table.Column<int>(type: "integer", nullable: false),
                    SelfServiceRunAsUser = table.Column<bool>(type: "boolean", nullable: false),
                    Frequency = table.Column<int>(type: "integer", nullable: false),
                    SubFrequency = table.Column<int>(type: "integer", nullable: false),
                    StartDateUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAction = table.Column<int>(type: "integer", nullable: false),
                    RunInventory = table.Column<bool>(type: "boolean", nullable: false),
                    RunLoginTracker = table.Column<bool>(type: "boolean", nullable: false),
                    RemoveInstallCache = table.Column<bool>(type: "boolean", nullable: false),
                    ExecutionType = table.Column<int>(type: "integer", nullable: false),
                    ErrorAction = table.Column<int>(type: "integer", nullable: false),
                    LogLevel = table.Column<int>(type: "integer", nullable: false),
                    MissedAction = table.Column<int>(type: "integer", nullable: false),
                    AutoArchiveType = table.Column<int>(type: "integer", nullable: false),
                    AutoArchiveSub = table.Column<string>(type: "text", nullable: true),
                    Archived = table.Column<bool>(type: "boolean", nullable: false),
                    SkipServerResult = table.Column<bool>(type: "boolean", nullable: false),
                    StartWindowEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    StartWindowDays = table.Column<int>(type: "integer", nullable: false),
                    StartWindowFrom = table.Column<TimeSpan>(type: "interval", nullable: true),
                    StartWindowUntil = table.Column<TimeSpan>(type: "interval", nullable: true),
                    ArchivedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ConditionId = table.Column<int>(type: "integer", nullable: true),
                    ConditionFailedAction = table.Column<int>(type: "integer", nullable: false),
                    RemoteAccess = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Policies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Policies_ScriptActions_ConditionId",
                        column: x => x.ConditionId,
                        principalTable: "ScriptActions",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "DirectoryEffectiveGroupMemberships",
                columns: table => new
                {
                    DirectorySourceId = table.Column<int>(type: "integer", nullable: false),
                    GroupGuid = table.Column<Guid>(type: "uuid", nullable: false),
                    MemberGuid = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DirectoryEffectiveGroupMemberships", x => new { x.DirectorySourceId, x.GroupGuid, x.MemberGuid });
                    table.ForeignKey(
                        name: "FK_DirectoryEffectiveGroupMemberships_DirectoryObjects_Directo~",
                        columns: x => new { x.DirectorySourceId, x.GroupGuid },
                        principalTable: "DirectoryObjects",
                        principalColumns: new[] { "DirectorySourceId", "ObjectGuid" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DirectoryEffectiveGroupMemberships_DirectoryObjects_Direct~1",
                        columns: x => new { x.DirectorySourceId, x.MemberGuid },
                        principalTable: "DirectoryObjects",
                        principalColumns: new[] { "DirectorySourceId", "ObjectGuid" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DirectoryEffectiveGroupMemberships_DirectorySources_Directo~",
                        column: x => x.DirectorySourceId,
                        principalTable: "DirectorySources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DirectoryGroupMemberships",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DirectorySourceId = table.Column<int>(type: "integer", nullable: false),
                    GroupGuid = table.Column<Guid>(type: "uuid", nullable: false),
                    MemberGuid = table.Column<Guid>(type: "uuid", nullable: true),
                    MemberDistinguishedName = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DirectoryGroupMemberships", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DirectoryGroupMemberships_DirectoryObjects_DirectorySourceI~",
                        columns: x => new { x.DirectorySourceId, x.GroupGuid },
                        principalTable: "DirectoryObjects",
                        principalColumns: new[] { "DirectorySourceId", "ObjectGuid" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DirectoryGroupMemberships_DirectoryObjects_DirectorySource~1",
                        columns: x => new { x.DirectorySourceId, x.MemberGuid },
                        principalTable: "DirectoryObjects",
                        principalColumns: new[] { "DirectorySourceId", "ObjectGuid" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DirectoryGroupMemberships_DirectorySources_DirectorySourceId",
                        column: x => x.DirectorySourceId,
                        principalTable: "DirectorySources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DomainJoinActions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Guid = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    DomainName = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    OrganizationalUnit = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    OuMode = table.Column<int>(type: "integer", nullable: false),
                    OuNamingRuleSetId = table.Column<int>(type: "integer", nullable: true),
                    ManagedCredentialId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DomainJoinActions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DomainJoinActions_ActionReferences_Guid",
                        column: x => x.Guid,
                        principalTable: "ActionReferences",
                        principalColumn: "ActionGuid",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DomainJoinActions_ManagedCredentials_ManagedCredentialId",
                        column: x => x.ManagedCredentialId,
                        principalTable: "ManagedCredentials",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DomainJoinActions_OuNamingRuleSets_OuNamingRuleSetId",
                        column: x => x.OuNamingRuleSetId,
                        principalTable: "OuNamingRuleSets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ImageProfiles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:IdentitySequenceOptions", "'2', '1', '', '', 'False', '1'")
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "citext", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    VisibleInWinPe = table.Column<bool>(type: "boolean", nullable: false),
                    OuNamingRuleSetId = table.Column<int>(type: "integer", nullable: true),
                    OuPrompt = table.Column<int>(type: "integer", nullable: false),
                    SetComputerName = table.Column<bool>(type: "boolean", nullable: false),
                    AfterDeployment = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    DriverMode = table.Column<int>(type: "integer", nullable: false),
                    DriverPackId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImageProfiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ImageProfiles_DriverProfiles_DriverPackId",
                        column: x => x.DriverPackId,
                        principalTable: "DriverProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ImageProfiles_OuNamingRuleSets_OuNamingRuleSetId",
                        column: x => x.OuNamingRuleSetId,
                        principalTable: "OuNamingRuleSets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ActionFiles",
                columns: table => new
                {
                    ActionGuid = table.Column<Guid>(type: "uuid", nullable: false),
                    StoredFileId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActionFiles", x => new { x.ActionGuid, x.StoredFileId });
                    table.ForeignKey(
                        name: "FK_ActionFiles_ActionReferences_ActionGuid",
                        column: x => x.ActionGuid,
                        principalTable: "ActionReferences",
                        principalColumn: "ActionGuid",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ActionFiles_StoredFiles_StoredFileId",
                        column: x => x.StoredFileId,
                        principalTable: "StoredFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DriverFiles",
                columns: table => new
                {
                    DriverProfileId = table.Column<int>(type: "integer", nullable: false),
                    StoredFileId = table.Column<int>(type: "integer", nullable: false),
                    CatalogVendor = table.Column<int>(type: "integer", nullable: true),
                    CatalogModel = table.Column<string>(type: "text", nullable: true),
                    CatalogOs = table.Column<string>(type: "text", nullable: true),
                    CatalogArchitecture = table.Column<string>(type: "text", nullable: true),
                    CatalogRelease = table.Column<string>(type: "text", nullable: true),
                    CatalogSource = table.Column<string>(type: "text", nullable: true),
                    FollowLatest = table.Column<bool>(type: "boolean", nullable: false),
                    PreparationStatus = table.Column<int>(type: "integer", nullable: false),
                    AttemptedSourceHash = table.Column<string>(type: "text", nullable: true),
                    PreparationError = table.Column<string>(type: "text", nullable: true),
                    PreparedStorageProviderId = table.Column<int>(type: "integer", nullable: true),
                    PreparedArchiveId = table.Column<Guid>(type: "uuid", nullable: true),
                    PreparedSourceHash = table.Column<string>(type: "text", nullable: true),
                    PreparedSha256 = table.Column<string>(type: "text", nullable: true),
                    PreparedInfCount = table.Column<int>(type: "integer", nullable: false),
                    PreparedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DriverFiles", x => new { x.DriverProfileId, x.StoredFileId });
                    table.ForeignKey(
                        name: "FK_DriverFiles_DriverProfiles_DriverProfileId",
                        column: x => x.DriverProfileId,
                        principalTable: "DriverProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DriverFiles_StorageProviders_PreparedStorageProviderId",
                        column: x => x.PreparedStorageProviderId,
                        principalTable: "StorageProviders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DriverFiles_StoredFiles_StoredFileId",
                        column: x => x.StoredFileId,
                        principalTable: "StoredFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SoftwareActions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Guid = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    Uninstall = table.Column<bool>(type: "boolean", nullable: false),
                    UninstallName = table.Column<string>(type: "text", nullable: true),
                    UninstallVersion = table.Column<string>(type: "text", nullable: true),
                    Name = table.Column<string>(type: "citext", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Arguments = table.Column<string>(type: "text", nullable: true),
                    Timeout = table.Column<int>(type: "integer", nullable: false),
                    RedirectStdOut = table.Column<bool>(type: "boolean", nullable: false),
                    RedirectStdError = table.Column<bool>(type: "boolean", nullable: false),
                    SuccessCodes = table.Column<string>(type: "text", nullable: false),
                    ManagedCredentialId = table.Column<int>(type: "integer", nullable: true),
                    MsiInstallType = table.Column<int>(type: "integer", nullable: false),
                    Command = table.Column<string>(type: "text", nullable: true),
                    InstallerFileId = table.Column<int>(type: "integer", nullable: true),
                    AdditionalArguments = table.Column<string>(type: "text", nullable: true),
                    Override = table.Column<string>(type: "text", nullable: true),
                    PackageId = table.Column<string>(type: "text", nullable: true),
                    PackageVersion = table.Column<string>(type: "text", nullable: true),
                    WinGetInstallType = table.Column<int>(type: "integer", nullable: false),
                    WinGetScope = table.Column<int>(type: "integer", nullable: false),
                    UseDownloadQueue = table.Column<bool>(type: "boolean", nullable: false),
                    KeepUpdated = table.Column<bool>(type: "boolean", nullable: false),
                    InstallLatest = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SoftwareActions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SoftwareActions_ActionReferences_Guid",
                        column: x => x.Guid,
                        principalTable: "ActionReferences",
                        principalColumn: "ActionGuid",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SoftwareActions_ManagedCredentials_ManagedCredentialId",
                        column: x => x.ManagedCredentialId,
                        principalTable: "ManagedCredentials",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_SoftwareActions_StoredFiles_InstallerFileId",
                        column: x => x.InstallerFileId,
                        principalTable: "StoredFiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "ActiveCompiledPolicies",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PolicyId = table.Column<int>(type: "integer", nullable: false),
                    Json = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActiveCompiledPolicies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ActiveCompiledPolicies_Policies_PolicyId",
                        column: x => x.PolicyId,
                        principalTable: "Policies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DirectoryOuPolicies",
                columns: table => new
                {
                    DirectorySourceId = table.Column<int>(type: "integer", nullable: false),
                    OuGuid = table.Column<Guid>(type: "uuid", nullable: false),
                    PolicyId = table.Column<int>(type: "integer", nullable: false),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    IncludeDescendants = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DirectoryOuPolicies", x => new { x.DirectorySourceId, x.OuGuid, x.PolicyId });
                    table.ForeignKey(
                        name: "FK_DirectoryOuPolicies_DirectoryObjects_DirectorySourceId_OuGu~",
                        columns: x => new { x.DirectorySourceId, x.OuGuid },
                        principalTable: "DirectoryObjects",
                        principalColumns: new[] { "DirectorySourceId", "ObjectGuid" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DirectoryOuPolicies_DirectorySources_DirectorySourceId",
                        column: x => x.DirectorySourceId,
                        principalTable: "DirectorySources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DirectoryOuPolicies_Policies_PolicyId",
                        column: x => x.PolicyId,
                        principalTable: "Policies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PolicyActions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PolicyId = table.Column<int>(type: "integer", nullable: false),
                    ActionGuid = table.Column<Guid>(type: "uuid", nullable: false),
                    ActionType = table.Column<int>(type: "integer", nullable: false),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    ConditionId = table.Column<int>(type: "integer", nullable: true),
                    ConditionFailedAction = table.Column<int>(type: "integer", nullable: false),
                    ConditionNextAction = table.Column<Guid>(type: "uuid", nullable: true),
                    ConditionNextActionReferenceActionGuid = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PolicyActions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PolicyActions_ActionReferences_ActionGuid",
                        column: x => x.ActionGuid,
                        principalTable: "ActionReferences",
                        principalColumn: "ActionGuid",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PolicyActions_ActionReferences_ConditionNextActionReference~",
                        column: x => x.ConditionNextActionReferenceActionGuid,
                        principalTable: "ActionReferences",
                        principalColumn: "ActionGuid");
                    table.ForeignKey(
                        name: "FK_PolicyActions_Policies_PolicyId",
                        column: x => x.PolicyId,
                        principalTable: "Policies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PolicyActions_ScriptActions_ConditionId",
                        column: x => x.ConditionId,
                        principalTable: "ScriptActions",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "PolicyHashHistories",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PolicyId = table.Column<int>(type: "integer", nullable: false),
                    Hash = table.Column<string>(type: "text", nullable: false),
                    Json = table.Column<string>(type: "text", nullable: false),
                    ModifiedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PolicyHashHistories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PolicyHashHistories_Policies_PolicyId",
                        column: x => x.PolicyId,
                        principalTable: "Policies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PolicyTags",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PolicyId = table.Column<int>(type: "integer", nullable: false),
                    TagId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PolicyTags", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PolicyTags_Policies_PolicyId",
                        column: x => x.PolicyId,
                        principalTable: "Policies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PolicyTags_Tags_TagId",
                        column: x => x.TagId,
                        principalTable: "Tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PolicyUserAccess",
                columns: table => new
                {
                    PolicyId = table.Column<int>(type: "integer", nullable: false),
                    LoginAudienceId = table.Column<int>(type: "integer", nullable: true),
                    SelfServiceAudienceId = table.Column<int>(type: "integer", nullable: true),
                    SelfServiceEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    AnyInteractiveUser = table.Column<bool>(type: "boolean", nullable: false),
                    RestrictSelfServiceToDeviceTargets = table.Column<bool>(type: "boolean", nullable: false),
                    CategoryId = table.Column<int>(type: "integer", nullable: true),
                    DisplayName = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Icon = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: true),
                    IconPng = table.Column<byte[]>(type: "bytea", nullable: true),
                    IconHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Revision = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PolicyUserAccess", x => x.PolicyId);
                    table.ForeignKey(
                        name: "FK_PolicyUserAccess_Policies_PolicyId",
                        column: x => x.PolicyId,
                        principalTable: "Policies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PolicyUserAccess_SelfServiceCategory_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "SelfServiceCategory",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_PolicyUserAccess_UserAudience_LoginAudienceId",
                        column: x => x.LoginAudienceId,
                        principalTable: "UserAudience",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_PolicyUserAccess_UserAudience_SelfServiceAudienceId",
                        column: x => x.SelfServiceAudienceId,
                        principalTable: "UserAudience",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AutomaticDeploymentRule",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    Manufacturer = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Model = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ManufacturerRegex = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ModelRegex = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ImageId = table.Column<int>(type: "integer", nullable: true),
                    DeploymentProfileId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutomaticDeploymentRule", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AutomaticDeploymentRule_ImageProfiles_DeploymentProfileId",
                        column: x => x.DeploymentProfileId,
                        principalTable: "ImageProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AutomaticDeploymentRule_Images_ImageId",
                        column: x => x.ImageId,
                        principalTable: "Images",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DeploymentDriverRules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DeploymentProfileId = table.Column<int>(type: "integer", nullable: false),
                    DriverPackId = table.Column<int>(type: "integer", nullable: false),
                    Manufacturer = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Model = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ManufacturerRegex = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    ModelRegex = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeploymentDriverRules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeploymentDriverRules_DriverProfiles_DriverPackId",
                        column: x => x.DriverPackId,
                        principalTable: "DriverProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DeploymentDriverRules_ImageProfiles_DeploymentProfileId",
                        column: x => x.DeploymentProfileId,
                        principalTable: "ImageProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DeploymentPolicies",
                columns: table => new
                {
                    DeploymentProfileId = table.Column<int>(type: "integer", nullable: false),
                    PolicyId = table.Column<int>(type: "integer", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeploymentPolicies", x => new { x.DeploymentProfileId, x.PolicyId });
                    table.ForeignKey(
                        name: "FK_DeploymentPolicies_ImageProfiles_DeploymentProfileId",
                        column: x => x.DeploymentProfileId,
                        principalTable: "ImageProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DeploymentPolicies_Policies_PolicyId",
                        column: x => x.PolicyId,
                        principalTable: "Policies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DeviceGroups",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:IdentitySequenceOptions", "'2', '1', '', '', 'False', '1'")
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    QueryDefinitionJson = table.Column<string>(type: "text", nullable: true),
                    MembershipEvaluatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ImageId = table.Column<int>(type: "integer", nullable: true),
                    DeploymentProfileId = table.Column<int>(type: "integer", nullable: true),
                    IsHidden = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceGroups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeviceGroups_ImageProfiles_DeploymentProfileId",
                        column: x => x.DeploymentProfileId,
                        principalTable: "ImageProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_DeviceGroups_Images_ImageId",
                        column: x => x.ImageId,
                        principalTable: "Images",
                        principalColumn: "Id");
                });

            migrationBuilder.CreateTable(
                name: "Devices",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Guid = table.Column<Guid>(type: "uuid", nullable: false),
                    ProvisionStatus = table.Column<int>(type: "integer", nullable: false),
                    InstallationId = table.Column<string>(type: "text", nullable: true),
                    ProvisionedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastCheckinAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastInventoryAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ArchivedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastKnownIp = table.Column<string>(type: "text", nullable: true),
                    ClientVersion = table.Column<string>(type: "text", nullable: true),
                    AgentPlatform = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "Windows"),
                    ImagingMac = table.Column<string>(type: "text", nullable: true),
                    UUID = table.Column<string>(type: "text", nullable: true),
                    LastSocketResult = table.Column<string>(type: "text", nullable: true),
                    DirectorySourceId = table.Column<int>(type: "integer", nullable: true),
                    DirectoryObjectGuid = table.Column<Guid>(type: "uuid", nullable: true),
                    ImageId = table.Column<int>(type: "integer", nullable: true),
                    DeploymentProfileId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Devices", x => x.Id);
                    table.CheckConstraint("CK_Devices_DirectoryLink", "(\"DirectorySourceId\" IS NULL) = (\"DirectoryObjectGuid\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_Devices_DirectoryObjects_DirectorySourceId_DirectoryObjectG~",
                        columns: x => new { x.DirectorySourceId, x.DirectoryObjectGuid },
                        principalTable: "DirectoryObjects",
                        principalColumns: new[] { "DirectorySourceId", "ObjectGuid" },
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Devices_ImageProfiles_DeploymentProfileId",
                        column: x => x.DeploymentProfileId,
                        principalTable: "ImageProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Devices_Images_ImageId",
                        column: x => x.ImageId,
                        principalTable: "Images",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "MulticastSession",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ImageVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeploymentProfileId = table.Column<int>(type: "integer", nullable: false),
                    StorageProviderId = table.Column<int>(type: "integer", nullable: false),
                    SiteServiceId = table.Column<Guid>(type: "uuid", nullable: true),
                    GroupId = table.Column<int>(type: "integer", nullable: true),
                    CreatedBy = table.Column<int>(type: "integer", nullable: false),
                    ExpectedReceivers = table.Column<int>(type: "integer", nullable: false),
                    SenderAddress = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: false),
                    GroupAddress = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: false),
                    Port = table.Column<int>(type: "integer", nullable: false),
                    MegabitsPerSecond = table.Column<int>(type: "integer", nullable: false),
                    Ttl = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    StartRequested = table.Column<bool>(type: "boolean", nullable: false),
                    SenderLease = table.Column<Guid>(type: "uuid", nullable: true),
                    SenderLeaseExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FinishedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Error = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MulticastSession", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MulticastSession_ImageProfiles_DeploymentProfileId",
                        column: x => x.DeploymentProfileId,
                        principalTable: "ImageProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_MulticastSession_StorageProviders_StorageProviderId",
                        column: x => x.StorageProviderId,
                        principalTable: "StorageProviders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DeviceGroupPowerSchedules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DeviceGroupId = table.Column<int>(type: "integer", nullable: false),
                    ScheduleId = table.Column<int>(type: "integer", nullable: false),
                    Action = table.Column<int>(type: "integer", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    ConfiguredByUserId = table.Column<int>(type: "integer", nullable: false),
                    LastEvaluatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastRunAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastResult = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceGroupPowerSchedules", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeviceGroupPowerSchedules_AppUsers_ConfiguredByUserId",
                        column: x => x.ConfiguredByUserId,
                        principalTable: "AppUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DeviceGroupPowerSchedules_DeviceGroups_DeviceGroupId",
                        column: x => x.DeviceGroupId,
                        principalTable: "DeviceGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DeviceGroupPowerSchedules_Schedules_ScheduleId",
                        column: x => x.ScheduleId,
                        principalTable: "Schedules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DeviceGroupTags",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DeviceGroupId = table.Column<int>(type: "integer", nullable: false),
                    TagId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceGroupTags", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeviceGroupTags_DeviceGroups_DeviceGroupId",
                        column: x => x.DeviceGroupId,
                        principalTable: "DeviceGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DeviceGroupTags_Tags_TagId",
                        column: x => x.TagId,
                        principalTable: "Tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "GroupPolicies",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DeviceGroupId = table.Column<int>(type: "integer", nullable: false),
                    PolicyId = table.Column<int>(type: "integer", nullable: false),
                    PolicyOrder = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupPolicies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_GroupPolicies_DeviceGroups_DeviceGroupId",
                        column: x => x.DeviceGroupId,
                        principalTable: "DeviceGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_GroupPolicies_Policies_PolicyId",
                        column: x => x.PolicyId,
                        principalTable: "Policies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AgentCertificates",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<int>(type: "integer", nullable: false),
                    IssuerId = table.Column<int>(type: "integer", nullable: false),
                    CertificateDer = table.Column<byte[]>(type: "bytea", nullable: false),
                    Sha256Fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    NotBeforeUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    NotAfterUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RevokedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RenewedFromId = table.Column<Guid>(type: "uuid", nullable: true),
                    RenewalConfirmedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RetireAfterUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentCertificates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentCertificates_AppCertificates_IssuerId",
                        column: x => x.IssuerId,
                        principalTable: "AppCertificates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_AgentCertificates_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AgentDownloadLease",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<int>(type: "integer", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentDownloadLease", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentDownloadLease_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AgentPolicyRun",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<int>(type: "integer", nullable: false),
                    PolicyId = table.Column<int>(type: "integer", nullable: false),
                    Hash = table.Column<string>(type: "text", nullable: false),
                    SnapshotJson = table.Column<string>(type: "text", nullable: false),
                    UserName = table.Column<string>(type: "text", nullable: true),
                    UserSid = table.Column<string>(type: "text", nullable: true),
                    SelfService = table.Column<bool>(type: "boolean", nullable: false),
                    ManagedWinGetActionsJson = table.Column<string>(type: "text", nullable: true),
                    StartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FinishedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Result = table.Column<int>(type: "integer", nullable: true),
                    Detail = table.Column<string>(type: "character varying(16000)", maxLength: 16000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentPolicyRun", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentPolicyRun_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AgentPolicyRun_Policies_PolicyId",
                        column: x => x.PolicyId,
                        principalTable: "Policies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DeviceActivityReports",
                columns: table => new
                {
                    DeviceId = table.Column<int>(type: "integer", nullable: false),
                    ConnectionId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    RunId = table.Column<Guid>(type: "uuid", nullable: true),
                    Name = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    ChangedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceActivityReports", x => x.DeviceId);
                    table.ForeignKey(
                        name: "FK_DeviceActivityReports_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DeviceAntiVirus",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DeviceId = table.Column<int>(type: "integer", nullable: false),
                    DisplayName = table.Column<string>(type: "text", nullable: true),
                    Provider = table.Column<string>(type: "text", nullable: true),
                    RealtimeScanner = table.Column<string>(type: "text", nullable: true),
                    DefinitionStatus = table.Column<string>(type: "text", nullable: true),
                    ProductState = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceAntiVirus", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeviceAntiVirus_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DeviceAttachments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DeviceId = table.Column<int>(type: "integer", nullable: false),
                    AttachmentId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceAttachments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeviceAttachments_Attachments_AttachmentId",
                        column: x => x.AttachmentId,
                        principalTable: "Attachments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DeviceAttachments_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DeviceBios",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DeviceId = table.Column<int>(type: "integer", nullable: false),
                    SerialNumber = table.Column<string>(type: "text", nullable: true),
                    Version = table.Column<string>(type: "text", nullable: true),
                    SMBIOSBIOSVersion = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceBios", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeviceBios_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DeviceBitLocker",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DeviceId = table.Column<int>(type: "integer", nullable: false),
                    DriveLetter = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceBitLocker", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeviceBitLocker_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DeviceCertificates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DeviceId = table.Column<int>(type: "integer", nullable: false),
                    CertificateId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceCertificates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeviceCertificates_Certificates_CertificateId",
                        column: x => x.CertificateId,
                        principalTable: "Certificates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DeviceCertificates_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DeviceComments",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DeviceId = table.Column<int>(type: "integer", nullable: false),
                    CommentId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceComments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeviceComments_Comments_CommentId",
                        column: x => x.CommentId,
                        principalTable: "Comments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DeviceComments_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DeviceComputerSystems",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DeviceId = table.Column<int>(type: "integer", nullable: false),
                    Manufacturer = table.Column<string>(type: "text", nullable: true),
                    Model = table.Column<string>(type: "text", nullable: true),
                    Name = table.Column<string>(type: "text", nullable: true),
                    Domain = table.Column<string>(type: "text", nullable: true),
                    Workgroup = table.Column<string>(type: "text", nullable: true),
                    Memory = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceComputerSystems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeviceComputerSystems_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DeviceCustomAttributes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DeviceId = table.Column<int>(type: "integer", nullable: false),
                    CustomAttributeId = table.Column<int>(type: "integer", nullable: false),
                    Value = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceCustomAttributes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeviceCustomAttributes_CustomAttributes_CustomAttributeId",
                        column: x => x.CustomAttributeId,
                        principalTable: "CustomAttributes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DeviceCustomAttributes_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DeviceCustomInventory",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DeviceId = table.Column<int>(type: "integer", nullable: false),
                    ScriptActionId = table.Column<int>(type: "integer", nullable: false),
                    Value = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceCustomInventory", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeviceCustomInventory_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DeviceCustomInventory_ScriptActions_ScriptActionId",
                        column: x => x.ScriptActionId,
                        principalTable: "ScriptActions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DeviceFirewalls",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DeviceId = table.Column<int>(type: "integer", nullable: false),
                    DomainEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    PrivateEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    PublicEnabled = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceFirewalls", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeviceFirewalls_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DeviceGpus",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DeviceId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: true),
                    Memory = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceGpus", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeviceGpus_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DeviceGroupMemberships",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DeviceGroupId = table.Column<int>(type: "integer", nullable: false),
                    DeviceId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceGroupMemberships", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeviceGroupMemberships_DeviceGroups_DeviceGroupId",
                        column: x => x.DeviceGroupId,
                        principalTable: "DeviceGroups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DeviceGroupMemberships_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DeviceHardDrives",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DeviceId = table.Column<int>(type: "integer", nullable: false),
                    Model = table.Column<string>(type: "text", nullable: true),
                    FirmwareRevision = table.Column<string>(type: "text", nullable: true),
                    SerialNumber = table.Column<string>(type: "text", nullable: true),
                    SizeMb = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceHardDrives", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeviceHardDrives_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DeviceImagingLogs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DeviceId = table.Column<int>(type: "integer", nullable: false),
                    Contents = table.Column<string>(type: "text", nullable: false),
                    LoggedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Mac = table.Column<string>(type: "text", nullable: true),
                    SubType = table.Column<string>(type: "text", nullable: true),
                    Type = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceImagingLogs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeviceImagingLogs_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DeviceLogicalVolumes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DeviceId = table.Column<int>(type: "integer", nullable: false),
                    Drive = table.Column<string>(type: "text", nullable: false),
                    FreeSpaceGB = table.Column<int>(type: "integer", nullable: false),
                    FreeSpacePercent = table.Column<int>(type: "integer", nullable: false),
                    SizeGB = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceLogicalVolumes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeviceLogicalVolumes_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DeviceLogins",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DeviceId = table.Column<int>(type: "integer", nullable: false),
                    UserName = table.Column<string>(type: "text", nullable: false),
                    LoggedInAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LoggedOutAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ClientDatabaseLoginId = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceLogins", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeviceLogins_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DeviceNics",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    IpNetworks = table.Column<string>(type: "text", nullable: true),
                    DeviceId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: true),
                    Description = table.Column<string>(type: "text", nullable: true),
                    Type = table.Column<string>(type: "text", nullable: true),
                    Mac = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<string>(type: "text", nullable: true),
                    Speed = table.Column<long>(type: "bigint", nullable: false),
                    Ips = table.Column<string>(type: "text", nullable: true),
                    Gateways = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceNics", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeviceNics_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DeviceNotificationReceipts",
                columns: table => new
                {
                    DeviceId = table.Column<int>(type: "integer", nullable: false),
                    NotificationId = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    UserSid = table.Column<string>(type: "character varying(184)", maxLength: 184, nullable: false),
                    UserKey = table.Column<string>(type: "character varying(220)", maxLength: 220, nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    UserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Title = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Body = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    DeliveredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AcknowledgedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceNotificationReceipts", x => new { x.DeviceId, x.NotificationId, x.UserSid });
                    table.ForeignKey(
                        name: "FK_DeviceNotificationReceipts_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DeviceOs",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DeviceId = table.Column<int>(type: "integer", nullable: false),
                    Caption = table.Column<string>(type: "text", nullable: true),
                    Version = table.Column<string>(type: "text", nullable: true),
                    BuildNumber = table.Column<string>(type: "text", nullable: true),
                    OSArchitecture = table.Column<string>(type: "text", nullable: true),
                    NativeArchitecture = table.Column<string>(type: "text", nullable: true),
                    ReleaseId = table.Column<string>(type: "text", nullable: true),
                    UacStatus = table.Column<string>(type: "text", nullable: true),
                    LocalTimeZone = table.Column<string>(type: "text", nullable: true),
                    Latitude = table.Column<string>(type: "text", nullable: true),
                    Longitude = table.Column<string>(type: "text", nullable: true),
                    LocationEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    LastLocationUpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdateServer = table.Column<string>(type: "text", nullable: true),
                    SUStargetGroup = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceOs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeviceOs_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DevicePolicies",
                columns: table => new
                {
                    DeviceId = table.Column<int>(type: "integer", nullable: false),
                    PolicyId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DevicePolicies", x => new { x.DeviceId, x.PolicyId });
                    table.ForeignKey(
                        name: "FK_DevicePolicies_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DevicePolicies_Policies_PolicyId",
                        column: x => x.PolicyId,
                        principalTable: "Policies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DevicePrinters",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DeviceId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: true),
                    DriverName = table.Column<string>(type: "text", nullable: true),
                    Local = table.Column<bool>(type: "boolean", nullable: false),
                    Network = table.Column<bool>(type: "boolean", nullable: false),
                    ShareName = table.Column<string>(type: "text", nullable: true),
                    SystemName = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DevicePrinters", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DevicePrinters_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DeviceProcessors",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DeviceId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: true),
                    Speed = table.Column<int>(type: "integer", nullable: false),
                    Cores = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceProcessors", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeviceProcessors_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DeviceSoftware",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DeviceId = table.Column<int>(type: "integer", nullable: false),
                    SoftwareId = table.Column<int>(type: "integer", nullable: false),
                    UninstallString = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceSoftware", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeviceSoftware_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DeviceSoftware_Software_SoftwareId",
                        column: x => x.SoftwareId,
                        principalTable: "Software",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DeviceTags",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DeviceId = table.Column<int>(type: "integer", nullable: false),
                    TagId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceTags", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeviceTags_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DeviceTags_Tags_TagId",
                        column: x => x.TagId,
                        principalTable: "Tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DeviceWindowsUpdates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DeviceId = table.Column<int>(type: "integer", nullable: false),
                    WindowsUpdateId = table.Column<int>(type: "integer", nullable: false),
                    LastDeploymentChangedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsInstalled = table.Column<bool>(type: "boolean", nullable: false),
                    RevisionNumber = table.Column<int>(type: "integer", nullable: true),
                    IsDownloaded = table.Column<bool>(type: "boolean", nullable: true),
                    IsHidden = table.Column<bool>(type: "boolean", nullable: true),
                    BrowseOnly = table.Column<bool>(type: "boolean", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceWindowsUpdates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeviceWindowsUpdates_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DeviceWindowsUpdates_WindowsUpdates_WindowsUpdateId",
                        column: x => x.WindowsUpdateId,
                        principalTable: "WindowsUpdates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "DeviceWindowsUpdateScan",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ConfigurationJson = table.Column<string>(type: "jsonb", nullable: true),
                    DeviceId = table.Column<int>(type: "integer", nullable: false),
                    CollectedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    IsOnline = table.Column<bool>(type: "boolean", nullable: false),
                    Succeeded = table.Column<bool>(type: "boolean", nullable: false),
                    ResultCode = table.Column<int>(type: "integer", nullable: true),
                    ErrorCode = table.Column<int>(type: "integer", nullable: true),
                    SourceName = table.Column<string>(type: "text", nullable: true),
                    SourceId = table.Column<string>(type: "text", nullable: true),
                    LastSuccessfulSearchAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    LastSuccessfulOnlineScanAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RebootRequired = table.Column<bool>(type: "boolean", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DeviceWindowsUpdateScan", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DeviceWindowsUpdateScan_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ImageUploads",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ImageId = table.Column<int>(type: "integer", nullable: false),
                    Format = table.Column<int>(type: "integer", nullable: false),
                    StorageProviderId = table.Column<int>(type: "integer", nullable: false),
                    UserId = table.Column<int>(type: "integer", nullable: false),
                    DeviceId = table.Column<int>(type: "integer", nullable: true),
                    DeviceName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    ExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CaptureTokenHash = table.Column<string>(type: "text", nullable: true),
                    CaptureBootPlanJson = table.Column<string>(type: "text", nullable: true),
                    CaptureUploadProtected = table.Column<string>(type: "text", nullable: true),
                    CaptureReady = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImageUploads", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ImageUploads_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_ImageUploads_Images_ImageId",
                        column: x => x.ImageId,
                        principalTable: "Images",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ImageUploads_StorageProviders_StorageProviderId",
                        column: x => x.StorageProviderId,
                        principalTable: "StorageProviders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PolicyHistories",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PolicyId = table.Column<int>(type: "integer", nullable: false),
                    DeviceId = table.Column<int>(type: "integer", nullable: false),
                    Result = table.Column<int>(type: "integer", nullable: false),
                    Hash = table.Column<string>(type: "text", nullable: false),
                    LastRanAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UserName = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PolicyHistories", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PolicyHistories_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PolicyHistories_Policies_PolicyId",
                        column: x => x.PolicyId,
                        principalTable: "Policies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ImagingDeployment",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceId = table.Column<int>(type: "integer", nullable: false),
                    ImageVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    MulticastSessionId = table.Column<Guid>(type: "uuid", nullable: true),
                    DeploymentProfileId = table.Column<int>(type: "integer", nullable: false),
                    AgentBootPlanJson = table.Column<string>(type: "text", nullable: true),
                    AgentBootReady = table.Column<bool>(type: "boolean", nullable: false),
                    SettingsJson = table.Column<string>(type: "text", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ImagingCompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CancelledAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SetupCompletedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    WindowsSetupReadyAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    SetupReadinessMessage = table.Column<string>(type: "text", nullable: true),
                    ImagerStartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ImagerFailedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ImagerClaimId = table.Column<Guid>(type: "uuid", nullable: true),
                    ImagerClaimKeyHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ImagerClaimExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImagingDeployment", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ImagingDeployment_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ImagingDeployment_ImageProfiles_DeploymentProfileId",
                        column: x => x.DeploymentProfileId,
                        principalTable: "ImageProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ImagingDeployment_MulticastSession_MulticastSessionId",
                        column: x => x.MulticastSessionId,
                        principalTable: "MulticastSession",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "AgentProvisioningRequests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InstallationId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeviceName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    PublicKeyHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    IpAddress = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    HardwareUuid = table.Column<string>(type: "text", nullable: true),
                    MacAddresses = table.Column<string[]>(type: "text[]", nullable: false),
                    RequestedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSeenAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    ApprovedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ApprovedByUserId = table.Column<int>(type: "integer", nullable: true),
                    TargetDeviceId = table.Column<int>(type: "integer", nullable: true),
                    AgentCertificateId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AgentProvisioningRequests", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AgentProvisioningRequests_AgentCertificates_AgentCertificat~",
                        column: x => x.AgentCertificateId,
                        principalTable: "AgentCertificates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_AgentProvisioningRequests_Devices_TargetDeviceId",
                        column: x => x.TargetDeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "SocketConnections",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DeviceId = table.Column<int>(type: "integer", nullable: false),
                    ConnectionId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ServerInstanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    AgentCertificateId = table.Column<Guid>(type: "uuid", nullable: true),
                    ConnectedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastSeenAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SocketConnections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SocketConnections_AgentCertificates_AgentCertificateId",
                        column: x => x.AgentCertificateId,
                        principalTable: "AgentCertificates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_SocketConnections_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ActiveImagingTasks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UploadId = table.Column<Guid>(type: "uuid", nullable: true),
                    DeploymentId = table.Column<Guid>(type: "uuid", nullable: true),
                    ImageName = table.Column<string>(type: "text", nullable: true),
                    Phase = table.Column<int>(type: "integer", nullable: false),
                    ProgressPercent = table.Column<double>(type: "double precision", nullable: true),
                    BytesTransferred = table.Column<long>(type: "bigint", nullable: false),
                    StartedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    FinishedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    StatusMessage = table.Column<string>(type: "text", nullable: true),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true),
                    DeviceId = table.Column<int>(type: "integer", nullable: false),
                    QueuePosition = table.Column<int>(type: "integer", nullable: false),
                    Rate = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Type = table.Column<string>(type: "text", nullable: true),
                    AppUserId = table.Column<int>(type: "integer", nullable: true),
                    LastUpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ActiveImagingTasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ActiveImagingTasks_AppUsers_AppUserId",
                        column: x => x.AppUserId,
                        principalTable: "AppUsers",
                        principalColumn: "Id");
                    table.ForeignKey(
                        name: "FK_ActiveImagingTasks_Devices_DeviceId",
                        column: x => x.DeviceId,
                        principalTable: "Devices",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ActiveImagingTasks_ImagingDeployment_DeploymentId",
                        column: x => x.DeploymentId,
                        principalTable: "ImagingDeployment",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ImagingDeploymentPolicy",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ImagingDeploymentId = table.Column<Guid>(type: "uuid", nullable: false),
                    PolicyId = table.Column<int>(type: "integer", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    Hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SnapshotJson = table.Column<string>(type: "text", nullable: false),
                    AgentPolicyRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    FinishedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    Result = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImagingDeploymentPolicy", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ImagingDeploymentPolicy_ImagingDeployment_ImagingDeployment~",
                        column: x => x.ImagingDeploymentId,
                        principalTable: "ImagingDeployment",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_ImagingDeploymentPolicy_Policies_PolicyId",
                        column: x => x.PolicyId,
                        principalTable: "Policies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WinPeBuilds",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DefinitionId = table.Column<int>(type: "integer", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "text", nullable: false),
                    RequestJson = table.Column<string>(type: "text", nullable: false),
                    StorageProviderId = table.Column<int>(type: "integer", nullable: true),
                    StoredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    StoredArtifactsJson = table.Column<string>(type: "text", nullable: false),
                    StorageError = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WinPeBuilds", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WinPeBuilds_StorageProviders_StorageProviderId",
                        column: x => x.StorageProviderId,
                        principalTable: "StorageProviders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WinPeDefinitions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "citext", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: true),
                    IsBase = table.Column<bool>(type: "boolean", nullable: false),
                    IsDefault = table.Column<bool>(type: "boolean", nullable: false),
                    IsArm64Default = table.Column<bool>(type: "boolean", nullable: false),
                    NativePxe = table.Column<bool>(type: "boolean", nullable: false),
                    PublishedBuildId = table.Column<Guid>(type: "uuid", nullable: true),
                    IncludePowerShell = table.Column<bool>(type: "boolean", nullable: false),
                    UseBootEx = table.Column<bool>(type: "boolean", nullable: false),
                    KeyboardLayout = table.Column<string>(type: "character varying(13)", maxLength: 13, nullable: false, defaultValue: "0409:00000409"),
                    DriverPackIdsJson = table.Column<string>(type: "text", nullable: false),
                    ModelRulesJson = table.Column<string>(type: "text", nullable: false, defaultValue: "[]"),
                    UpdatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WinPeDefinitions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WinPeDefinitions_WinPeBuilds_PublishedBuildId",
                        column: x => x.PublishedBuildId,
                        principalTable: "WinPeBuilds",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "DeviceGroups",
                columns: new[] { "Id", "DeploymentProfileId", "Description", "ImageId", "IsHidden", "MembershipEvaluatedAtUtc", "Name", "QueryDefinitionJson", "Type" },
                values: new object[] { 1, null, "All enrolled, non-archived devices. Membership is automatic.", null, false, null, "All devices", null, 2 });

            migrationBuilder.InsertData(
                table: "HelpQueue",
                columns: new[] { "Id", "EmailEnabled", "Name" },
                values: new object[] { -1, false, "Unassigned" });

            migrationBuilder.InsertData(
                table: "ImageProfiles",
                columns: new[] { "Id", "AfterDeployment", "Description", "DriverMode", "DriverPackId", "Enabled", "Name", "OuNamingRuleSetId", "OuPrompt", "SetComputerName", "VisibleInWinPe" },
                values: new object[] { 1, 1, null, 0, null, true, "Default", null, 0, true, true });

            migrationBuilder.InsertData(
                table: "ScheduledTask",
                columns: new[] { "Id", "IsEnabled", "LastEvaluatedAtUtc", "ScheduleId" },
                values: new object[,]
                {
                    { 1, false, null, null },
                    { 2, false, null, null }
                });

            migrationBuilder.InsertData(
                table: "SelfServiceCategory",
                columns: new[] { "Id", "Icon", "Name", "Revision", "SortOrder" },
                values: new object[,]
                {
                    { -3, "Maintenance", "Maintenance", new Guid("00000000-0000-0000-0000-000000000000"), 30 },
                    { -2, "Printers", "Printers", new Guid("00000000-0000-0000-0000-000000000000"), 20 },
                    { -1, "Software", "Software", new Guid("00000000-0000-0000-0000-000000000000"), 10 }
                });

            migrationBuilder.CreateIndex(
                name: "IX_ActionDeployments_ActionGuid",
                table: "ActionDeployments",
                column: "ActionGuid");

            migrationBuilder.CreateIndex(
                name: "IX_ActionDeployments_CreatedAtUtc",
                table: "ActionDeployments",
                column: "CreatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_ActionFiles_StoredFileId",
                table: "ActionFiles",
                column: "StoredFileId");

            migrationBuilder.CreateIndex(
                name: "IX_ActionReferences_ExternalFileUpdateScheduleId",
                table: "ActionReferences",
                column: "ExternalFileUpdateScheduleId");

            migrationBuilder.CreateIndex(
                name: "IX_ActionTags_ActionGuid",
                table: "ActionTags",
                column: "ActionGuid");

            migrationBuilder.CreateIndex(
                name: "IX_ActionTags_TagId",
                table: "ActionTags",
                column: "TagId");

            migrationBuilder.CreateIndex(
                name: "IX_ActiveCompiledPolicies_PolicyId",
                table: "ActiveCompiledPolicies",
                column: "PolicyId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ActiveImagingTasks_AppUserId",
                table: "ActiveImagingTasks",
                column: "AppUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ActiveImagingTasks_DeploymentId",
                table: "ActiveImagingTasks",
                column: "DeploymentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ActiveImagingTasks_DeviceId",
                table: "ActiveImagingTasks",
                column: "DeviceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ActiveImagingTasks_UploadId",
                table: "ActiveImagingTasks",
                column: "UploadId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentCertificates_DeviceId",
                table: "AgentCertificates",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentCertificates_IssuerId",
                table: "AgentCertificates",
                column: "IssuerId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentCertificates_RenewedFromId",
                table: "AgentCertificates",
                column: "RenewedFromId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentCertificates_Sha256Fingerprint",
                table: "AgentCertificates",
                column: "Sha256Fingerprint",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentDownloadLease_DeviceId",
                table: "AgentDownloadLease",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentDownloadLease_ExpiresAtUtc",
                table: "AgentDownloadLease",
                column: "ExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_AgentInventoryLease_DeviceId",
                table: "AgentInventoryLease",
                column: "DeviceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentInventoryLease_ExpiresAtUtc",
                table: "AgentInventoryLease",
                column: "ExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_AgentPolicyRun_DeviceId_PolicyId_StartedAtUtc",
                table: "AgentPolicyRun",
                columns: new[] { "DeviceId", "PolicyId", "StartedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_AgentPolicyRun_PolicyId",
                table: "AgentPolicyRun",
                column: "PolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentPortalSession_DeviceGuid_LocalUser",
                table: "AgentPortalSession",
                columns: new[] { "DeviceGuid", "LocalUser" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentPortalSession_ExpiresAtUtc",
                table: "AgentPortalSession",
                column: "ExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_AgentProvisioningRequests_AgentCertificateId",
                table: "AgentProvisioningRequests",
                column: "AgentCertificateId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentProvisioningRequests_InstallationId",
                table: "AgentProvisioningRequests",
                column: "InstallationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AgentProvisioningRequests_TargetDeviceId",
                table: "AgentProvisioningRequests",
                column: "TargetDeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_AgentRequestNonces_ExpiresAtUtc",
                table: "AgentRequestNonces",
                column: "ExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_AppRoleClaims_RoleId",
                table: "AppRoleClaims",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "RoleNameIndex",
                table: "AppRoles",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AppUserClaims_UserId",
                table: "AppUserClaims",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AppUserGridColumns_AppUserId_GridKey_ColumnKey",
                table: "AppUserGridColumns",
                columns: new[] { "AppUserId", "GridKey", "ColumnKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AppUserGroupDirectoryMapping_DirectorySourceId",
                table: "AppUserGroupDirectoryMapping",
                column: "DirectorySourceId");

            migrationBuilder.CreateIndex(
                name: "IX_AppUserGroupMemberships_AppUserGroupId",
                table: "AppUserGroupMemberships",
                column: "AppUserGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_AppUserGroupMemberships_AppUserId",
                table: "AppUserGroupMemberships",
                column: "AppUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AppUserGroupRights_AppUserGroupId",
                table: "AppUserGroupRights",
                column: "AppUserGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_AppUserLogins_UserId",
                table: "AppUserLogins",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AppUserPreferences_AppUserId",
                table: "AppUserPreferences",
                column: "AppUserId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AppUserRights_AppUserId",
                table: "AppUserRights",
                column: "AppUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AppUserRoles_RoleId",
                table: "AppUserRoles",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                table: "AppUsers",
                column: "NormalizedEmail");

            migrationBuilder.CreateIndex(
                name: "UserNameIndex",
                table: "AppUsers",
                column: "NormalizedUserName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditLogs_AppUserId",
                table: "AuditLogs",
                column: "AppUserId");

            migrationBuilder.CreateIndex(
                name: "IX_AutomaticDeploymentRule_DeploymentProfileId",
                table: "AutomaticDeploymentRule",
                column: "DeploymentProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_AutomaticDeploymentRule_ImageId",
                table: "AutomaticDeploymentRule",
                column: "ImageId");

            migrationBuilder.CreateIndex(
                name: "IX_Certificates_Thumbprint_Store",
                table: "Certificates",
                columns: new[] { "Thumbprint", "Store" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CommandActions_Guid",
                table: "CommandActions",
                column: "Guid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CommandActions_ManagedCredentialId",
                table: "CommandActions",
                column: "ManagedCredentialId");

            migrationBuilder.CreateIndex(
                name: "IX_CustomAttributes_Name",
                table: "CustomAttributes",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeploymentDriverRules_DeploymentProfileId",
                table: "DeploymentDriverRules",
                column: "DeploymentProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_DeploymentDriverRules_DriverPackId",
                table: "DeploymentDriverRules",
                column: "DriverPackId");

            migrationBuilder.CreateIndex(
                name: "IX_DeploymentPolicies_DeploymentProfileId_Position",
                table: "DeploymentPolicies",
                columns: new[] { "DeploymentProfileId", "Position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeploymentPolicies_PolicyId",
                table: "DeploymentPolicies",
                column: "PolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceAntiVirus_DeviceId",
                table: "DeviceAntiVirus",
                column: "DeviceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeviceAttachments_AttachmentId",
                table: "DeviceAttachments",
                column: "AttachmentId");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceAttachments_DeviceId",
                table: "DeviceAttachments",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceBios_DeviceId",
                table: "DeviceBios",
                column: "DeviceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeviceBitLocker_DeviceId",
                table: "DeviceBitLocker",
                column: "DeviceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeviceCertificates_CertificateId",
                table: "DeviceCertificates",
                column: "CertificateId");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceCertificates_DeviceId",
                table: "DeviceCertificates",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceCheckinLeases_DeviceGuid",
                table: "DeviceCheckinLeases",
                column: "DeviceGuid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeviceCheckinLeases_ExpiresAtUtc",
                table: "DeviceCheckinLeases",
                column: "ExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceComments_CommentId",
                table: "DeviceComments",
                column: "CommentId");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceComments_DeviceId",
                table: "DeviceComments",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceComputerSystems_DeviceId",
                table: "DeviceComputerSystems",
                column: "DeviceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeviceCustomAttributes_CustomAttributeId",
                table: "DeviceCustomAttributes",
                column: "CustomAttributeId");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceCustomAttributes_DeviceId",
                table: "DeviceCustomAttributes",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceCustomInventory_DeviceId",
                table: "DeviceCustomInventory",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceCustomInventory_ScriptActionId",
                table: "DeviceCustomInventory",
                column: "ScriptActionId");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceFirewalls_DeviceId",
                table: "DeviceFirewalls",
                column: "DeviceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeviceGpus_DeviceId",
                table: "DeviceGpus",
                column: "DeviceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeviceGroupMemberships_DeviceGroupId",
                table: "DeviceGroupMemberships",
                column: "DeviceGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceGroupMemberships_DeviceId",
                table: "DeviceGroupMemberships",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceGroupPowerSchedules_ConfiguredByUserId",
                table: "DeviceGroupPowerSchedules",
                column: "ConfiguredByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceGroupPowerSchedules_DeviceGroupId_ScheduleId_Action",
                table: "DeviceGroupPowerSchedules",
                columns: new[] { "DeviceGroupId", "ScheduleId", "Action" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeviceGroupPowerSchedules_ScheduleId",
                table: "DeviceGroupPowerSchedules",
                column: "ScheduleId");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceGroups_DeploymentProfileId",
                table: "DeviceGroups",
                column: "DeploymentProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceGroups_ImageId",
                table: "DeviceGroups",
                column: "ImageId");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceGroups_Type",
                table: "DeviceGroups",
                column: "Type",
                unique: true,
                filter: "\"Type\" = 2");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceGroupTags_DeviceGroupId",
                table: "DeviceGroupTags",
                column: "DeviceGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceGroupTags_TagId",
                table: "DeviceGroupTags",
                column: "TagId");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceHardDrives_DeviceId",
                table: "DeviceHardDrives",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceImagingLogs_DeviceId",
                table: "DeviceImagingLogs",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceLogicalVolumes_DeviceId",
                table: "DeviceLogicalVolumes",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceLogins_DeviceId",
                table: "DeviceLogins",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceNics_DeviceId",
                table: "DeviceNics",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceOs_DeviceId",
                table: "DeviceOs",
                column: "DeviceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DevicePolicies_PolicyId",
                table: "DevicePolicies",
                column: "PolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_DevicePrinters_DeviceId",
                table: "DevicePrinters",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceProcessors_DeviceId",
                table: "DeviceProcessors",
                column: "DeviceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Devices_DeploymentProfileId",
                table: "Devices",
                column: "DeploymentProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_Devices_DirectorySourceId_DirectoryObjectGuid",
                table: "Devices",
                columns: new[] { "DirectorySourceId", "DirectoryObjectGuid" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Devices_ImageId",
                table: "Devices",
                column: "ImageId");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceSoftware_DeviceId",
                table: "DeviceSoftware",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceSoftware_SoftwareId",
                table: "DeviceSoftware",
                column: "SoftwareId");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceTags_DeviceId",
                table: "DeviceTags",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceTags_TagId",
                table: "DeviceTags",
                column: "TagId");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceTasks_DeploymentId_DeviceId",
                table: "DeviceTasks",
                columns: new[] { "DeploymentId", "DeviceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DeviceTasks_DeviceGuid_Status",
                table: "DeviceTasks",
                columns: new[] { "DeviceGuid", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceTasks_DeviceId_Status",
                table: "DeviceTasks",
                columns: new[] { "DeviceId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceTasks_Status_LeaseExpiresAtUtc",
                table: "DeviceTasks",
                columns: new[] { "Status", "LeaseExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_DeviceWindowsUpdates_DeviceId",
                table: "DeviceWindowsUpdates",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceWindowsUpdates_WindowsUpdateId",
                table: "DeviceWindowsUpdates",
                column: "WindowsUpdateId");

            migrationBuilder.CreateIndex(
                name: "IX_DeviceWindowsUpdateScan_DeviceId",
                table: "DeviceWindowsUpdateScan",
                column: "DeviceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DirectoryEffectiveGroupMemberships_DirectorySourceId_Member~",
                table: "DirectoryEffectiveGroupMemberships",
                columns: new[] { "DirectorySourceId", "MemberGuid" });

            migrationBuilder.CreateIndex(
                name: "IX_DirectoryGroupMemberships_DirectorySourceId_GroupGuid_Membe~",
                table: "DirectoryGroupMemberships",
                columns: new[] { "DirectorySourceId", "GroupGuid", "MemberGuid" });

            migrationBuilder.CreateIndex(
                name: "IX_DirectoryGroupMemberships_DirectorySourceId_MemberGuid",
                table: "DirectoryGroupMemberships",
                columns: new[] { "DirectorySourceId", "MemberGuid" });

            migrationBuilder.CreateIndex(
                name: "IX_DirectoryObjects_DirectorySourceId_Kind_IsMissing",
                table: "DirectoryObjects",
                columns: new[] { "DirectorySourceId", "Kind", "IsMissing" });

            migrationBuilder.CreateIndex(
                name: "IX_DirectoryObjects_DirectorySourceId_ParentGuid",
                table: "DirectoryObjects",
                columns: new[] { "DirectorySourceId", "ParentGuid" });

            migrationBuilder.CreateIndex(
                name: "IX_DirectoryOuPolicies_PolicyId",
                table: "DirectoryOuPolicies",
                column: "PolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_DirectorySources_ScheduleId",
                table: "DirectorySources",
                column: "ScheduleId");

            migrationBuilder.CreateIndex(
                name: "IX_DomainJoinActions_Guid",
                table: "DomainJoinActions",
                column: "Guid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DomainJoinActions_ManagedCredentialId",
                table: "DomainJoinActions",
                column: "ManagedCredentialId");

            migrationBuilder.CreateIndex(
                name: "IX_DomainJoinActions_OuNamingRuleSetId",
                table: "DomainJoinActions",
                column: "OuNamingRuleSetId");

            migrationBuilder.CreateIndex(
                name: "IX_DriverFiles_PreparedStorageProviderId",
                table: "DriverFiles",
                column: "PreparedStorageProviderId");

            migrationBuilder.CreateIndex(
                name: "IX_DriverFiles_StoredFileId",
                table: "DriverFiles",
                column: "StoredFileId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DriverModelLinks_DriverProfileId_Manufacturer_Model",
                table: "DriverModelLinks",
                columns: new[] { "DriverProfileId", "Manufacturer", "Model" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DriverPackRegexRules_DriverPackId",
                table: "DriverPackRegexRules",
                column: "DriverPackId");

            migrationBuilder.CreateIndex(
                name: "IX_DriverProfiles_DirectoryGuid",
                table: "DriverProfiles",
                column: "DirectoryGuid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DriverProfiles_Name",
                table: "DriverProfiles",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DriverProfiles_UpdateScheduleId",
                table: "DriverProfiles",
                column: "UpdateScheduleId");

            migrationBuilder.CreateIndex(
                name: "IX_DriverProfileTag_TagId",
                table: "DriverProfileTag",
                column: "TagId");

            migrationBuilder.CreateIndex(
                name: "IX_EmailDigestDelivery_AppUserId_Kind_Period",
                table: "EmailDigestDelivery",
                columns: new[] { "AppUserId", "Kind", "Period" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FileActions_Guid",
                table: "FileActions",
                column: "Guid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_GroupPolicies_DeviceGroupId",
                table: "GroupPolicies",
                column: "DeviceGroupId");

            migrationBuilder.CreateIndex(
                name: "IX_GroupPolicies_PolicyId",
                table: "GroupPolicies",
                column: "PolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_HelpCategory_Name",
                table: "HelpCategory",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HelpEmailDelivery_BatchId_Address",
                table: "HelpEmailDelivery",
                columns: new[] { "BatchId", "Address" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HelpEmailDelivery_Status_NextAttemptUtc",
                table: "HelpEmailDelivery",
                columns: new[] { "Status", "NextAttemptUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_HelpEmailDelivery_TicketId",
                table: "HelpEmailDelivery",
                column: "TicketId");

            migrationBuilder.CreateIndex(
                name: "IX_HelpEmailState_DueAtUtc",
                table: "HelpEmailState",
                column: "DueAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_HelpQueue_Name",
                table: "HelpQueue",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HelpQueueMember_UserId",
                table: "HelpQueueMember",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_HelpRoutingRule_CategoryId",
                table: "HelpRoutingRule",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_HelpRoutingRule_QueueId",
                table: "HelpRoutingRule",
                column: "QueueId");

            migrationBuilder.CreateIndex(
                name: "IX_HelpRoutingRule_Stage_Order",
                table: "HelpRoutingRule",
                columns: new[] { "Stage", "Order" });

            migrationBuilder.CreateIndex(
                name: "IX_HelpTicketMessages_CommandId",
                table: "HelpTicketMessages",
                column: "CommandId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HelpTicketMessages_TicketId_Id",
                table: "HelpTicketMessages",
                columns: new[] { "TicketId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_HelpTickets_AssignedToId",
                table: "HelpTickets",
                column: "AssignedToId");

            migrationBuilder.CreateIndex(
                name: "IX_HelpTickets_CategoryId",
                table: "HelpTickets",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_HelpTickets_CommandId",
                table: "HelpTickets",
                column: "CommandId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_HelpTickets_QueueId",
                table: "HelpTickets",
                column: "QueueId");

            migrationBuilder.CreateIndex(
                name: "IX_HelpTickets_RequesterKeys",
                table: "HelpTickets",
                column: "RequesterKeys")
                .Annotation("Npgsql:IndexMethod", "gin");

            migrationBuilder.CreateIndex(
                name: "IX_HelpTickets_UpdatedAtUtc",
                table: "HelpTickets",
                column: "UpdatedAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_HelpTicketStatusEvent_Status_OccurredAtUtc",
                table: "HelpTicketStatusEvent",
                columns: new[] { "Status", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_HelpTicketStatusEvent_TicketId_OccurredAtUtc",
                table: "HelpTicketStatusEvent",
                columns: new[] { "TicketId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ImageContentDeletion_StorageProviderId",
                table: "ImageContentDeletion",
                column: "StorageProviderId");

            migrationBuilder.CreateIndex(
                name: "IX_ImagePrepProfiles_Name",
                table: "ImagePrepProfiles",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ImageProfiles_DriverPackId",
                table: "ImageProfiles",
                column: "DriverPackId");

            migrationBuilder.CreateIndex(
                name: "IX_ImageProfiles_Name",
                table: "ImageProfiles",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ImageProfiles_OuNamingRuleSetId",
                table: "ImageProfiles",
                column: "OuNamingRuleSetId");

            migrationBuilder.CreateIndex(
                name: "IX_ImageReplica_State_RetryAtUtc",
                table: "ImageReplica",
                columns: new[] { "State", "RetryAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_ImageReplica_StorageProviderId",
                table: "ImageReplica",
                column: "StorageProviderId");

            migrationBuilder.CreateIndex(
                name: "IX_ImageReplicationTarget_StorageProviderId",
                table: "ImageReplicationTarget",
                column: "StorageProviderId");

            migrationBuilder.CreateIndex(
                name: "IX_Images_Name",
                table: "Images",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ImageTags_ImageId",
                table: "ImageTags",
                column: "ImageId");

            migrationBuilder.CreateIndex(
                name: "IX_ImageTags_TagId",
                table: "ImageTags",
                column: "TagId");

            migrationBuilder.CreateIndex(
                name: "IX_ImageUploads_DeviceId",
                table: "ImageUploads",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_ImageUploads_ImageId",
                table: "ImageUploads",
                column: "ImageId");

            migrationBuilder.CreateIndex(
                name: "IX_ImageUploads_StorageProviderId",
                table: "ImageUploads",
                column: "StorageProviderId");

            migrationBuilder.CreateIndex(
                name: "IX_ImageUploads_UserId",
                table: "ImageUploads",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_ImageVersions_ImageId",
                table: "ImageVersions",
                column: "ImageId",
                unique: true,
                filter: "\"IsActive\" = TRUE");

            migrationBuilder.CreateIndex(
                name: "IX_ImageVersions_ImageId_Number",
                table: "ImageVersions",
                columns: new[] { "ImageId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ImageVersions_StorageProviderId",
                table: "ImageVersions",
                column: "StorageProviderId");

            migrationBuilder.CreateIndex(
                name: "IX_ImagingDeployment_DeploymentProfileId",
                table: "ImagingDeployment",
                column: "DeploymentProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_ImagingDeployment_DeviceId",
                table: "ImagingDeployment",
                column: "DeviceId",
                unique: true,
                filter: "\"CancelledAtUtc\" IS NULL AND \"SetupCompletedAtUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ImagingDeployment_ImageVersionId",
                table: "ImagingDeployment",
                column: "ImageVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_ImagingDeployment_MulticastSessionId",
                table: "ImagingDeployment",
                column: "MulticastSessionId");

            migrationBuilder.CreateIndex(
                name: "IX_ImagingDeploymentPolicy_AgentPolicyRunId",
                table: "ImagingDeploymentPolicy",
                column: "AgentPolicyRunId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ImagingDeploymentPolicy_ImagingDeploymentId_Position",
                table: "ImagingDeploymentPolicy",
                columns: new[] { "ImagingDeploymentId", "Position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ImagingDeploymentPolicy_PolicyId",
                table: "ImagingDeploymentPolicy",
                column: "PolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_LdapDirectorySettings_ManagedCredentialId",
                table: "LdapDirectorySettings",
                column: "ManagedCredentialId");

            migrationBuilder.CreateIndex(
                name: "IX_ManagedCredentials_Name",
                table: "ManagedCredentials",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_MulticastSession_DeploymentProfileId",
                table: "MulticastSession",
                column: "DeploymentProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_MulticastSession_ImageVersionId",
                table: "MulticastSession",
                column: "ImageVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_MulticastSession_Port",
                table: "MulticastSession",
                column: "Port",
                unique: true,
                filter: "\"State\" < 3");

            migrationBuilder.CreateIndex(
                name: "IX_MulticastSession_State_CreatedAtUtc",
                table: "MulticastSession",
                columns: new[] { "State", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_MulticastSession_StorageProviderId",
                table: "MulticastSession",
                column: "StorageProviderId");

            migrationBuilder.CreateIndex(
                name: "IX_NotificationActions_Guid",
                table: "NotificationActions",
                column: "Guid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OuNamingRuleSets_DirectorySourceId",
                table: "OuNamingRuleSets",
                column: "DirectorySourceId");

            migrationBuilder.CreateIndex(
                name: "IX_OuNamingRuleSets_Name",
                table: "OuNamingRuleSets",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Policies_ConditionId",
                table: "Policies",
                column: "ConditionId");

            migrationBuilder.CreateIndex(
                name: "IX_PolicyActions_ActionGuid",
                table: "PolicyActions",
                column: "ActionGuid");

            migrationBuilder.CreateIndex(
                name: "IX_PolicyActions_ConditionId",
                table: "PolicyActions",
                column: "ConditionId");

            migrationBuilder.CreateIndex(
                name: "IX_PolicyActions_ConditionNextActionReferenceActionGuid",
                table: "PolicyActions",
                column: "ConditionNextActionReferenceActionGuid");

            migrationBuilder.CreateIndex(
                name: "IX_PolicyActions_PolicyId",
                table: "PolicyActions",
                column: "PolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_PolicyHashHistories_PolicyId",
                table: "PolicyHashHistories",
                column: "PolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_PolicyHistories_DeviceId",
                table: "PolicyHistories",
                column: "DeviceId");

            migrationBuilder.CreateIndex(
                name: "IX_PolicyHistories_PolicyId",
                table: "PolicyHistories",
                column: "PolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_PolicyTags_PolicyId",
                table: "PolicyTags",
                column: "PolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_PolicyTags_TagId",
                table: "PolicyTags",
                column: "TagId");

            migrationBuilder.CreateIndex(
                name: "IX_PolicyUserAccess_CategoryId",
                table: "PolicyUserAccess",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_PolicyUserAccess_LoginAudienceId",
                table: "PolicyUserAccess",
                column: "LoginAudienceId");

            migrationBuilder.CreateIndex(
                name: "IX_PolicyUserAccess_SelfServiceAudienceId",
                table: "PolicyUserAccess",
                column: "SelfServiceAudienceId");

            migrationBuilder.CreateIndex(
                name: "IX_PrinterActions_Guid",
                table: "PrinterActions",
                column: "Guid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SavedDeviceReport_AppUserId_Name",
                table: "SavedDeviceReport",
                columns: new[] { "AppUserId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SavedUserDeviceReport_AppUserId_Name",
                table: "SavedUserDeviceReport",
                columns: new[] { "AppUserId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledTask_ScheduleId",
                table: "ScheduledTask",
                column: "ScheduleId");

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledTaskExecution_Status_QueuedAtUtc_Id",
                table: "ScheduledTaskExecution",
                columns: new[] { "Status", "QueuedAtUtc", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_ScheduledTaskExecution_TaskId_ScheduledForUtc",
                table: "ScheduledTaskExecution",
                columns: new[] { "TaskId", "ScheduledForUtc" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Schedules_Name",
                table: "Schedules",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScriptActions_Guid",
                table: "ScriptActions",
                column: "Guid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ScriptActions_ManagedCredentialId",
                table: "ScriptActions",
                column: "ManagedCredentialId");

            migrationBuilder.CreateIndex(
                name: "IX_SecurityActivityEvent_Account_SourceIp_OccurredAtUtc",
                table: "SecurityActivityEvent",
                columns: new[] { "Account", "SourceIp", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SecurityActivityEvent_OccurredAtUtc",
                table: "SecurityActivityEvent",
                column: "OccurredAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_SharedContent_Kind_LogicalKey",
                table: "SharedContent",
                columns: new[] { "Kind", "LogicalKey" });

            migrationBuilder.CreateIndex(
                name: "IX_SharedContentReplica_State_RetryAtUtc",
                table: "SharedContentReplica",
                columns: new[] { "State", "RetryAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_SharedContentReplica_StorageProviderId",
                table: "SharedContentReplica",
                column: "StorageProviderId");

            migrationBuilder.CreateIndex(
                name: "IX_SiteServices_CertificateSha256",
                table: "SiteServices",
                column: "CertificateSha256",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SiteServices_IssuerId",
                table: "SiteServices",
                column: "IssuerId");

            migrationBuilder.CreateIndex(
                name: "IX_SiteServices_IsWindowsWorker",
                table: "SiteServices",
                column: "IsWindowsWorker",
                unique: true,
                filter: "\"IsWindowsWorker\" = TRUE AND \"State\" <> 3");

            migrationBuilder.CreateIndex(
                name: "IX_SiteServices_PreviousCertificateSha256",
                table: "SiteServices",
                column: "PreviousCertificateSha256");

            migrationBuilder.CreateIndex(
                name: "IX_SocketConnections_AgentCertificateId",
                table: "SocketConnections",
                column: "AgentCertificateId");

            migrationBuilder.CreateIndex(
                name: "IX_SocketConnections_ConnectionId",
                table: "SocketConnections",
                column: "ConnectionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SocketConnections_DeviceId",
                table: "SocketConnections",
                column: "DeviceId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SocketConnections_LastSeenAtUtc",
                table: "SocketConnections",
                column: "LastSeenAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_Software_Name_Version",
                table: "Software",
                columns: new[] { "Name", "Version" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_SoftwareActions_Guid",
                table: "SoftwareActions",
                column: "Guid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SoftwareActions_InstallerFileId",
                table: "SoftwareActions",
                column: "InstallerFileId");

            migrationBuilder.CreateIndex(
                name: "IX_SoftwareActions_ManagedCredentialId",
                table: "SoftwareActions",
                column: "ManagedCredentialId");

            migrationBuilder.CreateIndex(
                name: "IX_SoftwareActions_Name",
                table: "SoftwareActions",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StorageMoves_DestinationId",
                table: "StorageMoves",
                column: "DestinationId");

            migrationBuilder.CreateIndex(
                name: "IX_StorageMoves_SourceId",
                table: "StorageMoves",
                column: "SourceId");

            migrationBuilder.CreateIndex(
                name: "IX_StorageProviders_IsImagingDefault",
                table: "StorageProviders",
                column: "IsImagingDefault",
                unique: true,
                filter: "\"IsImagingDefault\" = TRUE");

            migrationBuilder.CreateIndex(
                name: "IX_StorageProviders_IsManagementDefault",
                table: "StorageProviders",
                column: "IsManagementDefault",
                unique: true,
                filter: "\"IsManagementDefault\" = TRUE");

            migrationBuilder.CreateIndex(
                name: "IX_StorageProviders_ManagedCredentialId",
                table: "StorageProviders",
                column: "ManagedCredentialId");

            migrationBuilder.CreateIndex(
                name: "IX_StorageProviders_Name",
                table: "StorageProviders",
                column: "Name",
                unique: true,
                filter: "NOT \"IsDeleted\"");

            migrationBuilder.CreateIndex(
                name: "IX_StorageProviders_Path",
                table: "StorageProviders",
                column: "Path",
                unique: true,
                filter: "\"SiteServiceId\" IS NULL AND NOT \"IsDeleted\"");

            migrationBuilder.CreateIndex(
                name: "IX_StorageProviders_SiteServiceId_Path",
                table: "StorageProviders",
                columns: new[] { "SiteServiceId", "Path" },
                unique: true,
                filter: "\"SiteServiceId\" IS NOT NULL AND NOT \"IsDeleted\"");

            migrationBuilder.CreateIndex(
                name: "IX_StorageReadLease_ExpiresAtUtc",
                table: "StorageReadLease",
                column: "ExpiresAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_StorageReadLease_StorageProviderId_ExpiresAtUtc",
                table: "StorageReadLease",
                columns: new[] { "StorageProviderId", "ExpiresAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_StoredFiles_DirectoryGuid_Name",
                table: "StoredFiles",
                columns: new[] { "DirectoryGuid", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StoredFiles_StorageProviderId",
                table: "StoredFiles",
                column: "StorageProviderId");

            migrationBuilder.CreateIndex(
                name: "IX_Tags_Name",
                table: "Tags",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WindowsUpdateActions_Guid",
                table: "WindowsUpdateActions",
                column: "Guid",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WindowsUpdateAutoApprovalRules_AppUserId",
                table: "WindowsUpdateAutoApprovalRules",
                column: "AppUserId");

            migrationBuilder.CreateIndex(
                name: "IX_WindowsUpdates_UpdateId",
                table: "WindowsUpdates",
                column: "UpdateId",
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_WindowsWorkerJob_SiteId_State",
                table: "WindowsWorkerJob",
                columns: new[] { "SiteId", "State" });

            migrationBuilder.CreateIndex(
                name: "IX_WinPeBuilds_DefinitionId_Number",
                table: "WinPeBuilds",
                columns: new[] { "DefinitionId", "Number" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WinPeBuilds_StorageProviderId",
                table: "WinPeBuilds",
                column: "StorageProviderId");

            migrationBuilder.CreateIndex(
                name: "IX_WinPeDefinitions_IsArm64Default",
                table: "WinPeDefinitions",
                column: "IsArm64Default",
                unique: true,
                filter: "\"IsArm64Default\" = TRUE");

            migrationBuilder.CreateIndex(
                name: "IX_WinPeDefinitions_IsDefault",
                table: "WinPeDefinitions",
                column: "IsDefault",
                unique: true,
                filter: "\"IsDefault\" = TRUE");

            migrationBuilder.CreateIndex(
                name: "IX_WinPeDefinitions_Name",
                table: "WinPeDefinitions",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WinPeDefinitions_PublishedBuildId",
                table: "WinPeDefinitions",
                column: "PublishedBuildId");

            migrationBuilder.CreateIndex(
                name: "IX_WinPeSetupJobs_ToolchainId",
                table: "WinPeSetupJobs",
                column: "ToolchainId");

            migrationBuilder.CreateIndex(
                name: "IX_WinPeToolchains_Name",
                table: "WinPeToolchains",
                column: "Name",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_WinPeBuilds_WinPeDefinitions_DefinitionId",
                table: "WinPeBuilds",
                column: "DefinitionId",
                principalTable: "WinPeDefinitions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql("CREATE INDEX \"IX_DeviceNics_NormalizedMac\" ON \"DeviceNics\"\n(upper(btrim(replace(replace(replace(\"Mac\", ':', ''), '-', ''), '.', ''))))\nWHERE \"Mac\" IS NOT NULL;\nCREATE FUNCTION notify_device_activity() RETURNS trigger LANGUAGE plpgsql AS $$\nDECLARE\n    before_row jsonb;\n    after_row jsonb;\n    fields text[];\n    field text;\n    changed boolean := false;\n    device_id text;\nBEGIN\n    before_row := CASE WHEN TG_OP = 'INSERT' THEN '{}'::jsonb ELSE to_jsonb(OLD) END;\n    after_row := CASE WHEN TG_OP = 'DELETE' THEN '{}'::jsonb ELSE to_jsonb(NEW) END;\n    fields := CASE TG_TABLE_NAME\n        WHEN 'DeviceActivityReports' THEN ARRAY['ConnectionId', 'Kind', 'RunId', 'Name']\n        WHEN 'DeviceTasks' THEN ARRAY['Status', 'DeploymentId']\n        WHEN 'ActiveImagingTasks' THEN ARRAY['Status', 'ImageName', 'Type', 'DeploymentId', 'UploadId', 'FinishedAtUtc']\n        WHEN 'SocketConnections' THEN ARRAY['ConnectionId', 'AgentCertificateId']\n        ELSE ARRAY['ArchivedAtUtc', 'ProvisionStatus'] END;\n    IF TG_OP <> 'UPDATE' THEN changed := true;\n    ELSE\n        FOREACH field IN ARRAY fields LOOP\n            IF before_row -> field IS DISTINCT FROM after_row -> field THEN changed := true; EXIT; END IF;\n        END LOOP;\n        IF TG_TABLE_NAME = 'SocketConnections' AND\n            (before_row ->> 'LastSeenAtUtc')::timestamptz <= now() - interval '90 seconds' THEN\n            changed := true;\n        END IF;\n    END IF;\n    IF changed THEN\n        field := CASE WHEN TG_TABLE_NAME = 'Devices' THEN 'Id' ELSE 'DeviceId' END;\n        device_id := coalesce(after_row ->> field, before_row ->> field);\n        PERFORM pg_notify('device_activity_changed', TG_TABLE_SCHEMA || ':' || device_id);\n    END IF;\n    RETURN NULL;\nEND $$;\nCREATE TRIGGER device_activity_report_changed AFTER INSERT OR UPDATE OR DELETE ON \"DeviceActivityReports\"\n    FOR EACH ROW EXECUTE FUNCTION notify_device_activity();\nCREATE TRIGGER device_task_activity_changed AFTER INSERT OR UPDATE OR DELETE ON \"DeviceTasks\"\n    FOR EACH ROW EXECUTE FUNCTION notify_device_activity();\nCREATE TRIGGER device_imaging_activity_changed AFTER INSERT OR UPDATE OR DELETE ON \"ActiveImagingTasks\"\n    FOR EACH ROW EXECUTE FUNCTION notify_device_activity();\nCREATE TRIGGER device_connection_activity_changed AFTER INSERT OR UPDATE OR DELETE ON \"SocketConnections\"\n    FOR EACH ROW EXECUTE FUNCTION notify_device_activity();\nCREATE TRIGGER device_record_activity_changed AFTER INSERT OR UPDATE OR DELETE ON \"Devices\"\n    FOR EACH ROW EXECUTE FUNCTION notify_device_activity();");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TRIGGER device_record_activity_changed ON \"Devices\";\nDROP TRIGGER device_connection_activity_changed ON \"SocketConnections\";\nDROP TRIGGER device_imaging_activity_changed ON \"ActiveImagingTasks\";\nDROP TRIGGER device_task_activity_changed ON \"DeviceTasks\";\nDROP TRIGGER device_activity_report_changed ON \"DeviceActivityReports\";\nDROP FUNCTION notify_device_activity();\nDROP INDEX \"IX_DeviceNics_NormalizedMac\";");

            migrationBuilder.DropForeignKey(
                name: "FK_SiteServices_AppCertificates_IssuerId",
                table: "SiteServices");

            migrationBuilder.DropForeignKey(
                name: "FK_StorageProviders_ManagedCredentials_ManagedCredentialId",
                table: "StorageProviders");

            migrationBuilder.DropForeignKey(
                name: "FK_WinPeBuilds_StorageProviders_StorageProviderId",
                table: "WinPeBuilds");

            migrationBuilder.DropForeignKey(
                name: "FK_WinPeBuilds_WinPeDefinitions_DefinitionId",
                table: "WinPeBuilds");

            migrationBuilder.DropTable(
                name: "ActionFiles");

            migrationBuilder.DropTable(
                name: "ActionTags");

            migrationBuilder.DropTable(
                name: "ActiveCompiledPolicies");

            migrationBuilder.DropTable(
                name: "ActiveImagingTasks");

            migrationBuilder.DropTable(
                name: "AgentDownloadLease");

            migrationBuilder.DropTable(
                name: "AgentInventoryLease");

            migrationBuilder.DropTable(
                name: "AgentPolicyRun");

            migrationBuilder.DropTable(
                name: "AgentPortalSession");

            migrationBuilder.DropTable(
                name: "AgentProvisioningRequests");

            migrationBuilder.DropTable(
                name: "AgentRequestNonces");

            migrationBuilder.DropTable(
                name: "AppRoleClaims");

            migrationBuilder.DropTable(
                name: "AppSettings");

            migrationBuilder.DropTable(
                name: "AppUserClaims");

            migrationBuilder.DropTable(
                name: "AppUserGridColumns");

            migrationBuilder.DropTable(
                name: "AppUserGroupDirectoryMapping");

            migrationBuilder.DropTable(
                name: "AppUserGroupMemberships");

            migrationBuilder.DropTable(
                name: "AppUserGroupRights");

            migrationBuilder.DropTable(
                name: "AppUserLogins");

            migrationBuilder.DropTable(
                name: "AppUserPreferences");

            migrationBuilder.DropTable(
                name: "AppUserRights");

            migrationBuilder.DropTable(
                name: "AppUserRoles");

            migrationBuilder.DropTable(
                name: "AppUserTokens");

            migrationBuilder.DropTable(
                name: "AuditLogs");

            migrationBuilder.DropTable(
                name: "AutomaticDeploymentRule");

            migrationBuilder.DropTable(
                name: "CommandActions");

            migrationBuilder.DropTable(
                name: "DeploymentDriverRules");

            migrationBuilder.DropTable(
                name: "DeploymentPolicies");

            migrationBuilder.DropTable(
                name: "DeviceActivityReports");

            migrationBuilder.DropTable(
                name: "DeviceAntiVirus");

            migrationBuilder.DropTable(
                name: "DeviceAttachments");

            migrationBuilder.DropTable(
                name: "DeviceBios");

            migrationBuilder.DropTable(
                name: "DeviceBitLocker");

            migrationBuilder.DropTable(
                name: "DeviceCertificates");

            migrationBuilder.DropTable(
                name: "DeviceCheckinLeases");

            migrationBuilder.DropTable(
                name: "DeviceComments");

            migrationBuilder.DropTable(
                name: "DeviceComputerSystems");

            migrationBuilder.DropTable(
                name: "DeviceCustomAttributes");

            migrationBuilder.DropTable(
                name: "DeviceCustomInventory");

            migrationBuilder.DropTable(
                name: "DeviceFirewalls");

            migrationBuilder.DropTable(
                name: "DeviceGpus");

            migrationBuilder.DropTable(
                name: "DeviceGroupMemberships");

            migrationBuilder.DropTable(
                name: "DeviceGroupPowerSchedules");

            migrationBuilder.DropTable(
                name: "DeviceGroupTags");

            migrationBuilder.DropTable(
                name: "DeviceHardDrives");

            migrationBuilder.DropTable(
                name: "DeviceImagingLogs");

            migrationBuilder.DropTable(
                name: "DeviceLogicalVolumes");

            migrationBuilder.DropTable(
                name: "DeviceLogins");

            migrationBuilder.DropTable(
                name: "DeviceNics");

            migrationBuilder.DropTable(
                name: "DeviceNotificationReceipts");

            migrationBuilder.DropTable(
                name: "DeviceOs");

            migrationBuilder.DropTable(
                name: "DevicePolicies");

            migrationBuilder.DropTable(
                name: "DevicePrinters");

            migrationBuilder.DropTable(
                name: "DeviceProcessors");

            migrationBuilder.DropTable(
                name: "DeviceSoftware");

            migrationBuilder.DropTable(
                name: "DeviceTags");

            migrationBuilder.DropTable(
                name: "DeviceTasks");

            migrationBuilder.DropTable(
                name: "DeviceWindowsUpdates");

            migrationBuilder.DropTable(
                name: "DeviceWindowsUpdateScan");

            migrationBuilder.DropTable(
                name: "DirectoryEffectiveGroupMemberships");

            migrationBuilder.DropTable(
                name: "DirectoryGroupMemberships");

            migrationBuilder.DropTable(
                name: "DirectoryOuPolicies");

            migrationBuilder.DropTable(
                name: "DirectorySyncStates");

            migrationBuilder.DropTable(
                name: "DomainJoinActions");

            migrationBuilder.DropTable(
                name: "DriverFiles");

            migrationBuilder.DropTable(
                name: "DriverModelLinks");

            migrationBuilder.DropTable(
                name: "DriverPackRegexRules");

            migrationBuilder.DropTable(
                name: "DriverProfileTag");

            migrationBuilder.DropTable(
                name: "EmailDigestDelivery");

            migrationBuilder.DropTable(
                name: "EmailServerSettings");

            migrationBuilder.DropTable(
                name: "EntraConnections");

            migrationBuilder.DropTable(
                name: "FileActions");

            migrationBuilder.DropTable(
                name: "GroupPolicies");

            migrationBuilder.DropTable(
                name: "HelpEmailDelivery");

            migrationBuilder.DropTable(
                name: "HelpEmailState");

            migrationBuilder.DropTable(
                name: "HelpQueueMember");

            migrationBuilder.DropTable(
                name: "HelpRoutingRule");

            migrationBuilder.DropTable(
                name: "HelpTicketMessages");

            migrationBuilder.DropTable(
                name: "HelpTicketStatusEvent");

            migrationBuilder.DropTable(
                name: "ImageContentDeletion");

            migrationBuilder.DropTable(
                name: "ImagePrepProfiles");

            migrationBuilder.DropTable(
                name: "ImageReplica");

            migrationBuilder.DropTable(
                name: "ImageReplicationTarget");

            migrationBuilder.DropTable(
                name: "ImageTags");

            migrationBuilder.DropTable(
                name: "ImageUploads");

            migrationBuilder.DropTable(
                name: "ImageVersions");

            migrationBuilder.DropTable(
                name: "ImagingDeploymentPolicy");

            migrationBuilder.DropTable(
                name: "LdapDirectorySettings");

            migrationBuilder.DropTable(
                name: "NotificationActions");

            migrationBuilder.DropTable(
                name: "PolicyActions");

            migrationBuilder.DropTable(
                name: "PolicyHashHistories");

            migrationBuilder.DropTable(
                name: "PolicyHistories");

            migrationBuilder.DropTable(
                name: "PolicyTags");

            migrationBuilder.DropTable(
                name: "PolicyUserAccess");

            migrationBuilder.DropTable(
                name: "PrinterActions");

            migrationBuilder.DropTable(
                name: "SavedDeviceReport");

            migrationBuilder.DropTable(
                name: "SavedUserDeviceReport");

            migrationBuilder.DropTable(
                name: "ScheduledTaskExecution");

            migrationBuilder.DropTable(
                name: "SecurityActivityEvent");

            migrationBuilder.DropTable(
                name: "SharedContentReplica");

            migrationBuilder.DropTable(
                name: "SocketConnections");

            migrationBuilder.DropTable(
                name: "SoftwareActions");

            migrationBuilder.DropTable(
                name: "StorageMoves");

            migrationBuilder.DropTable(
                name: "StorageReadLease");

            migrationBuilder.DropTable(
                name: "UserAudienceMember");

            migrationBuilder.DropTable(
                name: "UserEmailSubscription");

            migrationBuilder.DropTable(
                name: "UserNotificationAcknowledgments");

            migrationBuilder.DropTable(
                name: "WindowsUpdateActions");

            migrationBuilder.DropTable(
                name: "WindowsUpdateAutoApprovalRules");

            migrationBuilder.DropTable(
                name: "WindowsWorkerJob");

            migrationBuilder.DropTable(
                name: "WinGetInstallerManifests");

            migrationBuilder.DropTable(
                name: "WinGetLocaleManifests");

            migrationBuilder.DropTable(
                name: "WinGetManifestDownloads");

            migrationBuilder.DropTable(
                name: "WinGetVersionManifests");

            migrationBuilder.DropTable(
                name: "WinPeSetupJobs");

            migrationBuilder.DropTable(
                name: "AppUserGroups");

            migrationBuilder.DropTable(
                name: "AppRoles");

            migrationBuilder.DropTable(
                name: "Attachments");

            migrationBuilder.DropTable(
                name: "Certificates");

            migrationBuilder.DropTable(
                name: "Comments");

            migrationBuilder.DropTable(
                name: "CustomAttributes");

            migrationBuilder.DropTable(
                name: "Software");

            migrationBuilder.DropTable(
                name: "ActionDeployments");

            migrationBuilder.DropTable(
                name: "WindowsUpdates");

            migrationBuilder.DropTable(
                name: "DeviceGroups");

            migrationBuilder.DropTable(
                name: "HelpTickets");

            migrationBuilder.DropTable(
                name: "ImagingDeployment");

            migrationBuilder.DropTable(
                name: "Tags");

            migrationBuilder.DropTable(
                name: "Policies");

            migrationBuilder.DropTable(
                name: "SelfServiceCategory");

            migrationBuilder.DropTable(
                name: "ScheduledTask");

            migrationBuilder.DropTable(
                name: "SharedContent");

            migrationBuilder.DropTable(
                name: "AgentCertificates");

            migrationBuilder.DropTable(
                name: "StoredFiles");

            migrationBuilder.DropTable(
                name: "UserAudience");

            migrationBuilder.DropTable(
                name: "WinPeToolchains");

            migrationBuilder.DropTable(
                name: "AppUsers");

            migrationBuilder.DropTable(
                name: "HelpCategory");

            migrationBuilder.DropTable(
                name: "HelpQueue");

            migrationBuilder.DropTable(
                name: "MulticastSession");

            migrationBuilder.DropTable(
                name: "ScriptActions");

            migrationBuilder.DropTable(
                name: "Devices");

            migrationBuilder.DropTable(
                name: "ActionReferences");

            migrationBuilder.DropTable(
                name: "DirectoryObjects");

            migrationBuilder.DropTable(
                name: "ImageProfiles");

            migrationBuilder.DropTable(
                name: "Images");

            migrationBuilder.DropTable(
                name: "DriverProfiles");

            migrationBuilder.DropTable(
                name: "OuNamingRuleSets");

            migrationBuilder.DropTable(
                name: "DirectorySources");

            migrationBuilder.DropTable(
                name: "Schedules");

            migrationBuilder.DropTable(
                name: "AppCertificates");

            migrationBuilder.DropTable(
                name: "ManagedCredentials");

            migrationBuilder.DropTable(
                name: "StorageProviders");

            migrationBuilder.DropTable(
                name: "SiteServices");

            migrationBuilder.DropTable(
                name: "WinPeDefinitions");

            migrationBuilder.DropTable(
                name: "WinPeBuilds");
        }
    }
}
