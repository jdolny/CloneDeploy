// Adapter for the imported JPMikkers server/session protocol. See LICENSE.txt.
#nullable disable
using System.Net;
using System.Net.Sockets;

namespace CodePlex.JPMikkers.TFTP;

/// <summary>Cancellable transport; no callbacks execute under a socket lock.</summary>
public sealed class UDPSocket : IDisposable
{
    public delegate void OnReceiveDelegate(UDPSocket sender, IPEndPoint endPoint, ArraySegment<byte> data);
    public delegate void OnStopDelegate(UDPSocket sender, Exception reason);
    private readonly Socket _socket;
    private readonly CancellationTokenSource _stop = new();
    private int _disposed;
    public EndPoint LocalEndPoint { get; }
    public bool SendPending => false;

    public UDPSocket(IPEndPoint localEndPoint, int packetSize, bool dontFragment, short ttl,
        OnReceiveDelegate onReceive, OnStopDelegate onStop)
    {
        _socket = new Socket(localEndPoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            _socket.ExclusiveAddressUse = true;
            _socket.ReceiveBufferSize = 1024 * 1024;
            if (localEndPoint.AddressFamily == AddressFamily.InterNetwork) _socket.DontFragment = dontFragment;
            if (ttl >= 0) _socket.Ttl = ttl;
            _socket.Bind(localEndPoint);
            LocalEndPoint = _socket.LocalEndPoint;
            _ = ReceiveAsync(packetSize, onReceive, onStop, _stop.Token);
        }
        catch { _socket.Dispose(); _stop.Dispose(); throw; }
    }

    private async Task ReceiveAsync(int packetSize, OnReceiveDelegate onReceive, OnStopDelegate onStop, CancellationToken ct)
    {
        await Task.Yield();
        var buffer = new byte[packetSize];
        EndPoint source = new IPEndPoint(_socket.AddressFamily == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any, 0);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                SocketReceiveFromResult received;
                try { received = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, source, ct); }
                catch (SocketException ex) when (ex.SocketErrorCode is SocketError.ConnectionReset or SocketError.MessageSize) { continue; }
                onReceive(this, (IPEndPoint)received.RemoteEndPoint, new ArraySegment<byte>(buffer, 0, received.ReceivedBytes));
            }
        }
        catch (Exception) when (ct.IsCancellationRequested) { }
        catch (Exception ex) { onStop(this, ex); }
    }

    public void Send(IPEndPoint endPoint, ArraySegment<byte> data)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        try { _socket.SendTo(data, SocketFlags.None, endPoint); }
        catch (SocketException) { /* The session timer retries lost datagrams. */ }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _stop.Cancel();
        _socket.Dispose();
        _stop.Dispose();
    }
}
