#nullable enable
using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace RemotePCControl.App.Infrastructure.Security;

public static class CertificateManager
{
    private static X509Certificate2? _cachedCert;
    private static readonly object SyncRoot = new();
    private static readonly byte[] Entropy = "RemotePCControl_Certificate_Salt"u8.ToArray();
    private static readonly string CertificatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RemotePCControl",
        "Security",
        "transport-cert.pfx.dat");

    public static X509Certificate2 GetOrCreateSelfSignedCertificate()
    {
        if (_cachedCert != null)
        {
            return _cachedCert;
        }

        lock (SyncRoot)
        {
            if (_cachedCert != null)
            {
                return _cachedCert;
            }

            try
            {
                if (File.Exists(CertificatePath))
                {
                    byte[] encryptedPfx = File.ReadAllBytes(CertificatePath);
                    byte[] decryptedPfx = ProtectedData.Unprotect(encryptedPfx, Entropy, DataProtectionScope.CurrentUser);
                    _cachedCert = X509CertificateLoader.LoadPkcs12(decryptedPfx, null, X509KeyStorageFlags.Exportable);
                    return _cachedCert;
                }

                using RSA rsa = RSA.Create(2048);
                var request = new CertificateRequest(
                    "CN=RemotePCControl-Internal",
                    rsa,
                    HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pkcs1);

                request.CertificateExtensions.Add(
                    new X509KeyUsageExtension(X509KeyUsageFlags.DataEncipherment | X509KeyUsageFlags.KeyEncipherment | X509KeyUsageFlags.DigitalSignature, false));

                request.CertificateExtensions.Add(
                    new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));

                DateTimeOffset now = DateTimeOffset.UtcNow;
                using X509Certificate2 certificate = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(1));

                byte[] pfxData = certificate.Export(X509ContentType.Pfx);
                SaveProtectedCertificate(pfxData);
                _cachedCert = X509CertificateLoader.LoadPkcs12(pfxData, null, X509KeyStorageFlags.Exportable);
                return _cachedCert;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[CertificateManager] Failed to create/load cert: {ex.Message}");
                throw;
            }
        }
    }

    private static void SaveProtectedCertificate(byte[] pfxData)
    {
        string? folder = Path.GetDirectoryName(CertificatePath);
        if (!string.IsNullOrWhiteSpace(folder))
        {
            Directory.CreateDirectory(folder);
        }

        byte[] encryptedPfx = ProtectedData.Protect(pfxData, Entropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(CertificatePath, encryptedPfx);
    }
}
