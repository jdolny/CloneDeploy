namespace CloneDeploy_Server.Backend.Data.Models
{
    public class ImageTag
    {
        public int Id { get; set; }
        
        public int ImageId { get; set; }
        
        public int TagId { get; set; }
        
        //navigation properties
        public Image? Image { get; set; }
        public Tag? Tag { get; set; }
    }
}