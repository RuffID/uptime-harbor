using System.Formats.Asn1;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Infrastructure.Cluster.Onboarding;

/// <summary>Фиксированные application CA/leaf templates; CSR extensions не определяют политику выдачи.</summary>
public static class EnrollmentCertificates
{
    public const int MAX_DER_BYTES = 65536;
    private const string CLIENT_AUTH = "1.3.6.1.5.5.7.3.2";
    private const string SERVER_AUTH = "1.3.6.1.5.5.7.3.1";

    public static X509Certificate2 CreateIssuer(ECDsa key, Guid clusterId, long generation, Guid epoch, DateTimeOffset now)
    {
        ValidateKey(key);
        StorageGuards.Id(clusterId, nameof(clusterId));
        StorageGuards.Id(epoch, nameof(epoch));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(generation);
        StorageGuards.Utc(now);
        // X.509 имеет секундную точность; канонический шаблон хранит те же значения.
        DateTimeOffset start = Seconds(now);
        CertificateRequest request = new($"CN=UptimeHarbor Enrollment {epoch:D}", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        SubjectAlternativeNameBuilder names = new();
        names.AddUri(new Uri(IssuerIdentity(clusterId, generation, epoch)));
        request.CertificateExtensions.Add(names.Build());

        return request.CreateSelfSigned(start, start.AddYears(3));
    }

    public static CertificateRequest ReadCsr(ReadOnlySpan<byte> der, EnrollmentRequest expected)
    {
        EnrollmentRequestCodec.Validate(expected);
        if (der.IsEmpty || der.Length > MAX_DER_BYTES || Hash(der) != expected.CsrDigest)
        {
            throw new InvalidDataException("EnrollmentCsrDigestInvalid");
        }

        try
        {
            AsnReader envelope = new(der.ToArray(), AsnEncodingRules.DER);
            AsnReader body = envelope.ReadSequence();
            envelope.ThrowIfNotEmpty();
            body.ReadEncodedValue();
            AsnReader algorithm = body.ReadSequence();
            if (algorithm.ReadObjectIdentifier() != "1.2.840.10045.4.3.2")
            {
                throw new InvalidDataException("EnrollmentCsrAlgorithmInvalid");
            }

            algorithm.ThrowIfNotEmpty();
            body.ReadBitString(out _);
            body.ThrowIfNotEmpty();
            // Подпись проверяется; v1 принимает только CSR без запрашиваемых extensions.
            CertificateRequest csr = CertificateRequest.LoadSigningRequest(der, HashAlgorithmName.SHA256,
                out int consumed, CertificateRequestLoadOptions.UnsafeLoadCertificateExtensions);
            if (consumed != der.Length || csr.PublicKey.Oid.Value != "1.2.840.10045.2.1"
                || csr.CertificateExtensions.Count != 0)
            {
                throw new InvalidDataException("EnrollmentCsrInvalid");
            }

            using ECDsa key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(csr.PublicKey.ExportSubjectPublicKeyInfo(), out _);
            ValidateKey(key);
            if (Hash(key.ExportSubjectPublicKeyInfo()) != expected.SpkiDigest)
            {
                throw new InvalidDataException("EnrollmentCsrKeyMismatch");
            }

            return csr;
        }
        catch (CryptographicException)
        {
            throw new InvalidDataException("EnrollmentCsrInvalid");
        }
        catch (AsnContentException)
        {
            throw new InvalidDataException("EnrollmentCsrInvalid");
        }
    }

    public static X509Certificate2 IssueLeaf(X509Certificate2 issuer, EnrollmentRequest request,
        EnrollmentIssuance template, ReadOnlySpan<byte> csrDer)
    {
        ValidateTemplate(request, template);
        if (!ValidateIssuer(issuer, request.ClusterId, request.Generation, template.IssuerEpoch, template.NotBeforeUtc))
        {
            throw new InvalidDataException("EnrollmentIssuerInvalid");
        }

        if (template.NotAfterUtc.UtcDateTime > issuer.NotAfter.ToUniversalTime())
        {
            throw new InvalidDataException("EnrollmentLeafExceedsIssuerLifetime");
        }

        CertificateRequest csr = ReadCsr(csrDer, request);
        CertificateRequest leaf = new(new X500DistinguishedName($"CN=UptimeHarbor {request.NodeId:D}"),
            csr.PublicKey, HashAlgorithmName.SHA256);
        leaf.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        leaf.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        leaf.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new(CLIENT_AUTH), new(SERVER_AUTH) }, true));
        SubjectAlternativeNameBuilder names = new();
        names.AddUri(new Uri(NodeIdentity(request.ClusterId, request.Generation, request.NodeId)));
        names.AddUri(new Uri(BindingIdentity(request.StorageGeneration, template.IssueId)));
        string host = new Uri(request.PeerUri).IdnHost;
        if (IPAddress.TryParse(host, out IPAddress? address))
        {
            names.AddIpAddress(address);
        }
        else
        {
            names.AddDnsName(host);
        }

        leaf.CertificateExtensions.Add(names.Build());
        using ECDsa signer = issuer.GetECDsaPrivateKey() ?? throw new InvalidDataException("EnrollmentIssuerKeyMissing");
        byte[] serial = Convert.FromHexString(template.Serial);
        if (serial.All(item => item == 0))
        {
            throw new InvalidDataException("EnrollmentSerialInvalid");
        }

        return leaf.Create(issuer.SubjectName, X509SignatureGenerator.CreateForECDsa(signer),
            template.NotBeforeUtc, template.NotAfterUtc, serial);
    }

    public static bool ValidateIssuer(X509Certificate2 issuer, Guid clusterId, long generation, Guid epoch, DateTimeOffset now)
    {
        try
        {
            X509BasicConstraintsExtension? constraints = issuer.Extensions.OfType<X509BasicConstraintsExtension>().SingleOrDefault();
            X509KeyUsageExtension? usage = issuer.Extensions.OfType<X509KeyUsageExtension>().SingleOrDefault();
            bool fixedPolicy = constraints is { CertificateAuthority: true, Critical: true, HasPathLengthConstraint: true, PathLengthConstraint: 0 }
                && usage is { Critical: true } && usage.KeyUsages == (X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign);
            bool lifetime = now.UtcDateTime >= issuer.NotBefore.ToUniversalTime() && now.UtcDateTime < issuer.NotAfter.ToUniversalTime()
                && issuer.NotAfter.ToUniversalTime() <= issuer.NotBefore.ToUniversalTime().AddYears(3);
            if (!fixedPolicy || !lifetime || issuer.SignatureAlgorithm.Value != "1.2.840.10045.4.3.2"
                || issuer.SubjectName.RawData.AsSpan().SequenceEqual(issuer.IssuerName.RawData) == false
                || !ExactNames(issuer, [IssuerIdentity(clusterId, generation, epoch)], null))
            {
                return false;
            }

            using ECDsa? key = issuer.GetECDsaPublicKey();
            if (key is null || !IsP256(key))
            {
                return false;
            }

            return SelfSignatureValid(issuer, key) && ValidChain(issuer, issuer, now, null);
        }
        catch (CryptographicException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    public static bool ValidateLeaf(X509Certificate2 leaf, X509Certificate2 issuer, EnrollmentRequest request,
        EnrollmentIssuance template, string fingerprint, DateTimeOffset now, bool server)
    {
        try
        {
            ValidateTemplate(request, template);
            if (!ValidateIssuer(issuer, request.ClusterId, request.Generation, template.IssuerEpoch, now)
                || leaf.SignatureAlgorithm.Value != "1.2.840.10045.4.3.2"
                || Hash(leaf.RawData) != fingerprint || Hash(leaf.PublicKey.ExportSubjectPublicKeyInfo()) != template.SpkiDigest
                || leaf.NotBefore.ToUniversalTime() != template.NotBeforeUtc.UtcDateTime
                || leaf.NotAfter.ToUniversalTime() != template.NotAfterUtc.UtcDateTime
                || leaf.NotBefore.ToUniversalTime() < issuer.NotBefore.ToUniversalTime()
                || leaf.NotAfter.ToUniversalTime() > issuer.NotAfter.ToUniversalTime()
                || now < template.NotBeforeUtc || now >= template.NotAfterUtc
                || !leaf.SerialNumber.Equals(template.Serial, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            X509BasicConstraintsExtension? constraints = leaf.Extensions.OfType<X509BasicConstraintsExtension>().SingleOrDefault();
            X509KeyUsageExtension? usage = leaf.Extensions.OfType<X509KeyUsageExtension>().SingleOrDefault();
            X509EnhancedKeyUsageExtension? eku = leaf.Extensions.OfType<X509EnhancedKeyUsageExtension>().SingleOrDefault();
            bool policy = constraints is { CertificateAuthority: false, Critical: true, HasPathLengthConstraint: false }
                && usage is { Critical: true, KeyUsages: X509KeyUsageFlags.DigitalSignature }
                && eku is { Critical: true } && eku.EnhancedKeyUsages.Cast<Oid>().Select(item => item.Value)
                    .Order(StringComparer.Ordinal).SequenceEqual(new[] { SERVER_AUTH, CLIENT_AUTH }.Order(StringComparer.Ordinal));
            string host = new Uri(request.PeerUri).IdnHost;
            if (!policy || !ExactNames(leaf, [NodeIdentity(request.ClusterId, request.Generation, request.NodeId),
                BindingIdentity(request.StorageGeneration, template.IssueId)], host))
            {
                return false;
            }

            using ECDsa? key = leaf.GetECDsaPublicKey();
            if (key is null || !IsP256(key))
            {
                return false;
            }

            string application = CLIENT_AUTH;
            if (server)
            {
                application = SERVER_AUTH;
            }

            return ValidChain(leaf, issuer, now, application);
        }
        catch (Exception error) when (error is CryptographicException or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    public static void ValidateTemplate(EnrollmentRequest request, EnrollmentIssuance template)
    {
        EnrollmentRequestCodec.Validate(request);
        StorageGuards.Id(template.IssueId, nameof(template.IssueId));
        StorageGuards.Id(template.IssuerEpoch, nameof(template.IssuerEpoch));
        StorageGuards.Utc(template.NotBeforeUtc);
        StorageGuards.Utc(template.NotAfterUtc);
        bool identity = template.Version == 1 && template.NodeId == request.NodeId
            && template.StorageGeneration == request.StorageGeneration && template.PeerUri == request.PeerUri
            && template.SpkiDigest == request.SpkiDigest && template.CsrDigest == request.CsrDigest;
        bool time = template.NotBeforeUtc == Seconds(template.NotBeforeUtc) && template.NotAfterUtc == Seconds(template.NotAfterUtc)
            && template.NotAfterUtc > template.NotBeforeUtc
            && template.NotAfterUtc - template.NotBeforeUtc <= TimeSpan.FromDays(30).Add(TimeSpan.FromMinutes(2));
        bool serial = template.Serial.Length == 32 && template.Serial.All(char.IsAsciiHexDigit)
            && template.Serial != new string('0', 32);
        if (serial)
        {
            // DER INTEGER должен быть положительным и ровно 16 байт без добавления sign byte/обрезки нулей.
            byte first = Convert.FromHexString(template.Serial)[0];
            serial = first is > 0 and < 128;
        }

        if (!identity || !time || !serial)
        {
            throw new InvalidDataException("EnrollmentTemplateInvalid");
        }
    }

    private static bool ExactNames(X509Certificate2 certificate, string[] expectedUris, string? host)
    {
        X509Extension? extension = certificate.Extensions["2.5.29.17"];
        if (extension is null)
        {
            return false;
        }

        try
        {
            AsnReader input = new(extension.RawData, AsnEncodingRules.DER);
            AsnReader names = input.ReadSequence();
            input.ThrowIfNotEmpty();
            List<string> uris = [];
            int hosts = 0;
            while (names.HasData)
            {
                Asn1Tag tag = names.PeekTag();
                if (tag.HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 6)))
                {
                    uris.Add(names.ReadCharacterString(UniversalTagNumber.IA5String, tag));
                }
                else if (host is not null && tag.HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 2)))
                {
                    string dns = names.ReadCharacterString(UniversalTagNumber.IA5String, tag);
                    if (IPAddress.TryParse(host, out _) || !dns.Equals(host, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    hosts++;
                }
                else if (host is not null && tag.HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 7)))
                {
                    byte[] ip = names.ReadOctetString(tag);
                    if (!IPAddress.TryParse(host, out IPAddress? address) || !address.GetAddressBytes().AsSpan().SequenceEqual(ip))
                    {
                        return false;
                    }

                    hosts++;
                }
                else
                {
                    return false;
                }
            }

            int expectedHosts = 0;
            if (host is not null)
            {
                expectedHosts = 1;
            }

            return hosts == expectedHosts && uris.Order(StringComparer.Ordinal).SequenceEqual(expectedUris.Order(StringComparer.Ordinal));
        }
        catch (AsnContentException)
        {
            return false;
        }
    }

    private static bool ValidChain(X509Certificate2 leaf, X509Certificate2 issuer, DateTimeOffset now, string? application)
    {
        using X509Chain chain = new();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(issuer);
        chain.ChainPolicy.VerificationTime = now.UtcDateTime;
        chain.ChainPolicy.DisableCertificateDownloads = true;
        // Только dedicated CA: обязательную accepted revocation проверяет RPC boundary. Legacy CRL здесь не меняется.
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        if (application is not null)
        {
            chain.ChainPolicy.ApplicationPolicy.Add(new Oid(application));
        }

        return chain.Build(leaf);
    }

    private static bool SelfSignatureValid(X509Certificate2 certificate, ECDsa key)
    {
        try
        {
            AsnReader input = new(certificate.RawData, AsnEncodingRules.DER);
            AsnReader body = input.ReadSequence();
            input.ThrowIfNotEmpty();
            ReadOnlyMemory<byte> tbs = body.ReadEncodedValue();
            AsnReader algorithm = body.ReadSequence();
            if (algorithm.ReadObjectIdentifier() != "1.2.840.10045.4.3.2")
            {
                return false;
            }

            algorithm.ThrowIfNotEmpty();
            byte[] signature = body.ReadBitString(out int unused);
            body.ThrowIfNotEmpty();
            return unused == 0 && key.VerifyData(tbs.Span, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (AsnContentException)
        {
            return false;
        }
    }

    public static DateTimeOffset Seconds(DateTimeOffset value)
    {
        return new DateTimeOffset(value.Ticks - value.Ticks % TimeSpan.TicksPerSecond, TimeSpan.Zero);
    }

    public static string Hash(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private static string IssuerIdentity(Guid clusterId, long generation, Guid epoch)
    {
        return FormattableString.Invariant($"urn:uptimeharbor:issuer:{clusterId:D}:{generation}:{epoch:D}:{EnrollmentIssuer.PEER_PURPOSE}");
    }

    private static string NodeIdentity(Guid clusterId, long generation, Guid nodeId)
    {
        return string.Create(CultureInfo.InvariantCulture, $"urn:uptimeharbor:{clusterId:D}:{generation}:{nodeId:D}");
    }

    private static string BindingIdentity(Guid storage, Guid credential)
    {
        return $"urn:uptimeharbor:storage:{storage:D}:credential:{credential:D}";
    }

    private static bool IsP256(ECDsa key)
    {
        ECParameters parameters = key.ExportParameters(false);
        return key.KeySize == 256 && parameters.Curve.Oid.Value == ECCurve.NamedCurves.nistP256.Oid.Value;
    }

    private static void ValidateKey(ECDsa key)
    {
        if (!IsP256(key))
        {
            throw new InvalidDataException("EnrollmentKeyMustBeP256");
        }
    }
}
