using MudBlazor;

namespace CloneDeploy_Server.Backend.Common;

public static class Constants
{
    public static class UserMembership
    {
        public const string Admin = "Admin";
        public const string User = "User";
    }
    
    public static class Claims
    {
        public const string Permission = "Permission";
    }
    
    public static class Permissions
    {
        public const string ReportHelpDesk = "Report.HelpDesk";
        public const string ReportDevices = "Report.Devices";
        public const string ReportUsers = "Report.Users";
        public const string ReportWindowsUpdates = "Report.WindowsUpdates";
        public const string ReportSoftware = "Report.Software";
        public const string ReportPolicies = "Report.Policies";
        public const string ReportSoftwareRemovalAll = "Report.SoftwareRemovalAll";
        // Baseline application access for own tasks; not an assignable grant.
        public const string TaskOwnAccess = "Task.Access";
        public const string TaskViewAll = "Task.ViewAll";
        public const string TaskManageAll = "Task.ManageAll";
        public const string AdminOnly = "AdminOnly";
        public const string AgentAdminTools = "Agent.AdminTools";
        public const string HelpDeskAccess = "HelpDesk.Access";
        public const string HelpDeskViewAssigned = "HelpDesk.ManageAssigned";
        public const string HelpDeskViewAll = "HelpDesk.ManageAll";
        public const string HelpDeskViewUnassigned = "HelpDesk.ManageUnassigned";
        public static readonly string[] HelpDeskViews = [HelpDeskViewAssigned, HelpDeskViewAll, HelpDeskViewUnassigned];
        public const string PolicyView = "Policy.View";
        public const string PolicyEdit = "Policy.Edit";
        public const string PolicyActivate = "Policy.Activate";
        public const string PolicyDeactivate = "Policy.Deactivate";
        public const string ActionDeploy = "Action.Deploy";

