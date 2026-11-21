using System.Security.Cryptography;
using System.Formats.Asn1;
using System.Security.Cryptography.X509Certificates;
using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Infrastructure.Cluster;

public class PeerCertificateValidator
{
    public bool Validate(X509Certificate2 certificate, X509Certificate2 root, ClusterOptions options,
        PeerCredential credential, DateTimeOffset now, bool offlineRevocation = false)
    {
        return Validate(certificate, root, options, credential, now,
            (leaf, anchor, policy, checkedAt) => ValidateChain(leaf, anchor, policy, checkedAt, offlineRevocation));
    }

    internal bool Validate(X509Certificate2 certificate, X509Certificate2 root, ClusterOptions options,
        PeerCredential credential, DateTimeOffset now, Func<X509Certificate2, X509Certificate2, ClusterOptions, DateTimeOffset, bool> validateChain)
    {
        return GetIdentityError(certificate, root, options, credential, now) is null
            && validateChain(certificate, root, options, now);
    }

    public void EnsureLocalCertificate(ClusterOptions options, DateTimeOffset now, PeerCredential? accepted = null)
    {
        using X509Certificate2 root = LoadCertificate(
            () => X509CertificateLoader.LoadCertificateFromFile(options.ClientCaPath), "Cluster:ClientCaPath");
        using X509Certificate2 certificate = LoadCertificate(
            () => X509Certificate2.CreateFromPemFile(options.ClientCertificatePath, options.ClientKeyPath),
            "Cluster:ClientCertificatePath and Cluster:ClientKeyPath");
        PeerCredential identity = accepted ?? new(options.CredentialId, options.NodeId, options.StorageGeneration,
            new Uri(options.PublicUri), certificate.GetCertHashString(HashAlgorithmName.SHA256),
            false, false, DateTimeOffset.MaxValue);

        string? error = GetIdentityError(certificate, root, options, identity, now)
            ?? GetChainError(certificate, root, options, now, offlineRevocation: false);
        if (error is not null)
        {
            throw new InvalidDataException($"Local peer certificate for node {options.NodeId:D} was rejected. {error}");
        }
    }

    private static X509Certificate2 LoadCertificate(Func<X509Certificate2> load, string settings)
    {
        try
        {
            return load();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or CryptographicException or ArgumentException)
        {
            throw new InvalidDataException($"Could not load the local peer certificate material. Check {settings}.", error);
        }
    }

    private static string? GetIdentityError(X509Certificate2 certificate, X509Certificate2 root, ClusterOptions options,
        PeerCredential credential, DateTimeOffset now)
    {
        if (!string.Equals(root.GetCertHashString(HashAlgorithmName.SHA256), options.ClientCaFingerprint, StringComparison.OrdinalIgnoreCase))
        {
            return "The CA fingerprint does not match Cluster:ClientCaFingerprint.";
        }

        if (!string.Equals(certificate.GetCertHashString(HashAlgorithmName.SHA256), credential.Fingerprint, StringComparison.OrdinalIgnoreCase))
        {
            return "The certificate fingerprint does not match the accepted credential.";
        }

        if (now.UtcDateTime < certificate.NotBefore.ToUniversalTime() || now.UtcDateTime >= certificate.NotAfter.ToUniversalTime())
        {
            return $"The certificate is outside its validity period ({certificate.NotBefore.ToUniversalTime():O} to {certificate.NotAfter.ToUniversalTime():O}).";
        }

        if (now >= credential.ExpiresAtUtc)
        {
            return $"The accepted credential expired at {credential.ExpiresAtUtc:O}.";
        }

        X509EnhancedKeyUsageExtension? eku = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().SingleOrDefault();
        if (eku is null || !eku.EnhancedKeyUsages.Cast<Oid>().Any(x => x.Value == "1.3.6.1.5.5.7.3.2"))
        {
            return "The certificate is missing the client authentication EKU.";
        }

        X509Extension? san = certificate.Extensions["2.5.29.17"];
        string expected = $"urn:uptimeharbor:{options.ClusterId:D}:{options.Generation}:{credential.NodeId:D}";
        if (san is null || !HasIdentity(san.RawData, expected))
        {
            return $"The certificate is missing the required URI SAN {expected}.";
        }

        return null;
    }

    public bool ValidateChain(X509Certificate2 certificate, X509Certificate2 root, ClusterOptions options, DateTimeOffset now,
        bool offlineRevocation = false)
    {
        if (!string.Equals(root.GetCertHashString(HashAlgorithmName.SHA256), options.ClientCaFingerprint,
            StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return GetChainError(certificate, root, options, now, offlineRevocation) is null;
    }

    private static string? GetChainError(X509Certificate2 certificate, X509Certificate2 root, ClusterOptions options,
        DateTimeOffset now, bool offlineRevocation)
    {
        using X509Chain chain = new();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.2"));
        chain.ChainPolicy.VerificationTime = now.UtcDateTime;
        // В Nginx CRL обязателен; актуальный accepted registry проверяется на каждом запросе.
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        if (options.TlsBoundary == "Direct")
        {
            chain.ChainPolicy.RevocationMode = X509RevocationMode.Online;
            chain.ChainPolicy.UrlRetrievalTimeout = TimeSpan.FromSeconds(3);
            if (offlineRevocation)
            {
                chain.ChainPolicy.RevocationMode = X509RevocationMode.Offline;
                chain.ChainPolicy.DisableCertificateDownloads = true;
            }
        }

        if (chain.Build(certificate))
        {
            return null;
        }

        string statuses = string.Join(", ", chain.ChainStatus.Select(status => status.Status));
        return $"The certificate chain was rejected ({statuses}).";
    }

    public static bool HasIdentity(byte[] rawData, string expected)
    {
        try
        {
            AsnReader reader = new(rawData, AsnEncodingRules.DER);
            AsnReader names = reader.ReadSequence();
            reader.ThrowIfNotEmpty();
            bool found = false;
            while (names.HasData)
            {
                Asn1Tag tag = names.PeekTag();
                if (tag.HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 6)))
                {
                    string value = names.ReadCharacterString(UniversalTagNumber.IA5String, tag);
                    if (value == expected)
                    {
                        found = true;
                    }
                }
                else
                {
                    names.ReadEncodedValue();
                }
            }

            return found;
        }
        catch (AsnContentException)
        {
            return false;
        }
    }
}
