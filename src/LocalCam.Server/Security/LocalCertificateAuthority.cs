using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace LocalCam.Server.Security;

public sealed class LocalCertificateAuthority
{
    private const string AuthorityFile = "localcam-ca.pfx";
    private const string AuthorityCertificateFile = "localcam-ca.cer";
    private readonly string directory;

    public LocalCertificateAuthority(string directory)
    {
        this.directory = directory;
        Directory.CreateDirectory(directory);
    }

    public string PublicCertificatePath => Path.Combine(directory, AuthorityCertificateFile);

    public X509Certificate2 CreateServerCertificate(IEnumerable<IPAddress> addresses)
    {
        using var authority = LoadOrCreateAuthority();
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=LocalCam Local Server",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], true));

        var subjectAlternativeNames = new SubjectAlternativeNameBuilder();
        subjectAlternativeNames.AddDnsName("localhost");
        subjectAlternativeNames.AddDnsName(Environment.MachineName);
        foreach (var address in addresses.Append(IPAddress.Loopback).Distinct())
        {
            subjectAlternativeNames.AddIpAddress(address);
        }

        request.CertificateExtensions.Add(subjectAlternativeNames.Build());
        var serial = RandomNumberGenerator.GetBytes(16);
        var expires = DateTimeOffset.UtcNow.AddYears(1);
        if (expires > authority.NotAfter.ToUniversalTime()) expires = authority.NotAfter.ToUniversalTime();
        using var signed = request.Create(authority, DateTimeOffset.UtcNow.AddMinutes(-5), expires, serial);
        using var signedWithKey = signed.CopyWithPrivateKey(key);
        var pfx = signedWithKey.Export(X509ContentType.Pfx);
        return X509CertificateLoader.LoadPkcs12(pfx, password: null, X509KeyStorageFlags.Exportable);
    }

    private X509Certificate2 LoadOrCreateAuthority()
    {
        var authorityPath = Path.Combine(directory, AuthorityFile);
        if (File.Exists(authorityPath))
        {
            return X509CertificateLoader.LoadPkcs12FromFile(authorityPath, password: null, X509KeyStorageFlags.Exportable);
        }

        using var key = RSA.Create(4096);
        var request = new CertificateRequest(
            "CN=LocalCam Local CA",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign | X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(5));
        File.WriteAllBytes(authorityPath, certificate.Export(X509ContentType.Pfx));
        File.WriteAllBytes(PublicCertificatePath, certificate.Export(X509ContentType.Cert));
        return X509CertificateLoader.LoadPkcs12FromFile(authorityPath, password: null, X509KeyStorageFlags.Exportable);
    }
}
