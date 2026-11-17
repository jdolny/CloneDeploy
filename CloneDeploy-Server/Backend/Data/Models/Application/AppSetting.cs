namespace CloneDeploy_Server.Backend.Data.Models
{
    public class AppSetting
    {
        public int Id { get; set; }
        
        public required string Name { get; set; }
        
        public string? Value { get; set; }
    }
}