using System.Diagnostics.CodeAnalysis;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Application.Cluster.Onboarding.Renewal;

internal static class VoterContinuationBoundRecovery
{
    internal static bool TryResolve(RevisionImage image, VoterContinuationContext context,
        [NotNullWhen(true)] out RevisionCommand? bind)
    {
        var requests = image.OnboardingContracts?.VoterContinuations;
        bind = null;
        if (!TryResolveBind(image, context.Request, out RevisionCommand? original)
            || context.BoundSource is not { } source || context.Predecessor is not { CredentialContinuation: true } predecessor
            || requests?.GetValueOrDefault(source.RequestId)?.Registration is not { } registration)
        {
            return false;
        }

        VoterContinuationContext issuing = original.VoterContinuation!.Context;
        if (issuing.PreviousIssuerEpoch != context.PreviousIssuerEpoch
            || issuing.PreviousIssuerRevision != context.PreviousIssuerRevision || issuing.IssuerRevision != context.IssuerRevision)
        {
            return false;
        }

        RevisionCommand? prior = registration;
        if (registration.OperationId != predecessor.GrantId)
        {
            prior = requests.Values.Select(item => item.Registration).SingleOrDefault(item => item?.OperationId == predecessor.GrantId);
            if (prior?.VoterContinuation is not { } previous || previous.Context.BoundSource != source
                || predecessor.Index <= source.CredentialRevision)
            {
                return false;
            }
        }

        if (VoterContinuationBinding.ReadDecision(image, prior) is not { Outcome: OperationOutcome.Committed } registered
            || predecessor != VoterContinuationReducer.Lease(prior, registered.Index))
        {
            return false;
        }

        bind = original;
        return true;
    }

    internal static bool TryResolveBind(RevisionImage image, VoterCredentialRequest request,
        [NotNullWhen(true)] out RevisionCommand? bind)
    {
        bind = null;
        if (request.BoundSource is not { } source
            || image.OnboardingContracts?.VoterContinuations?.GetValueOrDefault(source.RequestId)?.Bind is not { } original
            || original.VoterContinuation?.Context is not { BoundSource: null } issuing
            || issuing.Request.Renewal != request.Renewal
            || VoterContinuationBinding.ReadDecision(image, original) is not { Outcome: OperationOutcome.Committed } bound
            || bound.Digest != source.BindDigest || bound.Index != source.CredentialRevision)
        {
            return false;
        }

        bind = original;
        return true;
    }
}
