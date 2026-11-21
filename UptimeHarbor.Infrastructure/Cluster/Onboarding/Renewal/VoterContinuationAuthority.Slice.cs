using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Domain.Cluster;
using static UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal.VoterContinuationWire;

namespace UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;

public partial class VoterContinuationAuthority
{
    public const string SLICE = "accepted-slice";

    public async Task<byte[]> ReadAcceptedSliceAsync(SliceQuery query, CancellationToken token)
    {
        var cut = await audience.ReadAsync(token);
        if (cut.Profile.Mode != "Bootstrap" || options.Value.TlsBoundary != "Direct"
            || cut.Image.RecoveryBaseline is not null || cut.Image.OnboardingContracts?.GenesisInitialized != true)
        {
            throw new UnauthorizedAccessException("VoterContinuationManagedDirectRequired");
        }

        VoterCredentialProofRequest call = query.Call;
        RequireProofContext(cut, call.Request, call.Proof);
        ProjectionHead head = VoterContinuationSlice.Head(cut.Image);
        AcceptedVoterContinuation? accepted = Find(cut.Image, call.Request.OperationId);
        DateTimeOffset now = time.GetUtcNow();
        VoterContinuationSlice.RequirePrefix(cut.Image, query.Replay);
        if (query.NativeTerm < query.Replay.Term || query.NativeTerm > cut.Leader.Term
            || query.VotersDigest != VoterContinuationSlice.VotersDigest(cut.Image))
        {
            throw new UnauthorizedAccessException("VoterContinuationReplayAuthorityChanged");
        }

        var history = ReadBoundHistory(cut, query, accepted);
        bool discovery = query.Discovery is not null;
        RevisionImage proofImage = accepted?.Bind is not null
            ? VoterContinuationBinding.BeforeBinding(cut.Image, call.Request.Renewal.Issuance.IssueId)
            : cut.Image;
        string digest = VoterContinuationSlice.ReplayDigest(query.Replay, query.NativeTerm, query.VotersDigest, query.Discovery);
        AcceptedEnrollmentIssuer previousIssuer;
        if (discovery)
        {
            previousIssuer = VoterCredentialContinuationProofs.RequireBoundDiscovery(proofImage, call, now, SLICE,
                cut.Profile.PeerAddress!, digest);
            RequireExactProof(history!.Requested.Command, call, previousIssuer);
        }
        else
        {
            previousIssuer = VoterCredentialContinuationProofs.RequireChallenge(proofImage, call, now, SLICE,
                cut.Profile.PeerAddress!, digest);
            RequireSliceStages(cut, call, previousIssuer, accepted, proofImage, now);
        }

        VoterCredentialRenewal renewal = call.Request.Renewal;
        EnrollmentIssuance leaf = renewal.Issuance;
        AcceptedEnrollmentIssuer? issuer = cut.Image.OnboardingContracts.Issuers?.GetValueOrDefault(leaf.IssuerEpoch);
        if (issuer is not { RevokedAtUtc: null, RetiredAtUtc: null, Purpose: EnrollmentIssuer.PEER_PURPOSE }
            || issuer.NodeId != cut.Leader.NodeId || issuer.StorageGeneration != cut.Profile.StorageGeneration
            || now < issuer.NotBeforeUtc || now >= issuer.NotAfterUtc || now >= leaf.NotAfterUtc)
        {
            throw new UnauthorizedAccessException("VoterContinuationLocalIssuerRequired");
        }

        OnlineProcessLease? process = cut.Image.OnboardingContracts.OnlineProcesses?.GetValueOrDefault(leaf.NodeId);
        Stage? processRegistration = null;
        if (discovery)
        {
            processRegistration = ReadBoundProcess(cut, history!, process);
        }

        byte[] response = StrictOnboardingJson.Encode(new AcceptedSlice(
            VoterContinuationSlice.QueryDigest(query), head, cut.Leader.Term, cut.Recovery, cut.Leader.MembershipVersion,
            cut.Image.Operations.Nodes[leaf.NodeId], cut.Image.Operations.Nodes[cut.Leader.NodeId],
            cut.Image.Operations.Credentials[renewal.PreviousCredentialId], previousIssuer, issuer, process,
            ReadStage(cut, discovery ? null : accepted?.Request), ReadStage(cut, discovery ? null : accepted?.Authorization),
            ReadStage(cut, discovery ? null : accepted?.Registration), ReadStage(cut, discovery ? null : accepted?.Bind))
        {
            History = history,
            ProcessRegistration = processRegistration
        });
        if (response.Length > EnrollmentTransport.MAX_BYTES)
        {
            throw new InvalidDataException("VoterContinuationSliceLimit");
        }

        // Recheck the original cut after serialization. Loss of quorum or any apply invalidates this response.
        await barrier.ConfirmAsync(token);
        audience.RequireUnchanged(cut, cut.Image, token);
        if (time.GetUtcNow() - call.Proof.IssuedAtUtc >= TimeSpan.FromSeconds(10)
            || !replay.Accept(new Guid(Convert.FromBase64String(call.Proof.Nonce).AsSpan(0, 16))))
        {
            throw new UnauthorizedAccessException("VoterContinuationSliceProofExpiredOrReused");
        }

        return response;
    }

