namespace CloneDeploy_Server.Backend.Data.Models
{
    public class DeviceAttachment
    {
        public int Id { get; set; }

        public int DeviceId { get; set; }
        
        public int AttachmentId { get; set; }
        
        //navigation properties
        public Device? Device { get; set; }
        public Attachment? Attachment { get; set; }
    }
}