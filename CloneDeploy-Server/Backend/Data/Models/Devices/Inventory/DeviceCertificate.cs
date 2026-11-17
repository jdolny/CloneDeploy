namespace CloneDeploy_Server.Backend.Data.Models
{
    public class DeviceCertificate
    {
        public int Id { get; set; }
        
        public int DeviceId { get; set; }
        
        public int CertificateId { get; set; }
        
        //navigation properties
        public Device? Device { get; set; }
        public Certificate? Certificate { get; set; }
    }
}