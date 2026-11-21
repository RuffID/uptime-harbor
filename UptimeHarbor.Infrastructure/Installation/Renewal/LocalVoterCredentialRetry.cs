using System.Security.Cryptography;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;
using UptimeHarbor.Infrastructure.Installation.Renewal.Models;

namespace UptimeHarbor.Infrastructure.Installation.Renewal;

/// <summary>Append-only replacement of request metadata. The original protected key and CSR stay in intent.</summary>
internal static class LocalVoterCredentialRetry
{
    internal const string STAGE_PREFIX = "retry-";

    internal static (LocalVoterCredentialIntent Intent, ECDsa Key)? ReadIntent(
        LocalVoterCredentialFiles files, Guid installIdentity, string reference)
    {
        byte[]? bundle = files.Read(reference, "intent");
        if (bundle is null)
        {
            return null;
        }

        try
        {
            LocalVoterCredentialIntent intent = LocalVoterCredentialMaterial.Decode(bundle, installIdentity, reference, out ECDsa key);
            try
            {
                HashSet<string> remainingStages = files.ReadRetryStages(reference);
                long previousRejectionIndex = 0;
                while (remainingStages.Remove(Stage(intent.Request)))
                {
                    byte[] bytes = files.Read(reference, Stage(intent.Request))
                        ?? throw new InvalidDataException("VoterCredentialLocalRetryMissing");
                    Replacement replacement = StrictOnboardingJson.Decode<Replacement>(bytes);
                    VoterCredentialRequest old = intent.Request;
                    VoterCredentialRequest next = replacement.Request;
                    EnrollmentIssuance leaf = next.Renewal.Issuance;
                    VoterCredentialProofs.Digest(next);
                    if (!IsExactRejection(old, replacement.Rejection) || replacement.Rejection.Index <= previousRejectionIndex
                        || replacement.ReplacedAtUtc.Offset != TimeSpan.Zero || !IsExpired(old, replacement.ReplacedAtUtc)
                        || next != WithTemplate(old, next.OperationId, leaf.IssueId, leaf.Serial, replacement.ReplacedAtUtc)
                        || next.OperationId == old.OperationId || leaf.IssueId == old.Renewal.Issuance.IssueId
                        || leaf.Serial == old.Renewal.Issuance.Serial)
                    {
                        throw new InvalidDataException("VoterCredentialLocalRetryChanged");
                    }

                    intent = intent with { Request = next };
                    previousRejectionIndex = replacement.Rejection.Index;
                }

                if (remainingStages.Count > 0)
                {
                    throw new InvalidDataException("VoterCredentialLocalRetryOrphaned");
                }

                return (intent, key);
            }
            catch
            {
                key.Dispose();
                throw;
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bundle);
        }
    }

    internal static void Save(LocalVoterCredentialFiles files, LocalVoterCredentialIntent intent,
        RevisionImage image, DateTimeOffset now)
    {
        VoterCredentialRequest old = intent.Request;
        EnrollmentIssuance leaf = old.Renewal.Issuance;
        CommandResult? result = image.Results.GetValueOrDefault(old.OperationId);
        if (now.Offset != TimeSpan.Zero || !IsExpired(old, now) || result is null || !IsExactRejection(old, result)
            || result.Index > image.Index || result.Term > image.Term
            || image.OnboardingContracts?.VoterCredentialRotations?.Values.Any(item =>
                item.Renewal.Issuance.IssueId == leaf.IssueId || item.AuthorizationOperationId == old.OperationId
                || item.Renewal.Issuance.NodeId == leaf.NodeId
                && item.Renewal.PreviousCredentialId == old.Renewal.PreviousCredentialId) == true)
        {
            throw new UnauthorizedAccessException("VoterCredentialTerminalRejectionRequired");
        }

        byte[] serial = RandomNumberGenerator.GetBytes(16);
        serial[0] = (byte)((serial[0] & 0x3F) + 1);
        VoterCredentialRequest next = WithTemplate(old, Guid.NewGuid(), Guid.NewGuid(), Convert.ToHexString(serial), now);
        VoterCredentialProofs.Digest(next);
        VoterCredentialAuthorityPolicy.RequireCurrent(image, next, now);
        files.WriteOnce(intent.KeyReference, Stage(old), StrictOnboardingJson.Encode(new Replacement(result, now, next)));
    }

    private static bool IsExpired(VoterCredentialRequest request, DateTimeOffset now)
    {
        return now - request.Renewal.Issuance.NotAfterUtc.AddDays(-30) > TimeSpan.FromSeconds(10);
    }

    private static bool IsExactRejection(VoterCredentialRequest request, CommandResult result)
    {
        return result.OperationId == request.OperationId && result.Outcome == OperationOutcome.Rejected
            && result.Digest == RevisionCodec.Digest(VoterCredentialAuthorizationCommand.Create(request))
            && result.Index > 0 && result.Term > 0 && result.AppliedRevision == result.Index;
    }

    private static string Stage(VoterCredentialRequest request)
    {
        return STAGE_PREFIX + request.OperationId.ToString("N");
    }

    private static VoterCredentialRequest WithTemplate(VoterCredentialRequest request, Guid operationId,
        Guid issueId, string serial, DateTimeOffset now)
    {
        DateTimeOffset start = new(now.Ticks - now.Ticks % TimeSpan.TicksPerSecond, TimeSpan.Zero);
        return request with
        {
            OperationId = operationId,
            Renewal = request.Renewal with
            {
                Issuance = request.Renewal.Issuance with
                {
                    IssueId = issueId, Serial = serial, NotBeforeUtc = start.AddMinutes(-2), NotAfterUtc = start.AddDays(30)
                }
            }
        };
    }

    private sealed record Replacement(CommandResult Rejection, DateTimeOffset ReplacedAtUtc, VoterCredentialRequest Request);
}
