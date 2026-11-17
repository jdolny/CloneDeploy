using System.Runtime.InteropServices;
using System.Text;
namespace CloneDeploy_Agent.MacOS.Portal;

// User login Keychain; no passwords or tokens are sent through a command line or plaintext file.
internal static class PortalKeychain
{
    private const int ItemNotFound = -25300;

    private const uint Utf8Encoding = 0x08000100;

    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string Core = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private static readonly IntPtr security = NativeLibrary.Load(Security);
    private static readonly IntPtr core = NativeLibrary.Load(Core);
    private static IntPtr Key(string name) => Marshal.ReadIntPtr(NativeLibrary.GetExport(security, name));
    [DllImport(Security)] private static extern int SecItemCopyMatching(IntPtr query, out IntPtr result);
    [DllImport(Security)] private static extern int SecItemAdd(IntPtr attributes, IntPtr result);
    [DllImport(Security)] private static extern int SecItemUpdate(IntPtr query, IntPtr attributes);
    [DllImport(Security)] private static extern int SecItemDelete(IntPtr query);
    [DllImport(Core)] private static extern IntPtr CFDictionaryCreateMutable(IntPtr allocator, nint capacity, IntPtr keys, IntPtr values);
    [DllImport(Core)] private static extern void CFDictionarySetValue(IntPtr dictionary, IntPtr key, IntPtr value);
    [DllImport(Core)] private static extern IntPtr CFStringCreateWithBytes(IntPtr allocator, byte[] bytes, nint length, uint encoding, byte external);
    [DllImport(Core)] private static extern IntPtr CFDataCreate(IntPtr allocator, byte[] bytes, nint length);
    [DllImport(Core)] private static extern nint CFDataGetLength(IntPtr data);
    [DllImport(Core)] private static extern IntPtr CFDataGetBytePtr(IntPtr data);
    [DllImport(Core)] private static extern void CFRelease(IntPtr value);
    private sealed class Query : IDisposable
    {
        public IntPtr Handle { get; } = CFDictionaryCreateMutable(IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero);
        private readonly List<IntPtr> owned = [];
        public void Set(string key, IntPtr value) => CFDictionarySetValue(Handle, Key(key), value);
        public void Text(string key, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value); var item = CFStringCreateWithBytes(IntPtr.Zero, bytes, bytes.Length, Utf8Encoding, 0);
            owned.Add(item); Set(key, item);
        }
        public void Data(string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            try { var item = CFDataCreate(IntPtr.Zero, bytes, bytes.Length); owned.Add(item); Set("kSecValueData", item); }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
        }
        public void Dispose() { CFRelease(Handle); foreach (var value in owned) CFRelease(value); }
    }
    private static Query For(string account)
    {
        var query = new Query(); query.Set("kSecClass", Key("kSecClassGenericPassword"));
        query.Text("kSecAttrService", "com.clonedeploy.agent.portal.session"); query.Text("kSecAttrAccount", account); return query;
    }
    public static string? Read(string account)
    {
        using var query = For(account);
        query.Set("kSecReturnData", Marshal.ReadIntPtr(NativeLibrary.GetExport(core, "kCFBooleanTrue")));
        var status = SecItemCopyMatching(query.Handle, out var data);
        if (status == ItemNotFound) return null;
        Require(status);
        try
        {
            var length = CFDataGetLength(data);
            if (length is <= 0 or > 128) throw new IOException(PortalText.Get("SignIn.InvalidSavedSession"));
            var bytes = new byte[(int)length];
            try { Marshal.Copy(CFDataGetBytePtr(data), bytes, 0, bytes.Length); return Encoding.UTF8.GetString(bytes); }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); }
        }
        finally { if (data != IntPtr.Zero) CFRelease(data); }
    }
    public static void Save(string account, string token)
    {
        using var query = For(account); using var updates = new Query(); updates.Data(token);
        var status = SecItemUpdate(query.Handle, updates.Handle);
        if (status == ItemNotFound) { query.Data(token); status = SecItemAdd(query.Handle, IntPtr.Zero); }
        Require(status);
    }
    public static void Delete(string account)
    {
        using var query = For(account); var status = SecItemDelete(query.Handle);
        if (status != ItemNotFound) Require(status);
    }
    private static void Require(int status) { if (status != 0) throw new IOException(PortalText.Get("SignIn.KeychainUnavailable")); }
}
