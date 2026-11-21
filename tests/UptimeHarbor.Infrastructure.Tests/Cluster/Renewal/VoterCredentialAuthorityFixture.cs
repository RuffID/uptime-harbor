using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Issuers;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;
using UptimeHarbor.Tests.Infrastructure.Installation;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Cluster.Renewal;

public sealed class VoterCredentialAuthorityFixture : IDisposable
{
    public VoterCredentialCryptoScenario Scenario { get; } = new();
    public InstallationFixture Installation { get; } = new();
    public RevisionStateStore Subject { get; } = new();
    public RevisionStateStore Issuer { get; } = new();
    public Barrier SubjectBarrier { get; } = new();
    public Barrier IssuerBarrier { get; } = new();
    public Clock Time { get; }
    public Work Journal { get; } = new();
    public CapturedLog Logs { get; } = new();
    public EnrollmentIssuerSigningLock IssuerLock { get; } = new();
    public EnrollmentSigningLock JournalLock { get; } = new();
    public VoterCredentialRequest Request { get; set; }
    public VoterCredentialProofRequest Call { get; set; }
    public ClusterOptions SubjectOptions { get; }
    public ClusterOptions IssuerOptions { get; }
    public VoterCredentialRenewalAuthority SubjectAuthority { get; }
    public VoterCredentialRenewalAuthority IssuerAuthority { get; }
    public Transport Remote { get; }
    public X509ChainStatusFlags ManualRevocationStatus { get; set; }
    public Guid SubjectId => Request.Renewal.Issuance.NodeId;
    public Guid SubjectStorage => Request.Renewal.Issuance.StorageGeneration;

