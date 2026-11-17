namespace CloneDeploy_Imager.Api;

internal static class MulticastPortLimits
{
    // udpcast uses an even data port and the following port for control traffic.
    public const int MinimumReceiverPort = 1024;
    public const int MaximumReceiverPort = 65534;
}
