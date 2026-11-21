using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;
using UptimeHarbor.Infrastructure.Installation;
using UptimeHarbor.Infrastructure.Installation.Renewal;
using UptimeHarbor.Infrastructure.Installation.Renewal.Models;
using UptimeHarbor.Tests.Infrastructure.Cluster.Renewal;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Installation.Renewal;

internal sealed class LocalVoterContinuationInstallationFixture : IDisposable
{
    internal VoterContinuationSigningFixture Signing { get; } = new();
    internal VoterContinuationScenario Scenario => Signing.Scenario;
    internal InstallationFixture Local => Signing.Installation;
    internal InstallationLease Lease => lease;
    internal CancellationToken Token => Scenario.Token;
    internal string Reference => LocalVoterContinuationInstallation.Reference(lease.InstallIdentity, Scenario.Request);
    internal Guid PreviousKeyOwner { get; } = Guid.NewGuid();
    internal string PreviousKeyReference { get; }
    internal LocalVoterCredentialFixture.FaultFiles Files { get; private set; }
    internal InstallationState State { get; private set; } = new();
    internal LocalVoterContinuationInstallation Service { get; private set; }
    internal RevisionImage? PreBind { get; private set; }
    internal CommandResult Registration { get; private set; } = null!;
    internal EnrollmentIssuedArtifact Artifact { get; private set; } = null!;
    internal LocalVoterCredentialIntent Intent { get; private set; } = null!;
    private readonly VoterCredentialRenewalAuthority readAuthority;
    private InstallationLease lease;

    internal LocalVoterContinuationInstallationFixture()
    {
        lease = Local.Lease;
        Scenario.UseProcess(lease);
        var fileOptions = Options.Create(Local.Options);
        Files = new(fileOptions, Local.Secrets);
        byte[] pem = Encoding.UTF8.GetBytes(Scenario.Crypto.Crypto.PeerKey.ExportPkcs8PrivateKeyPem());
        try
        {
            PreviousKeyReference = Local.Secrets.Write(PreviousKeyOwner, "peer", pem);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pem);
        }

        EnrollmentIssuance leaf = Scenario.Request.Renewal.Issuance;
        ClusterOptions source = new()
        {
            ClusterId = Scenario.Request.ClusterId, Generation = Scenario.Request.Generation,
            NodeId = leaf.NodeId, StorageGeneration = leaf.StorageGeneration,
            ProtocolMajor = 3, CommandSchema = 3, SnapshotFormat = 3, SqlSchema = 3
        };
        readAuthority = new(Scenario.Store, Scenario.Barrier, new(Scenario.Time), Options.Create(source), Scenario.Time);
        Service = CreateService(Local.Secrets);
    }

    internal async Task StageAsync()
    {
        Intent = await PinAsync(Scenario.Request);
        Registration = await Signing.RegisterAsync();
        Artifact = await Signing.Service().IssueAsync(Signing.Prove(await Signing.ChallengeAsync(Registration.Digest)),
            Registration.Digest, Token);
        await Service.PinBindIntentAsync(Reference, Registration, Artifact.CertificateDer, Token);
        PreBind = Scenario.State;
    }

    internal Task<LocalVoterCredentialIntent> PinAsync(VoterCredentialRequest request)
    {
        return Service.PinIntentAsync(request, Scenario.Crypto.Csr, Scenario.Crypto.NewKey, PreviousKeyOwner, PreviousKeyReference, Token);
    }

    internal async Task<CommandResult> BindAsync()
    {
        return await Signing.Binding().BindAsync(await Signing.BindProofAsync(Registration.Digest, Artifact.CertificateDigest),
            Registration.Digest, Artifact.CertificateDigest, Token);
    }

    internal CommandResult AcceptedBind()
    {
        RevisionCommand bind = Assert.IsType<RevisionCommand>(
            Scenario.State.OnboardingContracts!.VoterContinuations![Scenario.Request.OperationId].Bind);
        return Scenario.State.Results[bind.OperationId];
    }

    internal LocalVoterContinuationInstallation.BindReceipt Receipt(CommandResult result) =>
        new(Scenario.Request.OperationId, Artifact.IssueId, result, result.Index);

    internal string PathFor(string stage) => Path.Combine(Local.Options.SecretDirectory, "voter-renewal", Reference, stage);

    internal void Rewrite<T>(string stage, Func<T, T> replace) where T : class
    {
        T pin = StrictOnboardingJson.Decode<T>(Files.Read(Reference, stage)!);
        File.Delete(PathFor(stage));
        Files.WriteOnce(Reference, stage, StrictOnboardingJson.Encode(replace(pin)));
    }

    internal void Reopen()
    {
        lease.Dispose();
        lease = new(Local.Options, Local.Cluster.StoragePath);
        var options = Options.Create(Local.Options);
        LocalSecretStore secrets = new(options, lease);
        State = new();
        Files = new(options, secrets);
        Service = CreateService(secrets);
    }

    internal void AssertCompleted(LocalVoterCredentialInstallation installed, CommandResult bind)
    {
        Assert.Equal(Artifact.IssueId, installed.IssueId);
        Assert.Equal(bind.Index, installed.CredentialRevision);
        Assert.Equal(Artifact.CertificateDigest, installed.CertificateFingerprint);
        Assert.Equal(Artifact.CertificateDer, Signing.Journal.Issued[Artifact.IssueId].CertificateDer);
        Assert.Equal(1, Signing.Journal.Commits);
        Assert.Equal(4, Scenario.Wal.Count);
        Assert.Same(PreBind!.Operations.Nodes, Scenario.State.Operations.Nodes);
        Assert.Equal(PreBind.Operations.MembershipVersion, Scenario.State.Operations.MembershipVersion);
        Assert.Same(PreBind.OnboardingContracts!.OnlineProcesses, Scenario.State.OnboardingContracts!.OnlineProcesses);
        Assert.Null(State.Current);
        Assert.False(State.CommonOpen);
    }

    private LocalVoterContinuationInstallation CreateService(LocalSecretStore secrets) =>
        new(Files, secrets, lease, State, readAuthority, Scenario.Time, NullLogger<LocalVoterContinuationInstallation>.Instance);

    public void Dispose()
    {
        lease.Dispose();
        Signing.Dispose();
    }
}