    public VoterCredentialAuthorityFixture(bool remote = false, bool manual = false)
    {
        Time = new(Scenario.Now);
        Request = new(1, Scenario.State.ClusterId, Scenario.State.Generation, Guid.NewGuid(), Scenario.Intent);
        SubjectOptions = OptionsFor(SubjectId, SubjectStorage);
        Guid issuerNode = SubjectId;
        Guid issuerStorage = SubjectStorage;
        if (remote)
        {
            issuerNode = Guid.NewGuid();
            issuerStorage = Guid.NewGuid();
            AcceptedEnrollmentIssuer issuer = Scenario.State.OnboardingContracts!.Issuers![Scenario.Crypto.Epoch];
            OperationalNode node = new(issuerNode, issuerStorage, "https://issuer.example.test/internal/raft", "issuer", true, true, false, 1);
            Scenario.State = Scenario.State with
            {
                Operations = Scenario.State.Operations with { Nodes = Scenario.State.Operations.Nodes.Add(node.NodeId, node) },
                OnboardingContracts = Scenario.State.OnboardingContracts with
                {
                    Issuers = Scenario.State.OnboardingContracts.Issuers.SetItem(issuer.Epoch,
                        issuer with { NodeId = issuerNode, StorageGeneration = issuerStorage })
                }
            };
        }

        IssuerOptions = OptionsFor(issuerNode, issuerStorage);
        if (manual)
        {
            ConfigureManualPrevious();
        }

        OnlineProcessLease process = new(SubjectId, Guid.NewGuid(), Guid.NewGuid(), Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            3, 5, Time.Now.AddMinutes(10), 1);
        Scenario.State = Scenario.State with
        {
            OnboardingContracts = Scenario.State.OnboardingContracts! with
            {
                OnlineProcesses = ImmutableDictionary<Guid, OnlineProcessLease>.Empty.Add(SubjectId, process)
            }
        };
        Publish();
        SubjectAuthority = new(Subject, SubjectBarrier, new(Time), Options.Create(SubjectOptions), Time,
            new(SubjectOptions, ValidateManualChain));
        IssuerAuthority = new(Issuer, IssuerBarrier, new(Time), Options.Create(IssuerOptions), Time,
            new(IssuerOptions, ValidateManualChain));
        VoterCredentialProof proof = new(1, VoterCredentialProofs.SCOPE, SubjectId, SubjectStorage, process.BootId,
            process.BootChallenge, process.ProcessEpoch, process.RecoveryVersion, Scenario.State.Operations.MembershipVersion,
            Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), Time.Now, string.Empty);
        Call = new(Request, VoterCredentialProofs.Sign(Request, proof, Scenario.Crypto.PeerKey), Convert.ToBase64String(Scenario.Csr));
        byte[] pem = Encoding.UTF8.GetBytes(Scenario.Crypto.IssuerKey.ExportPkcs8PrivateKeyPem());
        string key;
        try
        {
            key = Installation.Secrets.Write(Scenario.Crypto.Epoch, "issuer", pem);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pem);
        }

        Journal.Issuers.Add(Scenario.Crypto.Epoch, new(Scenario.Crypto.Epoch, Installation.Lease.InstallIdentity,
            Request.ClusterId, Request.Generation, issuerNode, issuerStorage, key, Scenario.Crypto.Issuer.RawData,
            EnrollmentCertificates.Hash(Scenario.Crypto.Issuer.RawData), EnrollmentCertificates.Hash(Scenario.Crypto.IssuerKey.ExportSubjectPublicKeyInfo())));
        Remote = new(this);
    }

    public async Task<RevisionCommand> PrepareAsync(ECDsa? previousKey = null)
    {
        string nonce = await SubjectAuthority.IssueNonceAsync(Request, Call.Proof, SubjectId, SubjectStorage, TestContext.Current.CancellationToken);
        Call = Call with { Request = Request, Proof = VoterCredentialProofs.Sign(Request,
            Call.Proof with { Nonce = nonce, IssuedAtUtc = Time.Now }, previousKey ?? Scenario.Crypto.PeerKey) };
        return await SubjectAuthority.PrepareAuthorizationAsync(Call, SubjectId, SubjectStorage, TestContext.Current.CancellationToken);
    }

    public async Task AuthorizeAsync(ECDsa? previousKey = null)
    {
        RevisionCommand command = await PrepareAsync(previousKey);
        command = command with { EnrollmentTrust = command.EnrollmentTrust! with { CoordinatorAtUtc = Time.Now, CoordinatorTerm = Scenario.State.Term } };
        Assert.Equal(UptimeHarbor.Domain.Cluster.OperationOutcome.Committed, Scenario.Apply(command).Outcome);
        Publish();
    }

    public VoterCredentialSigningRequest SigningCall()
    {
        long index = Scenario.State.OnboardingContracts?.VoterCredentialRotations?.GetValueOrDefault(Request.Renewal.Issuance.IssueId)?.AuthorizedIndex ?? 2;
        return new(1, Call, index);
    }

    public VoterCredentialSigner Signer()
    {
        EnrollmentIssuerStorage storage = new(Microsoft.Extensions.Options.Options.Create(Installation.Options),
            Microsoft.Extensions.Options.Options.Create(IssuerOptions), Installation.Lease, Installation.Secrets);
        return new(Journal, new(Journal, JournalLock), IssuerLock, storage, IssuerAuthority,
            Microsoft.Extensions.Options.Options.Create(IssuerOptions), Time, Logs);
    }

    public VoterCredentialIssuanceRouter Router()
    {
        return new(Signer(), Remote, SubjectAuthority, Microsoft.Extensions.Options.Options.Create(SubjectOptions), Time);
    }

    public void Publish()
    {
        Subject.Publish(Scenario.State);
        Issuer.Publish(Scenario.State);
    }

    private ClusterOptions OptionsFor(Guid node, Guid storage)
    {
        return new()
        {
            ClusterId = Request.ClusterId, Generation = Request.Generation, NodeId = node, StorageGeneration = storage,
            ProtocolMajor = 3, ProtocolMinor = 0, CommandSchema = 3, SnapshotFormat = 3, SqlSchema = 3, PasswordHashFormat = 1
        };
    }

    private void ConfigureManualPrevious()
    {
        using ECDsa caKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        CertificateRequest ca = new("CN=Manual renewal test CA", caKey, HashAlgorithmName.SHA256);
        ca.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
        ca.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));

        using X509Certificate2 root = ca.CreateSelfSigned(Time.Now.AddDays(-30), Time.Now.AddYears(1));
        CertificateRequest request = new("CN=manual voter", Scenario.Crypto.PeerKey, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.2") }, true));
        SubjectAlternativeNameBuilder san = new();
        san.AddUri(new($"urn:uptimeharbor:{Request.ClusterId:D}:{Request.Generation}:{SubjectId:D}"));
        request.CertificateExtensions.Add(san.Build());

        OperationalCredential previous = Scenario.State.Operations.Credentials[Request.Renewal.PreviousCredentialId];
        using X509Certificate2 leaf = request.Create(root, Time.Now.AddDays(-2), previous.ExpiresAtUtc, [1]);

        previous = previous with
        {
            Fingerprint = EnrollmentCertificates.Hash(leaf.RawData),
            PublicCertificate = Convert.ToBase64String(leaf.RawData)
        };
        Scenario.State = Scenario.State with
        {
            Operations = Scenario.State.Operations with { Credentials = Scenario.State.Operations.Credentials.SetItem(previous.Id, previous) }
        };
        Request = Request with { Renewal = Request.Renewal with { PreviousFingerprint = previous.Fingerprint } };

        string path = Path.Combine(Installation.Root, "manual-ca.der");
        File.WriteAllBytes(path, root.RawData);
        SubjectOptions.ClientCaPath = IssuerOptions.ClientCaPath = path;
        SubjectOptions.ClientCaFingerprint = IssuerOptions.ClientCaFingerprint = EnrollmentCertificates.Hash(root.RawData);
    }

    private bool ValidateManualChain(X509Certificate2 leaf, X509Certificate2 root, ClusterOptions options, DateTimeOffset now)
    {
        Assert.Equal("Direct", options.TlsBoundary);
        using X509Chain chain = new();

        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(root);
        chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.2"));
        chain.ChainPolicy.VerificationTime = now.UtcDateTime;
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.DisableCertificateDownloads = true;

        return ManualRevocationStatus == X509ChainStatusFlags.NoError && chain.Build(leaf);
    }

    public void Dispose()
    {
        IssuerLock.Dispose();
        Installation.Dispose();
        Scenario.Dispose();
    }

    public sealed class Barrier : IEnrollmentReadBarrier
    {
        public int Calls { get; private set; }
        public bool Available { get; set; } = true;
        public Action<int>? After { get; set; }

        public Func<int, ValueTask>? Wait { get; set; }

        public async ValueTask ConfirmAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Calls++;
            if (!Available)
            {
                throw new InvalidOperationException("EnrollmentReadMajorityUnavailable");
            }

            After?.Invoke(Calls);
            if (Wait is not null)
            {
                await Wait(Calls);
            }
        }
    }

    public sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public long Stamp { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow()
        {
            return Now;
        }

        public override long GetTimestamp()
        {
            return Stamp;
        }
    }

    public sealed class Transport(VoterCredentialAuthorityFixture fixture) : IVoterCredentialSignerClient
    {
        public int Calls { get; private set; }
        public bool Available { get; set; } = true;
        public bool LoseResponse { get; set; }
        public Func<EnrollmentIssuedArtifact, EnrollmentIssuedArtifact>? Alter { get; set; }
        public Action? After { get; set; }

        public async Task<EnrollmentIssuedArtifact> IssueAsync(Guid issuerNodeId, VoterCredentialSigningRequest request, CancellationToken token)
        {
            Assert.Equal(fixture.IssuerOptions.NodeId, issuerNodeId);
            Calls++;
            if (!Available)
            {
                throw new IOException("SimulatedIssuerOutage");
            }

            EnrollmentIssuedArtifact artifact = await fixture.Signer().IssueAsync(request, fixture.SubjectId, fixture.SubjectStorage, token);
            After?.Invoke();
            if (LoseResponse)
            {
                throw new IOException("SimulatedLostResponse");
            }

            if (Alter is not null)
            {
                return Alter(artifact);
            }

            return artifact;
        }
    }

    public sealed class Work : IEnrollmentSigningUnitOfWork, IEnrollmentSigningRepository
    {
        public IEnrollmentSigningRepository Signing => this;
        public Dictionary<Guid, EnrollmentIssuerReference> Issuers { get; } = [];
        public Dictionary<Guid, EnrollmentIssuedArtifact> Issued { get; } = [];
        public int Commits { get; private set; }
        public bool FailBeforeCommit { get; set; }
        public bool LoseCommitResponse { get; set; }
        private EnrollmentIssuedArtifact? pending;

        public Task<IReadOnlyList<EnrollmentIssuerReference>> ListIssuersAsync(CancellationToken token)
        {
            return Task.FromResult<IReadOnlyList<EnrollmentIssuerReference>>(Issuers.Values.ToArray());
        }

        public Task<EnrollmentIssuerReference?> FindIssuerAsync(Guid epoch, CancellationToken token)
        {
            return Task.FromResult(Issuers.GetValueOrDefault(epoch));
        }

        public Task<EnrollmentIssuedArtifact?> FindIssueAsync(Guid issueId, CancellationToken token)
        {
            return Task.FromResult(Issued.GetValueOrDefault(issueId));
        }

        public void AddIssuer(EnrollmentIssuerReference issuer)
        {
            throw new InvalidOperationException("ProvisioningNotExpected");
        }

        public void AddIssue(EnrollmentIssuedArtifact artifact)
        {
            pending = artifact;
        }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (FailBeforeCommit)
            {
                pending = null;
                throw new IOException("SimulatedCommitFailure");
            }

            Assert.NotNull(pending);
            Issued.Add(pending.IssueId, pending);
            pending = null;
            Commits++;
            if (LoseCommitResponse)
            {
                throw new IOException("SimulatedCommitResponseLost");
            }

            return Task.FromResult(1);
        }

        public async Task ExecuteInTransactionAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken = default)
        {
            await action(cancellationToken);
            await SaveChangesAsync(cancellationToken);
        }
    }

    public sealed class CapturedLog : ILogger<VoterCredentialSigner>, ILogger<VoterContinuationBind>, ILogger<OnboardingAudit>
    {
        public List<(LogLevel Level, string Message)> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Messages.Add((level, formatter(state, exception)));
        }
    }
}
