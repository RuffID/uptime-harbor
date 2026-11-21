using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Issuers;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;
using UptimeHarbor.Infrastructure.Identity;
using UptimeHarbor.Infrastructure.Installation;

namespace UptimeHarbor.Tests.Web.Cluster.EnrollmentRouting;

internal sealed class VoterContinuationHttpAuthority : TimeProvider, IConsensusWriter,
    IClusterStateView, IEnrollmentReadBarrier, ILocalAccountLogin, IDisposable
{
    private readonly ECDsa previousKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly DateTimeOffset expiry = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly RevisionStateStore store = new();
    private readonly VoterCredentialProof candidate;
    private readonly VoterCredentialRequest request;
    private readonly VoterCredentialRequest alternateRequest;
    private readonly byte[] newCsr;
    private string? signingRoot;
    private EnrollmentIssuerStorage? signingStorage;
    private TimeSpan utcOffset;
    private RevisionImage Image => store.Current!;
    internal ClusterOptions Options { get; }
    internal int Writes { get; private set; }
    internal LocalInstallationProfile Profile { get; }
    internal int PasswordChecks { get; private set; }
    internal bool Majority { get; set; } = true;
    internal bool AcceptedAuthority { get; set; } = true;
    internal Action? AfterPasswordCheck { get; set; }
    internal Action? BeforeBarrier { get; set; }
    internal long Term { get; set; } = 1;
    internal long TimestampOffset { get; set; }
    internal void SetTimeOffset(TimeSpan offset)
    {
        utcOffset = offset;
        TimestampOffset = (long)(offset.TotalSeconds * TimestampFrequency);
    }
    internal RevisionImage ReadImage() => Image;
    internal void ChangeImage(Func<RevisionImage, RevisionImage> change) => store.Publish(change(Image));
    internal void RecoverProjection()
    {
        store.BeginRecovery();
        store.ConfirmRecovery(Image, store.RecoveryVersion);
    }
    internal Guid RequestId => request.OperationId;
    internal Guid VoterId => candidate.OriginNodeId;
    internal Guid ActorId { get; } = Guid.NewGuid();
    internal ManagedBootstrapStore SigningJournal { get; } = new();
    internal const string PASSWORD = "example-administrator-password";

    internal VoterContinuationHttpAuthority(bool administrator = false, bool signing = false)
    {
        Guid clusterId = Guid.NewGuid();
        Guid epoch = Guid.NewGuid();
        OperationalNode audience = new(Guid.NewGuid(), Guid.NewGuid(), "https://target.invalid/internal/raft", "target", true, true, false, 1);
        OperationalNode origin = new(Guid.NewGuid(), Guid.NewGuid(), "https://origin.invalid/internal/raft", "origin", true, true, false, 1);
        DateTimeOffset start = expiry.AddDays(-30);
        using ECDsa rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using ECDsa newKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using X509Certificate2 root = EnrollmentCertificates.CreateIssuer(rootKey, clusterId, 1, epoch, start);
        byte[] previousCsr = Csr(previousKey);
        EnrollmentRequest source = new(1, clusterId, 1, Guid.NewGuid(), Guid.NewGuid(), origin.NodeId, origin.StorageGeneration,
            origin.Address, EnrollmentCertificates.Hash(previousKey.ExportSubjectPublicKeyInfo()), EnrollmentCertificates.Hash(previousCsr));
        EnrollmentIssuance template = new(1, Guid.NewGuid(), epoch, new string('1', 32), origin.NodeId, origin.StorageGeneration,
            origin.Address, source.SpkiDigest, source.CsrDigest, start, expiry);
        using X509Certificate2 leaf = EnrollmentCertificates.IssueLeaf(root, source, template, previousCsr);
        OperationalCredential credential = new(template.IssueId, origin.NodeId, EnrollmentCertificates.Hash(leaf.RawData),
            Convert.ToBase64String(leaf.RawData), expiry, null, null);
        AcceptedEnrollmentIssuer issuer = new(epoch, audience.NodeId, audience.StorageGeneration, EnrollmentIssuer.PEER_PURPOSE,
            Convert.ToBase64String(root.RawData), EnrollmentCertificates.Hash(root.RawData),
            EnrollmentCertificates.Hash(rootKey.ExportSubjectPublicKeyInfo()), start, start.AddYears(3), 1);

        string replayDigest = RevisionCodec.Digest("fixture accepted replay"u8);
        Guid replayOperation = Guid.NewGuid();
        store.Publish(new(clusterId, 1)
        {
            Index = 1, Term = 1, Digest = replayDigest,
            Results = ImmutableDictionary<Guid, CommandResult>.Empty.Add(replayOperation,
                new(replayOperation, replayDigest, OperationOutcome.Committed, 1, 1, 1)),
            Operations = new()
            {
                Nodes = ImmutableDictionary<Guid, OperationalNode>.Empty.Add(origin.NodeId, origin).Add(audience.NodeId, audience),
                Credentials = ImmutableDictionary<Guid, OperationalCredential>.Empty.Add(credential.Id, credential),
                MembershipVersion = 1, Revision = 1
            },
            OnboardingContracts = new()
            {
                GenesisInitialized = true, Issuers = ImmutableDictionary<Guid, AcceptedEnrollmentIssuer>.Empty.Add(epoch, issuer)
            }
        });
        newCsr = Csr(newKey);
        EnrollmentIssuance replacement = template with
        {
            IssueId = Guid.NewGuid(), Serial = new string('2', 32), NotBeforeUtc = expiry, NotAfterUtc = expiry.AddDays(30),
            CsrDigest = EnrollmentCertificates.Hash(newCsr), SpkiDigest = EnrollmentCertificates.Hash(newKey.ExportSubjectPublicKeyInfo())
        };
        request = new(1, clusterId, 1, Guid.NewGuid(), new(credential.Id, credential.Fingerprint, source.SpkiDigest, 0, replacement));
        alternateRequest = request with
        {
            OperationId = Guid.NewGuid(),
            Renewal = request.Renewal with { Issuance = replacement with { IssueId = Guid.NewGuid() } }
        };
        candidate = new(1, VoterCredentialContinuationProofs.SCOPE, origin.NodeId, origin.StorageGeneration, Guid.NewGuid(),
            RandomChallenge(), 1, 0, 1, string.Empty, expiry, string.Empty);
        Options = new()
        {
            Mode = "Bootstrap", ClusterId = clusterId, Generation = 1, NodeId = audience.NodeId, StorageGeneration = audience.StorageGeneration,
            PublicUri = audience.Address, ProtocolMajor = 3, CommandSchema = 3, SnapshotFormat = 3, SqlSchema = 3
        };
        Profile = new(Guid.NewGuid(), "Bootstrap", InstallationPhase.ClusterReady, 2, 1, 1, Guid.NewGuid(), expiry,
            NodeId: audience.NodeId, StorageGeneration: audience.StorageGeneration, PeerAddress: audience.Address,
            ClusterId: clusterId, ClusterGeneration: 1);
        if (administrator)
        {
            AcceptedValue account = new(ActorId, DeletedEntityKind.Account, 1, new()
            {
                RoleId = Guid.NewGuid(), Name = "admin", PasswordHash = "test-hash", SecurityStamp = "stamp", ConcurrencyStamp = "revision"
            });
            store.Publish(Image with { Values = Image.Values.Add(ActorId, account) });
        }

        if (signing)
        {
            PrepareSigning(issuer, root, rootKey);
        }
    }

    private void PrepareSigning(AcceptedEnrollmentIssuer issuer, X509Certificate2 root, ECDsa rootKey)
    {
        signingRoot = Path.Combine(Path.GetTempPath(), "uh-continuation-signing-" + Guid.NewGuid().ToString("N"));
        Options.StoragePath = Path.Combine(signingRoot, "storage");
        InstallationOptions installation = new()
        {
            StateDirectory = Path.Combine(signingRoot, "state"), KeyDirectory = Path.Combine(signingRoot, "key"),
            SecretDirectory = Path.Combine(signingRoot, "secret")
        };
        signingStorage = new(Microsoft.Extensions.Options.Options.Create(installation), Microsoft.Extensions.Options.Options.Create(Options));
        (InstallationLease lease, LocalSecretStore secrets) = signingStorage.Open(create: true);
        byte[] pem = Encoding.UTF8.GetBytes(rootKey.ExportPkcs8PrivateKeyPem());
        try
        {
            string reference = secrets.Write(issuer.Epoch, "issuer", pem);
            SigningJournal.Issuers.Add(issuer.Epoch, new(issuer.Epoch, lease.InstallIdentity, Image.ClusterId, Image.Generation,
                issuer.NodeId, issuer.StorageGeneration, reference, root.RawData, issuer.CertificateDigest, issuer.SpkiDigest));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pem);
        }
    }

    internal void ChangeAccount(bool enabled)
    {
        AcceptedValue account = Image.Values[ActorId];
        AcceptedValue changed = account with { Revision = account.Revision + 1, Value = account.Value with { Enabled = enabled } };
        store.Publish(Image with { Values = Image.Values.SetItem(ActorId, changed) });
    }

    internal void AddServices(IServiceCollection services)
    {
        services.AddSingleton<TimeProvider>(this);
        services.AddSingleton<IOptions<ClusterOptions>>(Microsoft.Extensions.Options.Options.Create(Options));
        services.AddSingleton(store);
        services.AddSingleton<IClusterStateView>(this);
        services.AddSingleton<IEnrollmentReadBarrier>(this);
        services.AddSingleton<ILocalAccountLogin>(this);
        services.AddSingleton(new RevisionDispatcher(this, store, this));
        if (signingStorage is { } storage)
        {
            services.AddSingleton<IEnrollmentSigningUnitOfWork>(SigningJournal);
            services.AddSingleton(storage);
            services.AddSingleton<EnrollmentIssuerSigningLock>();
            services.AddSingleton<EnrollmentSigningLock>();
            services.AddScoped<EnrollmentSigningJournal>();
            services.AddScoped(_ => new VoterCredentialRenewalAuthority(store, this, new(this),
                Microsoft.Extensions.Options.Options.Create(Options), this));
            services.AddSingleton<Microsoft.Extensions.Logging.ILogger<VoterCredentialSigner>>(NullLogger<VoterCredentialSigner>.Instance);
            services.AddSingleton<Microsoft.Extensions.Logging.ILogger<VoterContinuationBind>>(NullLogger<VoterContinuationBind>.Instance);
        }
    }

    internal VoterCredentialProofRequest Sign(string nonce, string? challengePurpose = null, bool alternate = false, string? decisionDigest = null)
    {
        VoterCredentialRequest intent = request;
        if (alternate)
        {
            intent = alternateRequest;
        }

        VoterCredentialProof proof = candidate with { Nonce = nonce };
        if (challengePurpose is null)
        {
            proof = VoterCredentialContinuationProofs.Sign(intent, proof, previousKey);
        }
        else
        {
            proof = VoterCredentialContinuationProofs.SignChallenge(intent, proof, previousKey, challengePurpose, Options.PublicUri, decisionDigest);
        }

        return new(intent, proof, Convert.ToBase64String(newCsr));
    }

    internal VoterContinuationClient.Intent ClientIntent() => new(request, Convert.ToBase64String(newCsr),
        new(candidate.BootId, candidate.BootChallenge, candidate.ProcessEpoch, candidate.RecoveryVersion),
        candidate.MembershipVersion, Options.PublicUri);

    internal ECDsa OpenPreviousKey() => ECDsa.Create(previousKey.ExportParameters(true));

    internal VoterContinuationClient.Intent WithPreviousLease(DateTimeOffset until)
    {
        OnlineProcessLease process = new(VoterId, Guid.NewGuid(), Guid.NewGuid(), RandomChallenge(), 1, 0, until, Image.Index);
        store.Publish(Image with
        {
            OnboardingContracts = Image.OnboardingContracts! with
            {
                OnlineProcesses = ImmutableDictionary<Guid, OnlineProcessLease>.Empty.Add(VoterId, process)
            }
        });
        VoterContinuationClient.Intent intent = ClientIntent();
        return intent with { Process = intent.Process with { Epoch = 2 } };
    }

    public override DateTimeOffset GetUtcNow()
    {
        return expiry + utcOffset;
    }

    public override long GetTimestamp() => base.GetTimestamp() + TimestampOffset;

    public ConsensusStatus Consensus => new(Image.ClusterId, 1, Options.NodeId, Term, 1, true,
        Image.Index, Image.Operations.Nodes.Keys.ToHashSet(), CancellationToken.None, CancellationToken.None);

    public AcceptedClusterState Accepted => new(AcceptedAuthority, true, true, true, true, Image.Index, 1, Guid.Empty, false);

    public ValueTask ConfirmAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        BeforeBarrier?.Invoke();
        if (!Majority)
        {
            throw new InvalidOperationException("EnrollmentReadMajorityUnavailable");
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<long> ReplicateAsync(ReadOnlyMemory<byte> payload, int commandId, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        RevisionCommand command = RevisionCodec.Decode(payload.Span);
        store.Publish(new RevisionReducer().Apply(Image, command, Image.Index + 1, 1));
        Writes++;
        return ValueTask.FromResult(Image.Index);
    }

    public ValueTask<bool> ResignAsync(CancellationToken token)
    {
        throw new InvalidOperationException("Resign forbidden");
    }

    public Task<bool> CheckAsync(SharedAccount account, string password, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!Image.Values.TryGetValue(ActorId, out AcceptedValue? current) || account.UserId != current.Id
            || account.UserRevision != current.Revision)
        {
            throw new InvalidOperationException("Unexpected local login");
        }

        PasswordChecks++;
        AfterPasswordCheck?.Invoke();
        return Task.FromResult(password == PASSWORD);
    }

    public void Dispose()
    {
        previousKey.Dispose();
        signingStorage?.Dispose();
        if (signingRoot is not null)
        {
            Directory.Delete(signingRoot, true);
        }
    }

    internal static string RandomChallenge()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    }

    private static byte[] Csr(ECDsa key)
    {
        CertificateRequest csr = new("CN=test", key, HashAlgorithmName.SHA256);
        return csr.CreateSigningRequest();
    }
}