    private void RequireSliceStages(VoterContinuationAudience.Cut cut, VoterCredentialProofRequest call,
        AcceptedEnrollmentIssuer previousIssuer, AcceptedVoterContinuation? accepted, RevisionImage proofImage, DateTimeOffset now)
    {
        if (accepted is null)
        {
            if (VoterContinuationSlice.Actions.Any(action =>
                cut.Image.Results.ContainsKey(VoterContinuationCommands.OperationId(action, call.Request.OperationId))))
            {
                throw new UnauthorizedAccessException("VoterContinuationAcceptedStageChanged");
            }

            CheckRequest(cut, call, previousIssuer);
        }
        else
        {
            var beforeBind = cut with { Image = proofImage };
            if (call.Request.BoundSource is not null)
            {
                ReadExact(cut, call, previousIssuer);
                if (accepted.Registration is { } recovery)
                {
                    RequireApplicable(cut.Image, recovery, now, replay: true);
                }
            }
            else if (accepted.Registration is { } registered)
            {
                RequireIssuance(beforeBind, call, previousIssuer, Committed(cut, registered).Digest);
            }
            else
            {
                ReadExact(beforeBind, call, previousIssuer);
            }

            if (accepted.Bind is { } bound)
            {
                RequireBinding(cut.Image, bound, now, replay: true);
            }
        }
    }

    private static BoundHistory? ReadBoundHistory(VoterContinuationAudience.Cut cut, SliceQuery query,
        AcceptedVoterContinuation? accepted)
    {
        var request = query.Call.Request;
        var source = request.BoundSource;
        if (query.Discovery is null && source is null)
        {
            return null;
        }

        AcceptedVoterContinuation? original = accepted;
        if (source is not null)
        {
            if (query.Discovery is not null || accepted?.Bind is not null
                || !VoterContinuationBoundRecovery.TryResolveBind(cut.Image, request, out _))
            {
                throw new UnauthorizedAccessException("VoterContinuationBoundSourceChanged");
            }

            original = Find(cut.Image, source.RequestId);
        }

        if (original is not { Authorization: { } approved, Registration: { } registered, Bind: { } bound }
            || !VoterContinuationBinding.IsCurrentBinding(cut.Image, bound))
        {
            throw new UnauthorizedAccessException("VoterContinuationBindingChanged");
        }

        if (query.Discovery is { } pin && (pin.RegistrationDigest != Committed(cut, registered).Digest
            || pin.CertificateDigest != bound.VoterContinuation!.Credential!.Fingerprint))
        {
            throw new UnauthorizedAccessException("VoterContinuationBoundDiscoveryChanged");
        }

        return new(ReadStage(cut, original.Request)!, ReadStage(cut, approved)!,
            ReadStage(cut, registered)!, ReadStage(cut, bound)!);
    }

    private static Stage ReadBoundProcess(VoterContinuationAudience.Cut cut, BoundHistory history, OnlineProcessLease? process)
    {
        if (process is not { CredentialContinuation: true })
        {
            throw new UnauthorizedAccessException("VoterContinuationBoundProcessChanged");
        }

        var registered = cut.Image.OnboardingContracts!.VoterContinuations!.Values
            .Select(item => item.Registration).SingleOrDefault(command => command?.OperationId == process.GrantId);
        if (registered is null
            || registered.OperationId != history.Registered.Command.OperationId
                && (!VoterContinuationBoundRecovery.TryResolve(cut.Image, registered.VoterContinuation!.Context, out var bind)
                    || bind != history.Bound.Command))
        {
            throw new UnauthorizedAccessException("VoterContinuationBoundProcessChanged");
        }

        Stage stage = ReadStage(cut, registered)!;
        if (process != VoterContinuationReducer.Lease(stage.Command, stage.Decision.Index))
        {
            throw new UnauthorizedAccessException("VoterContinuationBoundProcessChanged");
        }

        return stage;
    }

    private static Stage? ReadStage(VoterContinuationAudience.Cut cut, RevisionCommand? command)
    {
        return command is null ? null : new(command, Committed(cut, command));
    }
}
