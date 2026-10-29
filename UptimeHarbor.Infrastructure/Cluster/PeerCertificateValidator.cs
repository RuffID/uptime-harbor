using System.Security.Cryptography;
using System.Formats.Asn1;
using System.Security.Cryptography.X509Certificates;
using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Infrastructure.Cluster;

public class PeerCertificateValidator
{
    public bool Validate(X509Certificate2 certificate, X509Certificate2 root, ClusterOptions options,
        PeerCredential credential, DateTimeOffset now)
    {
        string rootHash = root.GetCertHashString(HashAlgorithmName.SHA256);
        string hash = certificate.GetCertHashString(HashAlgorithmName.SHA256);
        if (!string.Equals(rootHash, options.ClientCaFingerprint, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(hash, credential.Fingerprint, StringComparison.OrdinalIgnoreCase)
            || now.UtcDateTime < certificate.NotBefore.ToUniversalTime()
            || now.UtcDateTime >= certificate.NotAfter.ToUniversalTime()
            || now >= credential.ExpiresAtUtc)
        {
            return false;
        }

        X509EnhancedKeyUsageExtension? eku = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().SingleOrDefault();
        if (eku is null || !eku.EnhancedKeyUsages.Cast<Oid>().Any(x => x.Value == "1.3.6.1.5.5.7.3.2"))
        {
            return false;
        }

        X509Extension? san = certificate.Extensions["2.5.29.17"];
        if (san is null)
        {
            return false;
        }

        string expected = $"urn:uptimeharbor:{options.ClusterId:D}:{options.Generation}:{credential.NodeId:D}";
        if (!HasIdentity(san.RawData, expected))
        {
            return false;
        }

        return ValidateChain(certificate, root, options, now);
    }

    public bool ValidateChain(X509Certificate2 certificate, X509Certificate2 root, ClusterOptions options, DateTimeOffset now)
    {
        if (!string.Equals(root.GetCertHashString(HashAlgorithmName.SHA256), options.ClientCaFingerprint,
            StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

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
        }

        return chain.Build(certificate);
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
