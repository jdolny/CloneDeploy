namespace CloneDeploy_Agent.Contracts;

public sealed record SiteDirectDownload(string Url, string Address, string CertificateSha256, string Ticket)
{
    public const string CapabilityHeader = "X-CloneDeploy-Direct-Storage";
    public const string OfferHeader = "X-CloneDeploy-Direct-Offer";
    public const string BypassHeader = "X-CloneDeploy-Direct-Bypass";
    public const string TicketHeader = "X-CloneDeploy-Download-Ticket";
    public const string Path = "/api/site-download";
}

