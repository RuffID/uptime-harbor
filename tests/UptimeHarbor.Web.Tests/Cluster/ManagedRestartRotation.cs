using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Replication;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Issuers;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;
using UptimeHarbor.Infrastructure.Installation;
using UptimeHarbor.Infrastructure.Installation.Renewal;
using UptimeHarbor.Infrastructure.Installation.Renewal.Models;
using Xunit;

namespace UptimeHarbor.Tests.Web.Cluster;

/// <summary>Пишет штатный accepted Authorize/Bind в локальный WAL. Production dispatcher остаётся закрытым.</summary>
internal sealed class ManagedRestartRotation(ManagedBootstrapFixture first)
{
    public RevisionImage? CutBeforeBind { get; private set; }
    public RevisionCommand? BindCommand { get; private set; }

    public async Task<LocalVoterCredentialInstallation> InstallAsync(CancellationToken token, string? interruptAfter = null)
    {
        var profile = first.Store.Profile!;
        RevisionImage image = first.Accepted.Current!;
        Guid node = first.Options.NodeId;
        Guid storage = first.Options.StorageGeneration;
        Guid previous = profile.CredentialId!.Value;
        Guid epoch = Assert.Single(image.OnboardingContracts!.Issuers!).Key;
        first.Time.Now = image.Operations.Credentials[previous].ExpiresAtUtc.AddDays(-2);
        await first.RefreshAsync(token);
        await first.Driver.TickAsync(token); // Refresh the live process at the beginning of the renewal window.

        var local = Options.Create(first.Local);
        var cluster = Options.Create(first.Options);
        VoterCredentialRenewalAuthority authority = new(first.Accepted, first.ReadBarrier, first.Nonces, cluster, first.Time);
        LocalVoterCredentialFiles files = new InterruptingFiles(local, first.Secrets, interruptAfter);
        LocalVoterCredentialPreparation preparation = new(files, first.Secrets, first.Lease, first.Installation, authority,
            first.Time, NullLogger<LocalVoterCredentialPreparation>.Instance);
        WalTransport wal = new(first.Native, first.Accepted, first.Time, interruptAfter);
        var material = await preparation.PrepareAsync(first.Accepted.Current!, node, previous, epoch,
            profile.AttemptId!.Value, profile.KeyReference!, first.Time.Now, token);
        var request = material.Intent.Request;
        var process = first.Accepted.Current!.OnboardingContracts!.OnlineProcesses![node];
        VoterCredentialProof proof = new(1, VoterCredentialProofs.SCOPE, node, storage, process.BootId,
            process.BootChallenge, process.ProcessEpoch, process.RecoveryVersion,
            image.Operations.MembershipVersion, string.Empty, first.Time.Now, string.Empty);
        string nonce = await authority.IssueNonceAsync(request, proof, node, storage, token);
        using ECDsa key = first.Tls.Certificate!.GetECDsaPrivateKey()!;
        VoterCredentialProofRequest call = new(request, VoterCredentialProofs.Sign(request, proof with { Nonce = nonce }, key),
            material.Intent.CsrDer);
        RevisionCommand authorize = await authority.PrepareAuthorizationAsync(call, node, storage, token);
        CommandResult result = Assert.Single(await wal.SendAsync([authorize], token));
        Assert.Equal(OperationOutcome.Committed, result.Outcome);
        VoterCredentialSigningRequest signing = new(1, call, result.Index);
        string reference = material.Intent.KeyReference;
        await preparation.PinAuthorizationAsync(reference, signing, token);
        VoterCredentialSigner signer = new(first.Store, new(first.Store, new()), new(),
            new EnrollmentIssuerStorage(local, cluster, first.Lease, first.Secrets), authority, cluster, first.Time,
            NullLogger<VoterCredentialSigner>.Instance);
        var artifact = await signer.IssueAsync(signing, node, storage, token);
        await preparation.PinArtifactAsync(reference, signing, artifact, token);
        LocalVoterCredentialBindingCoordinator binding = new(preparation, files, first.Installation, authority, wal,
            first.Time, NullLogger<LocalVoterCredentialBindingCoordinator>.Instance);
        var installation = new LocalVoterCredentialInstallationCoordinator(binding, files, first.Installation,
            NullLogger<LocalVoterCredentialInstallationCoordinator>.Instance);
        CutBeforeBind = first.Accepted.Current;
        if (interruptAfter == "before-bind-intent")
        {
            return new(reference, request.Renewal.Issuance.IssueId, 0, artifact.CertificateDigest);
        }

        if (interruptAfter is null)
        {
            await binding.BindAsync(reference, signing, token);
            return await installation.InstallAsync(reference, token);
        }

        var failure = await Assert.ThrowsAsync<Interrupted>(async () =>
        {
            await binding.BindAsync(reference, signing, token);
            await installation.InstallAsync(reference, token);
        });
        Assert.Equal(interruptAfter, failure.Message);
        byte[]? savedBind = files.Read(reference, "bind-intent");
        BindCommand = savedBind is null ? null : StrictOnboardingJson.Decode<LocalVoterCredentialBindIntent>(savedBind).Command;
        var rotation = first.Accepted.Current!.OnboardingContracts!.VoterCredentialRotations![request.Renewal.Issuance.IssueId];
        return new(reference, request.Renewal.Issuance.IssueId, rotation.CredentialRevision, artifact.CertificateDigest);
    }

    internal sealed class WalTransport(ManagedRaftProbe native, RevisionStateStore revisions, TimeProvider time,
        string? interruptAfter = null) : IRevisionTransport
    {
        public int Sends { get; private set; }

        public async Task<IReadOnlyList<CommandResult>> SendAsync(IReadOnlyList<RevisionCommand> commands, CancellationToken token)
        {
            RevisionCommand command = Assert.Single(commands);
            Sends++;
            command = command with { EnrollmentTrust = command.EnrollmentTrust! with
            {
                CoordinatorAtUtc = time.GetUtcNow(), CoordinatorTerm = native.Store.Term
            } };
            if (command.Action == "BindVoterCredential" && interruptAfter == "before-bind")
            {
                throw new Interrupted(interruptAfter);
            }

            await native.Replicate(new BinaryLogEntry
            {
                Content = RevisionCodec.Encode(command),
                Term = native.Store.Term,
                CommandId = RevisionCodec.COMMAND_ID
            }, token);
            if (command.Action == "BindVoterCredential" && interruptAfter == "accepted-bind")
            {
                throw new Interrupted(interruptAfter);
            }

            return [revisions.Current!.Results[command.OperationId]];
        }
    }

    private sealed class InterruptingFiles(IOptions<InstallationOptions> options, LocalSecretStore secrets, string? interruptAfter)
        : LocalVoterCredentialFiles(options, secrets)
    {
        public override void WriteOnce(string reference, string stage, ReadOnlySpan<byte> bytes)
        {
            base.WriteOnce(reference, stage, bytes);
            if (stage == interruptAfter)
            {
                throw new Interrupted(stage);
            }
        }
    }

    private sealed class Interrupted(string stage) : IOException(stage);
}
