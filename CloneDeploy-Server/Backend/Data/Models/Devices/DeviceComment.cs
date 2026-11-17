namespace CloneDeploy_Server.Backend.Data.Models
{
    public class DeviceComment
    {
        public int Id { get; set; }
        
        public int DeviceId { get; set; }
        
        public int CommentId { get; set; }   
        
        //navigation properties
        public Device? Device { get; set; }
        public Comment? Comment { get; set; }
    }
}