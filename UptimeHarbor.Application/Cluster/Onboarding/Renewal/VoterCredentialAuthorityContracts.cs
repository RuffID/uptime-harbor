using System.Text.Json.Serialization;
using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Application.Cluster.Onboarding.Renewal;

/// <summary>Immutable voter intent. OperationId/IssueId не зависят от процесса или лидера.</summary>
public record VoterCredentialRequest(int Version, Guid ClusterId, long Generation, Guid OperationId,
    VoterCredentialRenewal Renewal)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public VoterContinuationBoundSource? BoundSource { get; init; }

    public override string ToString()
    {
        return $"VoterCredentialRequest {OperationId}";
    }
}

/// <summary>Отдельный operational-key PoP; не invitation, admission или installation proof.</summary>
public record VoterCredentialProof(int Version, string Scope, Guid OriginNodeId, Guid OriginStorageGeneration,
    Guid BootId, string BootChallenge, long ProcessEpoch, long RecoveryVersion, long MembershipVersion,
    string Nonce, DateTimeOffset IssuedAtUtc, string Signature)
{
    public override string ToString()
    {
        return "VoterCredentialProof";
    }
}

public record VoterCredentialProofRequest(VoterCredentialRequest Request, VoterCredentialProof Proof, string CsrDer)
{
    public override string ToString()
    {
        return $"VoterCredentialProofRequest {Request.OperationId}";
    }
}

/// <summary>Только exact accepted authorization, без автоматического Bind/Ready.</summary>
public record VoterCredentialSigningRequest(int Version, VoterCredentialProofRequest ProofRequest, long AuthorizationIndex)
{
    public override string ToString()
    {
        return $"VoterCredentialSigningRequest {ProofRequest.Request.OperationId}";
    }
}

/// <summary>Адаптер обязан аутентифицировать subject voter и обращаться только к accepted owner epoch.</summary>
public interface IVoterCredentialSignerClient
{
    Task<EnrollmentIssuedArtifact> IssueAsync(Guid issuerNodeId, VoterCredentialSigningRequest request, CancellationToken token);
}

/// <summary>Подготовка не принимает команду и не открывает production dispatcher.</summary>
public interface IVoterCredentialRenewalAuthority
{
    ValueTask<RevisionCommand> PrepareAuthorizationAsync(VoterCredentialProofRequest request,
        Guid authenticatedNodeId, Guid authenticatedStorage, CancellationToken token);
}
