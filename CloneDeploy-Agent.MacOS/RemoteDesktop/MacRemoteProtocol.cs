namespace CloneDeploy_Agent.MacOS;
public sealed record MacRemoteAttach(Guid Session, Guid Client);
public static class MacRemoteProtocol
{
    public const string SocketPath = "/var/run/clonedeploy-portal/remote.sock";
}
