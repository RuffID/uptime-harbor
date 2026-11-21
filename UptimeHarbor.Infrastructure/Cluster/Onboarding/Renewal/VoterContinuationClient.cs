using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Installation;
using static UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal.VoterContinuationWire;

namespace UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;

/// <summary>Machine HTTPS exchange for a caller's exact, persisted continuation intent.</summary>
public sealed class VoterContinuationClient(EnrollmentTransport transport, EnrollmentEndpoints endpoints,
    IOptions<InstallationOptions> options, TimeProvider time)
{
    /// <summary>Reads only the exact continuation slice while the caller owns the startup transition.</summary>
    public Task<AcceptedSlice> ReadAcceptedSliceAsync(ManagedContinuationReplay replay, Intent intent,
        ECDsa previousKey, CancellationToken token)
    {
        return ReadSliceAsync(replay, intent, previousKey, null, token);
    }

    internal Task<AcceptedSlice> DiscoverBoundAsync(ManagedContinuationReplay replay, Intent original, ECDsa previousKey,
        BoundDiscovery discovery, CancellationToken token)
    {
        return ReadSliceAsync(replay, original, previousKey, discovery, token);
    }

    private async Task<AcceptedSlice> ReadSliceAsync(ManagedContinuationReplay replay, Intent intent,
        ECDsa previousKey, BoundDiscovery? discovery, CancellationToken token)
    {
        replay.RequireCurrent(token);
        VoterCredentialRequest request = intent.Request;
        EnrollmentIssuance leaf = request.Renewal.Issuance;
        RevisionImage image = replay.Image;
        VoterContinuationProcess process = intent.Process;
        bool currentBoot = process.BootId == replay.Process.BootId && process.BootChallenge == replay.Process.BootChallenge;
        if ((discovery is null) != currentBoot
            || request.ClusterId != image.ClusterId || request.Generation != image.Generation
            || intent.MembershipVersion != image.Operations.MembershipVersion || leaf.NodeId != replay.NodeId
            || image.Operations.Nodes.GetValueOrDefault(leaf.NodeId) is not { Voting: true, Removed: false } node
            || node.StorageGeneration != leaf.StorageGeneration || node.Address != leaf.PeerUri)
        {
            throw new InvalidDataException("VoterContinuationLocalReplayIdentityChanged");
        }

        var latest = VoterCredentialPolicy.Latest(image, leaf.NodeId);
        if (latest is { ContinuationRequestId: { } original } && latest.Renewal == request.Renewal
            && (request.BoundSource?.RequestId == original || request.OperationId == original))
        {
            latest = VoterCredentialPolicy.Latest(VoterContinuationBinding.BeforeBinding(image, leaf.IssueId), leaf.NodeId);
        }

        if ((latest?.CredentialRevision ?? 0) != request.Renewal.PreviousCredentialRevision
            || latest is not null && latest.Renewal.Issuance.IssueId != request.Renewal.PreviousCredentialId
            || EnrollmentCertificates.Hash(previousKey.ExportSubjectPublicKeyInfo()) != request.Renewal.PreviousSpkiDigest)
        {
            throw new InvalidDataException("VoterContinuationLocalReplayCredentialChanged");
        }

        Uri origin = endpoints.Origin(options.Value.CredentialContinuationOrigin + "/");
        ProjectionHead floor = VoterContinuationSlice.Head(image);
        VoterContinuationSlice.RequireAtLeast(floor, floor);
        long started = time.GetTimestamp();
        string votersDigest = VoterContinuationSlice.VotersDigest(image);
        VoterCredentialProof proof = CreateProof(intent);
        VoterCredentialProof signed = VoterCredentialContinuationProofs.SignChallenge(request, proof, previousKey,
            VoterContinuationAuthority.SLICE, intent.AudiencePeerAddress,
            VoterContinuationSlice.ReplayDigest(floor, replay.NativeTerm, votersDigest, discovery));
        SliceQuery query = new(new(request, signed, intent.CsrDer), floor, replay.NativeTerm, votersDigest) { Discovery = discovery };
        AcceptedSlice slice = await transport.PostContinuationAsync<SliceQuery, AcceptedSlice>(origin,
            VoterContinuationAuthority.SLICE, query, token);
        if (time.GetElapsedTime(started) >= TimeSpan.FromSeconds(10))
        {
            throw new InvalidDataException("VoterContinuationSliceExpired");
        }

        replay.Verify(slice, query, intent.AudiencePeerAddress, time.GetUtcNow(), token);
        return slice;
    }

    public async Task<CommandResult> RequestAsync(Intent intent, ECDsa previousKey, CancellationToken token)
    {
        var (origin, call) = await ProveAsync(intent, previousKey, VoterContinuationAuthority.REQUEST, token);
        CommandResult result = await transport.PostContinuationAsync<VoterCredentialProofRequest, CommandResult>(
            origin, VoterContinuationAuthority.REQUEST, call, token);
        RequireDecision(result, intent.Request.OperationId);
        return result;
    }

    public async Task<VoterContinuationStatus> ReadStatusAsync(Intent intent, ECDsa previousKey, CancellationToken token)
    {
        var (origin, call) = await ProveAsync(intent, previousKey, VoterContinuationAuthority.STATUS, token);
        VoterContinuationStatus status = await transport.PostContinuationAsync<VoterCredentialProofRequest, VoterContinuationStatus>(
            origin, VoterContinuationAuthority.STATUS, call, token);
        RequireDecision(status.Request, intent.Request.OperationId);
        if (status.AuthorizationDigest is { } digest)
        {
            StorageGuards.Digest(digest);
        }

        if (status.Registration is { } registration)
        {
            RequireDecision(registration, VoterContinuationCommands.OperationId(VoterContinuationCommands.REGISTER, intent.Request.OperationId));
            if (status.AuthorizationDigest is null || registration.Index <= status.Request.Index
                || registration.Term != status.Request.Term)
            {
                throw new InvalidDataException("VoterContinuationStatusChanged");
            }
        }

        return status;
    }

    public async Task<CommandResult> RegisterAsync(Intent intent, ECDsa previousKey, string authorizationDigest,
        CancellationToken token)
    {
        authorizationDigest = StorageGuards.Digest(authorizationDigest);
        var (origin, call) = await ProveAsync(intent, previousKey, VoterContinuationAuthority.REGISTER, token,
            authorizationDigest: authorizationDigest);
        CommandResult result = await transport.PostContinuationAsync<Registration, CommandResult>(origin,
            VoterContinuationAuthority.REGISTER, new(call, authorizationDigest), token);
        RequireDecision(result, VoterContinuationCommands.OperationId(VoterContinuationCommands.REGISTER, intent.Request.OperationId));
        return result;
    }

    public async Task<IssuedLeaf> IssueAsync(Intent intent, ECDsa previousKey, string registrationDigest, CancellationToken token)
    {
        registrationDigest = StorageGuards.Digest(registrationDigest);
        var (origin, call) = await ProveAsync(intent, previousKey, VoterContinuationAuthority.ISSUE, token,
            registrationDigest: registrationDigest);
        IssuedLeaf result = await transport.PostContinuationAsync<Issuance, IssuedLeaf>(origin,
            VoterContinuationAuthority.ISSUE, new(call, registrationDigest), token);
        RequireIdentity(intent, result.OperationId, result.IssueId);
        if (result.CertificateDer is not { Length: > 0 } || EnrollmentCertificates.Hash(result.CertificateDer) != result.CertificateDigest)
        {
            throw new InvalidDataException("VoterContinuationIssueReceiptChanged");
        }

        return result;
    }

    public async Task<BoundLeaf> BindAsync(Intent intent, ECDsa previousKey, string registrationDigest,
        string certificateDigest, CancellationToken token)
    {
        var (origin, call) = await ProveAsync(intent, previousKey, VoterContinuationAuthority.BIND, token,
            registrationDigest: registrationDigest, certificateDigest: certificateDigest);
        BoundLeaf result = await transport.PostContinuationAsync<Binding, BoundLeaf>(origin,
            VoterContinuationAuthority.BIND, new(call, registrationDigest, certificateDigest), token);
        RequireIdentity(intent, result.OperationId, result.IssueId);
        RequireDecision(result.Decision, VoterContinuationCommands.OperationId(VoterContinuationCommands.BIND, intent.Request.OperationId));
        if (result.CredentialRevision != result.Decision.Index)
        {
            throw new InvalidDataException("VoterContinuationBindReceiptChanged");
        }

        return result;
    }

    private async Task<(Uri Origin, VoterCredentialProofRequest Call)> ProveAsync(Intent intent, ECDsa previousKey,
        string purpose, CancellationToken token, string? authorizationDigest = null, string? registrationDigest = null,
        string? certificateDigest = null)
    {
        string configuredOrigin = options.Value.CredentialContinuationOrigin;
        Uri origin = endpoints.Origin(configuredOrigin + "/");
        string? decisionDigest = purpose == VoterContinuationAuthority.BIND
            ? VoterContinuationAuthority.BindChallengeDigest(registrationDigest, certificateDigest)
            : authorizationDigest ?? registrationDigest;
        VoterCredentialProof proof = CreateProof(intent);
        VoterCredentialProof challenge = VoterCredentialContinuationProofs.SignChallenge(intent.Request, proof, previousKey,
            purpose, intent.AudiencePeerAddress, decisionDigest);
        Nonce nonce = await transport.PostContinuationAsync<Challenge, Nonce>(origin, "nonce",
            new(new(intent.Request, challenge, intent.CsrDer), purpose, authorizationDigest, registrationDigest, certificateDigest),
            token);
        VoterCredentialProof signed = VoterCredentialContinuationProofs.Sign(intent.Request,
            proof with { Nonce = nonce.Value, IssuedAtUtc = time.GetUtcNow() }, previousKey);
        return (origin, new(intent.Request, signed, intent.CsrDer));
    }

    private VoterCredentialProof CreateProof(Intent intent)
    {
        EnrollmentIssuance leaf = intent.Request.Renewal.Issuance;
        VoterContinuationProcess process = intent.Process;
        return new(1, VoterCredentialContinuationProofs.SCOPE, leaf.NodeId, leaf.StorageGeneration, process.BootId,
            process.BootChallenge, process.Epoch, process.RecoveryVersion, intent.MembershipVersion,
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), time.GetUtcNow(), string.Empty);
    }

    private static void RequireIdentity(Intent intent, Guid operationId, Guid issueId)
    {
        if (operationId != intent.Request.OperationId || issueId != intent.Request.Renewal.Issuance.IssueId)
        {
            throw new InvalidDataException("VoterContinuationReceiptIdentityChanged");
        }
    }

    private static void RequireDecision(CommandResult? result, Guid operationId)
    {
        if (result is not { Outcome: OperationOutcome.Committed, Index: > 0, Term: > 0, Reason: null,
            ConflictId: null, ReplacedBy: null, SubjectUserId: null } || result.OperationId != operationId)
        {
            throw new InvalidDataException("VoterContinuationReceiptInvalid");
        }

        StorageGuards.Digest(result.Digest);
    }

    public sealed record Intent(VoterCredentialRequest Request, string CsrDer, VoterContinuationProcess Process,
        long MembershipVersion, string AudiencePeerAddress)
    {
        public override string ToString() => $"VoterContinuationIntent {Request.OperationId}";
    }
}
