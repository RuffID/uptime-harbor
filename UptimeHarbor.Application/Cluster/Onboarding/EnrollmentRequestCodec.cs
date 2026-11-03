using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Application.Cluster.Onboarding;

public static class EnrollmentRequestCodec
{
    public static byte[] Encode(EnrollmentRequest request)
    {
        Validate(request);
        using MemoryStream stream = new();
        using (Utf8JsonWriter writer = new(stream))
        {
            // Имена полей фиксированы и отсортированы ordinal, секрет и transport envelope исключены.
            writer.WriteStartObject();
            writer.WriteString("action", "ReserveInvitation");
            writer.WriteString("attemptId", request.AttemptId.ToString("D"));
            writer.WriteString("clusterId", request.ClusterId.ToString("D"));
            writer.WriteString("csrDigest", request.CsrDigest);
            writer.WriteNumber("generation", request.Generation);
            writer.WriteString("invitationId", request.InvitationId.ToString("D"));
            writer.WriteString("nodeId", request.NodeId.ToString("D"));
            writer.WriteString("peerUri", request.PeerUri);
            writer.WriteString("spkiDigest", request.SpkiDigest);
            writer.WriteString("storageGeneration", request.StorageGeneration.ToString("D"));
            writer.WriteNumber("version", request.Version);
            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    public static string Digest(EnrollmentRequest request)
    {
        byte[] domain = Encoding.UTF8.GetBytes("UptimeHarbor.Enrollment.v1");
        return Convert.ToHexString(SHA256.HashData([.. domain, .. Encode(request)]));
    }

    public static void Validate(EnrollmentRequest request)
    {
        if (request.Version != 1 || request.Generation < 1)
        {
            throw new InvalidDataException("EnrollmentVersionUnsupported");
        }

        StorageGuards.Id(request.ClusterId, nameof(request.ClusterId));
        StorageGuards.Id(request.InvitationId, nameof(request.InvitationId));
        StorageGuards.Id(request.AttemptId, nameof(request.AttemptId));
        StorageGuards.Id(request.NodeId, nameof(request.NodeId));
        StorageGuards.Id(request.StorageGeneration, nameof(request.StorageGeneration));
        ClusterOperationValidation.ValidateAddress(request.PeerUri);
        if (StorageGuards.Digest(request.SpkiDigest) != request.SpkiDigest
            || StorageGuards.Digest(request.CsrDigest) != request.CsrDigest)
        {
            throw new InvalidDataException("EnrollmentDigestNotCanonical");
        }
    }
}
