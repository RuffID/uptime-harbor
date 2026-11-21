using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Issuers;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;
using UptimeHarbor.Tests.Infrastructure.Installation;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Cluster.Renewal;

internal sealed class VoterContinuationSigningFixture : IDisposable
{
    internal VoterContinuationScenario Scenario { get; }
    internal InstallationFixture Installation { get; } = new();
    internal VoterCredentialAuthorityFixture.Work Journal { get; } = new();
    internal VoterCredentialAuthorityFixture.CapturedLog Logs { get; } = new();
    internal EnrollmentIssuerSigningLock IssuerLock { get; } = new();
    private readonly EnrollmentSigningLock journalLock = new();

    internal VoterContinuationSigningFixture()
    {
        Scenario = new(new OnboardingAudit(Logs));
        EnrollmentCertificateFixture crypto = Scenario.Crypto.Crypto;
        AcceptedEnrollmentIssuer issuer = Scenario.State.OnboardingContracts!.Issuers![crypto.Epoch] with
        {
            NodeId = Scenario.Options.NodeId, StorageGeneration = Scenario.Options.StorageGeneration
        };
        PublishIssuer(issuer);
        Installation.Secrets.Initialize();
        byte[] pem = Encoding.UTF8.GetBytes(crypto.IssuerKey.ExportPkcs8PrivateKeyPem());
        try
        {
            string key = Installation.Secrets.Write(issuer.Epoch, "issuer", pem);
            Journal.Issuers.Add(issuer.Epoch, new(issuer.Epoch, Installation.Lease.InstallIdentity, Scenario.State.ClusterId,
                Scenario.State.Generation, issuer.NodeId, issuer.StorageGeneration, key, crypto.Issuer.RawData,
                issuer.CertificateDigest, issuer.SpkiDigest));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pem);
        }
    }

    internal void PublishIssuer(AcceptedEnrollmentIssuer issuer)
    {
        OnboardingContractImage contracts = Scenario.State.OnboardingContracts!;
        Scenario.Store.Publish(Scenario.State with
        {
            OnboardingContracts = contracts with { Issuers = contracts.Issuers!.SetItem(issuer.Epoch, issuer) }
        });
    }

    internal VoterContinuationAuthority NewAuthority()
    {
        return new(Scenario.Dispatcher, Scenario.Store, Scenario.Barrier, new(Scenario.Time), Scenario,
            Options.Create(Scenario.Options), Scenario.Installation, Scenario, Scenario, Scenario.Time, new(Scenario.Time));
    }

    internal VoterContinuationIssuance Service(VoterContinuationAuthority? continuation = null)
    {
        IOptions<ClusterOptions> configured = Options.Create(Scenario.Options);
        EnrollmentIssuerStorage storage = new(Options.Create(Installation.Options), configured, Installation.Lease, Installation.Secrets);
        VoterCredentialRenewalAuthority renewal = new(Scenario.Store, Scenario.Barrier, new(Scenario.Time), configured, Scenario.Time);
        VoterCredentialSigner signer = new(Journal, new(Journal, journalLock), IssuerLock, storage, renewal, configured, Scenario.Time, Logs);
        return new(continuation ?? Scenario.Authority, signer);
    }

    internal VoterContinuationBind Binding(VoterContinuationAuthority? continuation = null)
    {
        return new(continuation ?? Scenario.Authority, Journal, Logs);
    }

    internal async Task<VoterCredentialProofRequest> BindProofAsync(string registration, string certificate,
        VoterContinuationAuthority? authority = null)
    {
        string binding = VoterContinuationAuthority.BindChallengeDigest(registration, certificate);
        VoterCredentialProof candidate = Scenario.Candidate with
        {
            Nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), IssuedAtUtc = Scenario.Time.Now
        };
        VoterCredentialProof proof = VoterCredentialContinuationProofs.SignChallenge(Scenario.Request, candidate,
            Scenario.Crypto.Crypto.PeerKey, VoterContinuationAuthority.BIND, Scenario.Options.PublicUri, binding);
        VoterCredentialProofRequest call = new(Scenario.Request, proof, Convert.ToBase64String(Scenario.Crypto.Csr));
        string nonce = await (authority ?? Scenario.Authority).IssueChallengeAsync(call, VoterContinuationAuthority.BIND,
            registration, Scenario.Token, certificate);
        return Prove(nonce);
    }

    internal async Task<CommandResult> RegisterAsync()
    {
        CommandResult request = await Scenario.ProposeAsync();
        CommandResult approval = await Scenario.ApproveAsync(request);
        CommandResult registration = await Scenario.RegisterAsync(approval);
        Assert.Equal(OperationOutcome.Committed, registration.Outcome);
        return registration;
    }

    internal Task<string> ChallengeAsync(string digest, VoterContinuationAuthority? authority = null)
    {
        VoterCredentialProof proof = VoterCredentialContinuationProofs.SignChallenge(Scenario.Request,
            Scenario.Candidate with { Nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), IssuedAtUtc = Scenario.Time.Now },
            Scenario.Crypto.Crypto.PeerKey, VoterContinuationAuthority.ISSUE, Scenario.Options.PublicUri, digest);
        return (authority ?? Scenario.Authority).IssueChallengeAsync(new(Scenario.Request, proof, Convert.ToBase64String(Scenario.Crypto.Csr)),
            VoterContinuationAuthority.ISSUE, digest, Scenario.Token);
    }

    internal VoterCredentialProofRequest Prove(string nonce, VoterCredentialRequest? request = null, VoterCredentialProof? candidate = null)
    {
        VoterCredentialRequest intent = request ?? Scenario.Request;
        VoterCredentialProof proof = VoterCredentialContinuationProofs.Sign(intent,
            (candidate ?? Scenario.Candidate) with { Nonce = nonce, IssuedAtUtc = Scenario.Time.Now }, Scenario.Crypto.Crypto.PeerKey);
        return new(intent, proof, Convert.ToBase64String(Scenario.Crypto.Csr));
    }

    public void Dispose()
    {
        IssuerLock.Dispose();
        Installation.Dispose();
        Scenario.Dispose();
    }
}
