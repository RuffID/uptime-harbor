using System.Collections.Immutable;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Application.Cluster.Onboarding;

public static class OnlineLifecycleReducer
{
    public static RevisionImage Apply(RevisionImage image, RevisionCommand command)
    {
        OnlineLifecycleCommand data = command.OnlineLifecycle!;
        if (!data.CoordinatorAtUtc.HasValue || data.CoordinatorTerm != image.Term)
        {
            throw new InvalidDataException("OnlineLifecycleAcceptanceUnconfirmed");
        }

        OnlineGrant grant = data.Grant;
        OperationalNode? origin = image.Operations.Nodes.GetValueOrDefault(command.OriginNodeId);
        bool registering = command.Action == "RegisterOnlineProcess";
        bool renewing = command.Action == "RenewOnlineProcess";
        DateTimeOffset now = data.CoordinatorAtUtc.Value;
        if (origin is null || !origin.Voting || origin.Removed || origin.StorageGeneration != data.StorageGeneration
            || grant.MinimumIndex >= image.Index
            || !OnlineGrantPolicy.Current(image, grant, now, registering, processRenewal: renewing))
        {
            return Decide(image, command, "OnlineGrantAuthorityChanged");
        }

        if (grant.Purpose == OnlineGrantPurpose.Activation && !OnlineActivationValidation.Current(image, grant, now)
            || grant.Purpose == OnlineGrantPurpose.Enable && !OnlineEnableValidation.Current(image, grant, now))
        {
            return Decide(image, command, "OnlineLifecycleScopeChanged");
        }

        OnboardingContractImage contracts = image.OnboardingContracts!;
        ImmutableDictionary<Guid, AcceptedOnlineGrant> grants = contracts.OnlineGrants ?? ImmutableDictionary<Guid, AcceptedOnlineGrant>.Empty;
        bool authorizing = registering || renewing || command.Action is "AuthorizeOnboardingActivation" or "AuthorizeOnlineEnable";
        if (authorizing)
        {
            if (command.OperationId != grant.GrantId || grants.ContainsKey(grant.GrantId))
            {
                return Decide(image, command, "OnlineGrantAlreadyAuthorized");
            }

            if (registering || renewing)
            {
                ImmutableDictionary<Guid, OnlineProcessLease> processes = contracts.OnlineProcesses ?? ImmutableDictionary<Guid, OnlineProcessLease>.Empty;
                OnlineProcessLease? previous = processes.GetValueOrDefault(grant.NodeId);
                bool sameProcess = previous is not null && previous.BootId == grant.BootId
                    && previous.BootChallenge == grant.BootChallenge && previous.ProcessEpoch == grant.ProcessEpoch
                    && previous.RecoveryVersion <= grant.RecoveryVersion;
                bool nextProcess = previous is not null && previous.BootId != grant.BootId && previous.BootChallenge != grant.BootChallenge
                    && grant.ProcessEpoch == previous.ProcessEpoch + 1 && now >= previous.LeaseUntilUtc;
                bool rejected = previous is null && grant.ProcessEpoch != 1
                    || previous is not null && !sameProcess && !nextProcess;
                if (renewing && (!sameProcess || previous!.RecoveryVersion != grant.RecoveryVersion
                    || now >= previous.LeaseUntilUtc))
                {
                    rejected = true;
                }

                if (rejected)
                {
                    return Decide(image, command, "OnlineProcessLeaseActive");
                }

                OnlineProcessLease process = new(grant.NodeId, grant.GrantId, grant.BootId, grant.BootChallenge,
                    grant.ProcessEpoch, grant.RecoveryVersion, now.AddMinutes(2), image.Index);
                contracts = contracts with { OnlineProcesses = processes.SetItem(grant.NodeId, process) };
            }

            grants = grants.Add(grant.GrantId, new(grant, image.Index, RevisionCodec.Digest(command), null, null)
            {
                ProcessRenewal = renewing
            });
            image = image with { OnboardingContracts = contracts with { OnlineGrants = grants } };
            return Decide(image, command, null);
        }

        AcceptedOnlineGrant? accepted = grants.GetValueOrDefault(grant.GrantId);
        if (command.OperationId != OnlineLifecycleCommands.ConsumeId(grant.GrantId)
            || accepted is null || accepted.Grant != grant || accepted.ConsumedIndex.HasValue)
        {
            return Decide(image, command, "OnlineGrantNotAvailable");
        }

        grants = grants.SetItem(grant.GrantId, accepted with
        {
            ConsumedIndex = image.Index, ConsumedDigest = RevisionCodec.Digest(command)
        });
        image = image with { OnboardingContracts = contracts with { OnlineGrants = grants } };
        if (command.Action == "ConsumeOnboardingActivation")
        {
            OperationalNode node = image.Operations.Nodes[grant.NodeId];
            image = image with { Operations = image.Operations with
            {
                Nodes = image.Operations.Nodes.SetItem(node.NodeId, node with { Activated = true, Revision = image.Index }),
                Revision = image.Index
            } };
        }

        return Decide(image, command, null);
    }

    private static RevisionImage Decide(RevisionImage image, RevisionCommand command, string? reason)
    {
        OperationOutcome outcome = OperationOutcome.Committed;
        if (reason is not null)
        {
            outcome = OperationOutcome.Rejected;
        }

        CommandResult result = new(command.OperationId, RevisionCodec.Digest(command), outcome, image.Index, image.Term,
            image.Index, reason, null, null, 0);
        return image with { Results = image.Results.Add(result.OperationId, result) };
    }
}
