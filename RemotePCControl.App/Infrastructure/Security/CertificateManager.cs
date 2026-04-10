#nullable enable
using System;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace RemotePCControl.App.Infrastructure.Security;

public static class CertificateManager
{
    private static X509Certificate2? _cachedCert;

    public static X509Certificate2 GetOrCreateSelfSignedCertificate()
    {
        if (_cachedCert != null) return _cachedCert;

        try
        {
            // 자가 서명 인증서 생성 루틴
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

            // 유효 기간 1년
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var certificate = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(1));
            
            // Windows에서 SslStream이 동작하려면 비공개 키가 포함된 exportable certificate여야 함
            byte[] pfxData = certificate.Export(X509ContentType.Pfx);
            _cachedCert = X509CertificateLoader.LoadPkcs12(pfxData, null, X509KeyStorageFlags.Exportable);
            return _cachedCert;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CertificateManager] Failed to create cert: {ex.Message}");
            throw;
        }
    }
}