        public const string DeviceAdd = "Device.Add";
        public const string DeviceEdit = "Device.Edit";
        public const string DeviceDeployImage = "Device.DeployImage";
        public const string DeviceDeployImageThroughAgent = "Device.DeployImageThroughAgent";
        public const string DeviceDeployActions = "Device.DeployActions";
        public const string DeviceReboot = "Device.Reboot";
        public const string DeviceShutdown = "Device.Shutdown";
        public const string DeviceWake = "Device.Wake";
        public const string DeviceUninstallSoftware = "Device.UninstallSoftware";
        public const string DeviceTerminal = "Device.Terminal";
        public const string DeviceServices = "Device.Services";
        public const string DeviceProcesses = "Device.Processes";
        public const string DeviceRemoteView = "Device.RemoteView";
        public const string DeviceRemoteControl = "Device.RemoteControl";
        public const string DeviceView = "Device.View";
        public const string DeviceApprove = "Device.Approve";
        public const string DeviceInventory = "Device.Inventory";
        public const string DeviceCheckIn = "Device.CheckIn";
        public const string DeviceGroupEdit = "DeviceGroup.Edit";
        public const string DeviceGroupPowerManagement = "DeviceGroup.PowerManagement";
        public const string DeviceGroupView = "DeviceGroup.View";
        public const string DeviceGroupDeployImage = "DeviceGroup.DeployImage";
        public const string DeviceGroupDeployMulticast = "DeviceGroup.DeployMulticast";
        public const string CustomAttributeView = "CustomAttribute.View";
        public const string CustomAttributeEdit = "CustomAttribute.Edit";
        public const string ScheduleView = "Schedule.View";
        public const string ScheduleEdit = "Schedule.Edit";
        public const string UserAudienceView = "UserAudience.View";
        public const string UserAudienceEdit = "UserAudience.Edit";
        public const string SelfServiceCategoryView = "SelfServiceCategory.View";
        public const string SelfServiceCategoryEdit = "SelfServiceCategory.Edit";
        public const string TagView = "Tag.View";
        public const string TagEdit = "Tag.Edit";
        public const string ImageCreate = "Image.Create";
        public const string ImageEdit = "Image.Edit";
        public const string ImageView = "Image.View";
        public const string ImageApprove = "Image.Approve";
        public const string ImageDeployUnapproved = "Image.DeployUnapproved";
        public const string ImageCapture = "Image.Capture";
        public const string ImagerDeploy = "Imager.Deploy";
        public const string ImagerOverride = "Imager.Override";
        public const string DeploymentRuleView = "DeploymentRule.View";
        public const string DeploymentRuleEdit = "DeploymentRule.Edit";
        public const string OuNamingRuleView = "OuNamingRule.View";
        public const string OuNamingRuleEdit = "OuNamingRule.Edit";
        public const string DeploymentProfileView = "DeploymentProfile.View";
        public const string DeploymentProfileEdit = "DeploymentProfile.Edit";
        public const string DriverPackView = "DriverPack.View";
        public const string DriverPackEdit = "DriverPack.Edit";
        public const string ImagePrepProfileView = "ImagePrepProfile.View";
        public const string ImagePrepProfileEdit = "ImagePrepProfile.Edit";
        public const string ImagingAccess = "Imaging.Access";
        
        
        public record PermissionDefinition(string Key, string Category, string Name);
        public static readonly IReadOnlyList<PermissionDefinition> All =
        [
            new(AgentAdminTools, "Agent", "Access agent admin tools"),
            new(TaskViewAll, "Tasks", "View all tasks"),
            new(TaskManageAll, "Tasks", "Manage all tasks (includes viewing all)"),
            new(ReportHelpDesk, "Reports", "Help Desk"),
            new(ReportDevices, "Reports", "Devices"),
            new(ReportUsers, "Reports", "Users"),
            new(ReportWindowsUpdates, "Reports", "Windows Updates"),
            new(ReportSoftware, "Reports", "Software"),
            new(ReportPolicies, "Reports", "Policies"),
            new(ReportSoftwareRemovalAll, "Reports", "Software removal all"),
            new(HelpDeskViewAssigned, "Help Desk", "Manage my tickets and my queues’ tickets"),
            new(HelpDeskViewAll, "Help Desk", "Manage all tickets"),
            new(HelpDeskViewUnassigned, "Help Desk", "Manage the Unassigned queue"),
            new(PolicyView, "Policies and Actions", "View"),
            new(PolicyEdit, "Policies and Actions", "Edit (includes creating and deleting)"),
            new(PolicyActivate, "Policies and Actions", "Activate policies"),
            new(PolicyDeactivate, "Policies and Actions", "Deactivate policies"),
            new(ActionDeploy, "Policies and Actions", "Deploy actions (now or next check-in)"),
            new(DeviceView, "Device", "View Devices"),
            new(DeviceAdd, "Device", "Add device"),
            new(DeviceEdit, "Device", "Edit device (includes archiving and deleting)"),
            new(DeviceWake, "Device", "Wake up"),
            new(DeviceUninstallSoftware, "Device", "Uninstall software"),
            new(DeviceDeployImage, "Device", "Deploy image"),
            new(DeviceDeployImageThroughAgent, "Device", "Deploy image through agent"),
            new(DeviceDeployActions, "Device", "Deploy actions now"),
            new(DeviceReboot, "Device", "Reboot"),
            new(DeviceShutdown, "Device", "Shutdown"),
            new(DeviceTerminal, "Device", "Terminal"),
            new(DeviceServices, "Device", "Services"),
            new(DeviceProcesses, "Device", "Processes"),
            new(DeviceRemoteView, "Device", "View remote viewing"),
            new(DeviceRemoteControl, "Device", "Control remote viewing"),
            new(DeviceApprove, "Device", "Approve enrollment / reset request"),
            new(DeviceInventory, "Device", "Inventory now"),
            new(DeviceCheckIn, "Device", "Checkin now"),
            new(DeviceGroupEdit, "Device Groups", "Edit group (includes creating and deleting)"),
            new(DeviceGroupPowerManagement, "Device Groups", "Group power management (wake-up and power schedules)"),
            new(DeviceGroupView, "Device Groups", "View group"),
            new(DeviceGroupDeployImage, "Device Groups", "Deploy image"),
            new(DeviceGroupDeployMulticast, "Device Groups", "Deploy multicast (groups and standalone sessions)"),
            new(CustomAttributeView, "Shared Resources - Custom Attributes", "View"),
            new(CustomAttributeEdit, "Shared Resources - Custom Attributes", "Edit (includes creating and deleting)"),
            new(ScheduleView, "Shared Resources - Schedules", "View"),
            new(ScheduleEdit, "Shared Resources - Schedules", "Edit (includes creating and deleting)"),
            new(UserAudienceView, "Shared Resources - User Audiences", "View"),
            new(UserAudienceEdit, "Shared Resources - User Audiences", "Edit (includes creating and deleting)"),
            new(SelfServiceCategoryView, "Shared Resources - Self-service Categories", "View"),
            new(SelfServiceCategoryEdit, "Shared Resources - Self-service Categories", "Edit (includes creating and deleting)"),
            new(TagView, "Shared Resources - Tags", "View"),
            new(TagEdit, "Shared Resources - Tags", "Edit (includes creating and deleting)"),
            new(ImageCreate, "Imaging - Images", "Create image (UI and Imager)"),
            new(ImageEdit, "Imaging - Images", "Edit image (includes renaming and deleting)"),
            new(ImageView, "Imaging - Images", "View images"),
            new(ImageApprove, "Imaging - Images", "Approve image version"),
            new(ImageDeployUnapproved, "Imaging - Images", "Deploy unapproved version"),
            new(ImageCapture, "Imaging - Images", "Capture image (agent and Imager)"),
            new(ImagingAccess, "Imaging - Imager", "Access Imager (API)"),
            new(ImagerDeploy, "Imaging - Imager", "Deploy assigned image from Imager"),
            new(ImagerOverride, "Imaging - Imager", "Choose different image, profile, or drivers"),
            new(DeploymentRuleView, "Imaging - Deployment Rules", "View"),
            new(DeploymentRuleEdit, "Imaging - Deployment Rules", "Edit (includes creating and deleting)"),
            new(DeploymentProfileView, "Imaging - Deployment Profiles", "View"),
            new(DeploymentProfileEdit, "Imaging - Deployment Profiles", "Edit (includes creating and deleting)"),
            new(DriverPackView, "Imaging - Driver Packs", "View"),
            new(DriverPackEdit, "Imaging - Driver Packs", "Edit (includes creating and deleting)"),
            new(ImagePrepProfileView, "Imaging - Image Prep Profiles", "View"),
            new(OuNamingRuleView, "Imaging - OU Naming Rules", "View"),
            new(OuNamingRuleEdit, "Imaging - OU Naming Rules", "Edit"),
            new(ImagePrepProfileEdit, "Imaging - Image Prep Profiles", "Edit (includes creating and deleting)")
        ];
    }

    public static class UI
    {
        public static readonly DialogOptions StandardDialogBox = new DialogOptions
        {
            CloseButton = false,
            MaxWidth = MaxWidth.Medium,
            FullWidth = true
        };
    
        public const int DefaultPageSize = 50;
        public static string[] Limits = new[] { "10", "25", "100" };
    }
}
