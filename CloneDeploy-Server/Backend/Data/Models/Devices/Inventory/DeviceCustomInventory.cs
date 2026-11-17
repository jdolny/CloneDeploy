namespace CloneDeploy_Server.Backend.Data.Models
{
    public class DeviceCustomInventory
    {
         public int Id { get; set; }
         
         public int DeviceId { get; set; }
         
         public int ScriptActionId { get; set; }
         
         public string? Value { get; set; }
         
         //navigation properties
         public Device? Device { get; set; }
         public ScriptAction? ScriptAction { get; set; }
         
    }
}
