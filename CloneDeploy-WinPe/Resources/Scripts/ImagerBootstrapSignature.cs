// Compiled by Windows PowerShell 5.1 in WinPE. Keep compatible with .NET Framework/C# 5.
using System;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

public static class ImagerBootstrapSignature
{
    public static byte[] Decode(string value)
    {
        return Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4));
    }
    public static string Hash(byte[] value)
    {
        using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(value)).Replace("-", "");
    }
    public static string Context(string nonce, string path)
    {
        return Hash(Encoding.UTF8.GetBytes("CloneDeploy/imager-bootstrap/v1\n" + nonce + "\n" + path));
    }
    public static void Verify(byte[] certificate, byte[] rootBytes, string input, byte[] signature)
    {
        using (var leaf = new X509Certificate2(certificate))
        using (var root = new X509Certificate2(rootBytes))
        using (var chain = new X509Chain())
        {
            var basic = leaf.Extensions.OfType<X509BasicConstraintsExtension>().SingleOrDefault();
            var usage = leaf.Extensions.OfType<X509KeyUsageExtension>().SingleOrDefault();
            var purposes = leaf.Extensions.OfType<X509EnhancedKeyUsageExtension>().SingleOrDefault();
            if (!leaf.SubjectName.RawData.SequenceEqual(new X500DistinguishedName("CN=CloneDeploy server message signing").RawData) ||
                basic == null || basic.CertificateAuthority || usage == null || usage.KeyUsages != X509KeyUsageFlags.DigitalSignature ||
                purposes == null || purposes.EnhancedKeyUsages.Count != 1 || purposes.EnhancedKeyUsages[0].Value != "1.3.6.1.5.5.7.3.3" ||
                leaf.PublicKey.Oid.Value != "1.2.840.10045.2.1" ||
                BitConverter.ToString(leaf.PublicKey.EncodedParameters.RawData) != "06-08-2A-86-48-CE-3D-03-01-07")
                throw new CryptographicException("Certificate cannot sign Imager manifests.");
            chain.ChainPolicy.ExtraStore.Add(root);
            chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.3"));
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            // Framework lacks CustomRootTrust. Allow an uninstalled root, then require the exact embedded CA.
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.AllowUnknownCertificateAuthority;
            if (!chain.Build(leaf) || chain.ChainElements.Count != 2 || !chain.ChainElements[1].Certificate.RawData.SequenceEqual(root.RawData))
                throw new CryptographicException("Untrusted Imager manifest signer.");
            using (var key = ECDsaCertificateExtensions.GetECDsaPublicKey(leaf))
                if (key == null || signature.Length != 64 || !key.VerifyData(Encoding.ASCII.GetBytes(input), signature, HashAlgorithmName.SHA256))
                    throw new CryptographicException("Imager manifest signature failed.");
        }
    }
}
