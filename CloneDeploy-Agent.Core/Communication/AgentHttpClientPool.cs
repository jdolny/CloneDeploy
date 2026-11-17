using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace CloneDeploy_Agent;

/// <summary>Shares connections while each operation retains its own headers, limits and cancellation.</summary>
internal sealed class AgentHttpClientPool(AgentOptions options) : IDisposable
{
    private const int MaximumResponseBytes = 65536;

    private static readonly TimeSpan ConnectionLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private sealed class Pool(HttpMessageHandler handler, X509Certificate2? certificate, string key)
    {
        public readonly HttpMessageHandler Handler = handler;
        public readonly X509Certificate2? Certificate = certificate;
        public readonly string Key = key;
        public readonly DateTimeOffset CreatedAt = DateTimeOffset.UtcNow;
        public int Users;
        public bool Retired;

        public void Dispose()
        {
            Handler.Dispose();
            Certificate?.Dispose();
        }
    }

    private readonly object gate = new();
    private Pool? current;
    private bool disposed;

    public HttpClient Create(X509Certificate2? certificate = null)
    {
        // Check configuration even when an existing TLS connection could be reused.
        AgentServerTrust.ValidateConfiguration(options);
        var server = options.GetServerUri();
        var rootHash = string.IsNullOrWhiteSpace(options.ServerCaCertificatePath)
            ? null
            : Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(options.ServerCaCertificatePath)));
        var key = string.Join(
            "|",
            server.AbsoluteUri,
            rootHash,
            certificate?.GetCertHashString(HashAlgorithmName.SHA256));

        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            // Periodically refresh the pool so a busy client can discover endpoint changes.
            if (current is null || current.Key != key
                || DateTimeOffset.UtcNow - current.CreatedAt >= ConnectionLifetime)
            {
                var ownedCertificate = certificate is null ? null : new X509Certificate2(certificate);
                var handler = new HttpClientHandler
                {
                    UseCookies = false,
                    AllowAutoRedirect = false
                };

                try
                {
                    AgentServerTrust.Configure(handler, options);

                    if (ownedCertificate is not null)
                        handler.ClientCertificates.Add(ownedCertificate);

                    var replacement = new Pool(new AgentResponseVerification(handler, options), ownedCertificate, key);
                    RetireCurrent();
                    current = replacement;
                }
                catch
                {
                    handler.Dispose();
                    ownedCertificate?.Dispose();
                    throw;
                }
            }

            var pool = current;
            pool.Users++;

            return new LeaseClient(pool.Handler, () => Release(pool))
            {
                BaseAddress = server,
                Timeout = RequestTimeout,
                MaxResponseContentBufferSize = MaximumResponseBytes
            };
        }
    }

    private sealed class LeaseClient(HttpMessageHandler handler, Action release) : HttpClient(
        handler,
        disposeHandler: false)
    {
        private Action? releasePool = release;

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            if (disposing)
                Interlocked.Exchange(ref releasePool, null)?.Invoke();
        }
    }

    private void Release(Pool pool)
    {
        lock (gate)
        {
            if (--pool.Users == 0 && pool.Retired)
                pool.Dispose();
        }
    }

    private void RetireCurrent()
    {
        if (current is not { } pool)
            return;

        current = null;
        pool.Retired = true;

        if (pool.Users == 0)
            pool.Dispose();
    }

    public void Invalidate()
    {
        lock (gate)
            RetireCurrent();
    }

    public void Dispose()
    {
        lock (gate)
        {
            disposed = true;
            RetireCurrent();
        }
    }
}
