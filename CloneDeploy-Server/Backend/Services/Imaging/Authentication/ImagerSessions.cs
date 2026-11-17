using System.Security.Cryptography;
using CloneDeploy_Agent.Contracts;

namespace CloneDeploy_Server.Backend.Services;

// Restart loses all keys and rejects old packets, including replays. Keys never leave main/Imager.
public sealed class ImagerSessions : IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<string, Session> sessions = [];
    private DateTime window = DateTime.UtcNow;
    private int created;
    public sealed class Session(ImagerSessionProtocol.Keys keys, DateTime expires) : IDisposable
    {
        public ImagerSessionProtocol.Keys Keys { get; } = keys;
        public DateTime Expires { get; } = expires;
        private readonly HashSet<long> seen = [];
        private long high;
        public void Accept(long sequence)
        {
            lock (seen)
            {
                if (sequence <= 0 || sequence <= high - 4096 || !seen.Add(sequence)) throw new CryptographicException("Replayed Imager request.");
                high = Math.Max(high, sequence);
                seen.RemoveWhere(x => x <= high - 4096);
            }
        }
        public void Dispose() { CryptographicOperations.ZeroMemory(Keys.Request); CryptographicOperations.ZeroMemory(Keys.Response); }
    }
    public ImagerSessionProtocol.Welcome Create(ImagerSessionProtocol.Hello hello)
    {
        if (!ServerSignatures.ValidNonce(hello.Nonce) || hello.PublicKey is not { Length: > 0 and < 256 }) throw new CryptographicException("Invalid handshake.");
        lock (gate)
        {
            var now = DateTime.UtcNow;
            foreach (var entry in sessions.Where(x => x.Value.Expires <= now).ToArray()) { sessions.Remove(entry.Key); entry.Value.Dispose(); }
            if (now - window >= TimeSpan.FromMinutes(1)) { window = now; created = 0; }
            if (++created > 120 || sessions.Count >= 1024) throw new InvalidOperationException("Imager session capacity exceeded. Retry later.");
            using var key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
            var reply = new ImagerSessionProtocol.Welcome(ServerSignatures.Nonce(), Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), hello.Nonce,
                new DateTimeOffset(now).AddSeconds(ImagerSessionProtocol.LifetimeSeconds).ToUnixTimeSeconds());
            sessions.Add(reply.Id, new(ImagerSessionProtocol.Derive(key, hello.PublicKey, hello, reply), DateTimeOffset.FromUnixTimeSeconds(reply.Expires).UtcDateTime));
            return reply;
        }
    }
    public (Session Session, ImagerSessionProtocol.Request Request) Open(ImagerSessionProtocol.Packet packet)
    {
        lock (gate)
        {
            if (!sessions.TryGetValue(packet.Id, out var session) || session.Expires <= DateTime.UtcNow) throw new CryptographicException("Expired Imager session.");
            // Authenticate before touching the replay window, and consume before dispatching any operation.
            var request = ImagerSessionProtocol.Open<ImagerSessionProtocol.Request>(session.Keys.Request, packet, "request");
            session.Accept(packet.Sequence);
            return (session, request);
        }
    }
    public void Dispose() { lock (gate) { foreach (var session in sessions.Values) session.Dispose(); sessions.Clear(); } }
}
