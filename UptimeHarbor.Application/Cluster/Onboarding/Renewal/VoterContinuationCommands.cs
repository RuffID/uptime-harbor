using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Application.Cluster.Onboarding.Renewal;

public static class VoterContinuationCommands
{
    internal const string REQUEST = "RequestVoterContinuation";
    internal const string AUTHORIZE = "AuthorizeVoterContinuation";
    internal const string REGISTER = "RegisterVoterContinuationProcess";
    internal const string BIND = "BindVoterContinuation";

    public static bool IsContinuation(RevisionCommand command)
    {
        return command.Action is REQUEST or AUTHORIZE or REGISTER or BIND;
    }

    public static string IntentDigest(VoterCredentialRequest request)
    {
        VoterCredentialCommands.ValidateRenewal(request.Renewal);
        StorageGuards.Id(request.ClusterId, nameof(request.ClusterId));
        StorageGuards.Id(request.OperationId, nameof(request.OperationId));
        if (request.Version != 1 || request.Generation < 1)
        {
            throw new InvalidDataException("VoterCredentialRequestVersionInvalid");
        }

        return RevisionCodec.Digest(StrictOnboardingJson.Encode(new
        {
            Domain = "UptimeHarbor.VoterCredential.Intent.v1", Request = request
        }));
    }

    internal static void Validate(RevisionCommand command)
    {
        VoterContinuationCommand data = command.VoterContinuation ?? throw new InvalidDataException("VoterContinuationMissing");
        VoterContinuationContext context = data.Context;
        VoterCredentialRequest request = context.Request;
        bool requested = command.Action == REQUEST;
        bool validActor = command.ActorUserId != Guid.Empty && command.ActorRevision > 0;
        if (requested)
        {
            validActor = command.ActorUserId == Guid.Empty && command.ActorRevision == 0;
        }

        bool mixed = command.Onboarding is not null || command.OnlineLifecycle is not null || command.Operation is not null
            || command.EnrollmentTrust is not null || command.Value is not null || command.Monitoring is not null
            || command.Delivery is not null || command.Target is not null || command.Replacement is not null
            || command.AccountAction is not null || command.SystemSource is not null || command.ResolvesConflictId.HasValue;
        if (mixed || !validActor || command.Schema != 3 || command.ClusterId != request.ClusterId
            || command.ClusterGeneration != request.Generation || command.OriginNodeId != context.AudienceNodeId
            || command.OperationId != OperationId(command.Action, request.OperationId) || command.EntityKind != DeletedEntityKind.Node
            || command.EntityId != request.Renewal.Issuance.NodeId || command.BaseRevision != request.Renewal.PreviousCredentialRevision
            || context.RequestDigest != IntentDigest(request) || requested == (data.PriorDigest is not null)
            || (command.Action == AUTHORIZE) == (data.Evidence is not null)
            || (command.Action == BIND) != (data.Credential is not null))
        {
            throw new InvalidDataException("VoterContinuationEnvelopeInvalid");
        }

        if (data.Credential is { } credential)
        {
            VoterCredentialCommands.ValidateCredential(credential);
            EnrollmentIssuance leaf = request.Renewal.Issuance;
            if (command.OperationId == leaf.IssueId || credential.Id != leaf.IssueId || credential.NodeId != leaf.NodeId
                || credential.ExpiresAtUtc != leaf.NotAfterUtc || credential.RevokedAtUtc.HasValue
                || credential.AdmissionId.HasValue || credential.AdmissionExpiresAtUtc.HasValue || credential.ConsumedAtUtc.HasValue)
            {
                throw new InvalidDataException("VoterContinuationBindCredentialInvalid");
            }
        }

        if (context.BoundSource is { } source)
        {
            StorageGuards.Id(source.RequestId, nameof(source.RequestId));
            RequireDigest(source.BindDigest);
            if (source.RequestId == request.OperationId || source.CredentialRevision <= 0 || command.Action == BIND
                || context.Predecessor is not { CredentialContinuation: true } predecessor
                || context.Process.BootId == predecessor.BootId || context.Process.BootChallenge == predecessor.BootChallenge)
            {
                throw new InvalidDataException("VoterContinuationBoundSourceInvalid");
            }
        }

        StorageGuards.Id(context.AudienceStorage, nameof(context.AudienceStorage));
        StorageGuards.Id(context.PreviousIssuerEpoch, nameof(context.PreviousIssuerEpoch));
        StorageGuards.Id(context.Process.BootId, nameof(context.Process.BootId));
        if (context.Term <= 0 || context.MembershipVersion <= 0 || context.RecoveryVersion < 0
            || context.PreviousIssuerRevision <= 0 || context.IssuerRevision <= 0 || context.Process.Epoch <= 0
            || context.Process.RecoveryVersion < 0 || !IsChallenge(context.Process.BootChallenge))
        {
            throw new InvalidDataException("VoterContinuationContextInvalid");
        }

        if (context.Predecessor is { } previous && (previous.NodeId != command.EntityId || previous.Index <= 0
            || previous.GrantId == Guid.Empty || previous.BootId == Guid.Empty || !IsChallenge(previous.BootChallenge)
            || previous.ProcessEpoch <= 0 || previous.RecoveryVersion < 0 || previous.LeaseUntilUtc.Offset != TimeSpan.Zero))
        {
            throw new InvalidDataException("VoterContinuationPredecessorInvalid");
        }

        if (data.PriorDigest is not null)
        {
            RequireDigest(data.PriorDigest);
        }

        if (data.Evidence is { } evidence)
        {
            RequireDigest(evidence.ProofDigest);
            if (!IsChallenge(evidence.Nonce))
            {
                throw new InvalidDataException("VoterContinuationNonceInvalid");
            }
        }

        if (data.Acceptance is { } stamp)
        {
            StorageGuards.Utc(stamp.AtUtc);
            if (stamp.MinimumIndex <= 0 || stamp.Term != context.Term || stamp.MembershipVersion != context.MembershipVersion
                || stamp.RecoveryVersion != context.RecoveryVersion)
            {
                throw new InvalidDataException("VoterContinuationAcceptanceInvalid");
            }
        }
    }

    internal static Guid OperationId(string action, Guid requestId)
    {
        if (action == REQUEST)
        {
            return requestId;
        }

        string digest = RevisionCodec.Digest(StrictOnboardingJson.Encode(new
        {
            Domain = "UptimeHarbor.VoterCredential.ContinuationDecision.v1", Action = action, RequestId = requestId
        }));
        return new Guid(Convert.FromHexString(digest[..32]));
    }

    private static void RequireDigest(string digest)
    {
        if (StorageGuards.Digest(digest) != digest)
        {
            throw new InvalidDataException("VoterContinuationDigestInvalid");
        }
    }

    private static bool IsChallenge(string value)
    {
        Span<byte> bytes = stackalloc byte[32];
        return value.Length == 44 && Convert.TryFromBase64String(value, bytes, out int written)
            && written == bytes.Length && value == Convert.ToBase64String(bytes);
    }
}
