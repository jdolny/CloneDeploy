namespace CloneDeploy_Server.Backend.Services;

/// <summary>Preserves the legacy PXE discovery and inform handling on UDP 67.</summary>
public sealed class DHCPDataReceived
{
    public PxeReplyDto? Process(DHCPRequest dhcpRequest)
    {
        var requestType = dhcpRequest.GetMsgType();
        if (requestType != DHCPMsgType.DHCPDISCOVER && requestType != DHCPMsgType.DHCPINFORM)
            return null;

        var vendorId = dhcpRequest.GetVendorOptionData();
        if (vendorId is null || !vendorId.AsSpan().StartsWith("PXEClient"u8))
            return null;

        return ProcessPXERequest(dhcpRequest);
    }

    private static PxeReplyDto ProcessPXERequest(DHCPRequest dhcpRequest)
    {
        var replyOptions = new DHCPReplyOptions();
        replyOptions.OtherOptions.Add(DHCPOption.Vendorclassidentifier, PxePacketConfiguration.VendorClass);
        var reply = new DHCPReply(dhcpRequest);
        return reply.CreateReply(DHCPMsgType.DHCPOFFER, replyOptions, 68);
    }
}
