using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace CloneDeploy.SiteServices;

public sealed record MulticastConnection(Guid SessionId, string SenderAddress, string GroupAddress, int Port);
public sealed record MulticastOffer(Guid Id, Guid Lease, SiteDownloadOffer File, MulticastConnection Connection,
    int Receivers, int MegabitsPerSecond, int Ttl);
public sealed record MulticastProgress(bool Transmitting, bool Complete, string? Error);
public sealed record MulticastControl(bool Start);
