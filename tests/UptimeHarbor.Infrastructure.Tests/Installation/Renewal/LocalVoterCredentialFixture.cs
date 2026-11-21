using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;
using UptimeHarbor.Infrastructure.Installation;
using UptimeHarbor.Infrastructure.Installation.Renewal;
using UptimeHarbor.Infrastructure.Installation.Renewal.Models;
using UptimeHarbor.Tests.Infrastructure.Cluster.Renewal;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Installation.Renewal;

public class LocalVoterCredentialFixture : IDisposable
{
    public VoterCredentialAuthorityFixture Authority { get; }
    public Guid PreviousKeyOwner { get; } = Guid.NewGuid();
    public string PreviousKeyReference { get; }
    public LocalSecretStore Secrets { get; private set; }
    public InstallationLease Lease { get; private set; }
    public InstallationState State { get; private set; } = new();
    public FaultFiles Files { get; private set; }
    public CapturedLog Logs { get; } = new();
    public LocalVoterCredentialPreparation Store { get; private set; }
    public LocalVoterCredentialState? Local { get; private set; }
    public EnrollmentIssuedArtifact? Artifact { get; private set; }
    public CancellationToken Token => TestContext.Current.CancellationToken;
    public string Reference => LocalVoterCredentialMaterial.Reference(Lease.InstallIdentity, Authority.Request.ClusterId,
        Authority.Request.Generation, Authority.SubjectId, Authority.SubjectStorage, Authority.Scenario.Intent.PreviousCredentialId);

    public LocalVoterCredentialFixture(bool manual = false, bool remoteIssuer = true)
    {
        Authority = new(remoteIssuer, manual);
        Lease = Authority.Installation.Lease;
        Secrets = Authority.Installation.Secrets;
        byte[] pem = Encoding.UTF8.GetBytes(Authority.Scenario.Crypto.PeerKey.ExportPkcs8PrivateKeyPem());
        try
        {
            PreviousKeyReference = Secrets.Write(PreviousKeyOwner, "peer", pem);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pem);
        }

        Files = new(Options.Create(Authority.Installation.Options), Secrets);
        Store = new(Files, Secrets, Lease, State, Authority.SubjectAuthority, Authority.Time, Logs);
    }

    public async Task<LocalVoterCredentialState> PrepareAsync()
    {
        Local = await Store.PrepareAsync(Authority.Scenario.State, Authority.SubjectId,
            Authority.Scenario.Intent.PreviousCredentialId, Authority.Scenario.Crypto.Epoch,
            PreviousKeyOwner, PreviousKeyReference, Authority.Time.Now, Token);
        Authority.Request = Local.Intent.Request;
        Authority.Call = Authority.Call with { Request = Authority.Request, CsrDer = Local.Intent.CsrDer };
        return Local;
    }

    public async Task AuthorizeAsync()
    {
        await PrepareAsync();
        await Authority.AuthorizeAsync();
        Local = await Store.PinAuthorizationAsync(Reference, Authority.SigningCall(), Token);
    }

    public async Task<EnrollmentIssuedArtifact> SignAsync()
    {
        Artifact = await Authority.Router().IssueAsync(Authority.SigningCall(), Token);
        return Artifact;
    }

    public async Task<LocalVoterCredentialState> PinAsync()
    {
        Local = await Store.PinArtifactAsync(Reference, Authority.SigningCall(),
            Artifact ?? throw new InvalidOperationException("TestArtifactMissing"), Token);
        return Local;
    }

    public void Reopen()
    {
        Lease.Dispose();
        Lease = new(Authority.Installation.Options, Authority.Installation.Cluster.StoragePath);
        Secrets = new(Options.Create(Authority.Installation.Options), Lease);
        State = new();
        Files = new(Options.Create(Authority.Installation.Options), Secrets);
        Store = new(Files, Secrets, Lease, State, Authority.SubjectAuthority, Authority.Time, Logs);
        Local = null;
    }

    public string PathFor(string stage)
    {
        return Path.Combine(Authority.Installation.Options.SecretDirectory, "voter-renewal", Reference, stage);
    }

    public void RewriteIntent(Func<LocalVoterCredentialIntent, LocalVoterCredentialIntent> change, bool replaceKey = false)
    {
        byte[] bundle = Files.Read(Reference, "intent")!;
        byte[]? rewritten = null;
        try
        {
            LocalVoterCredentialIntent intent = LocalVoterCredentialMaterial.Decode(bundle, Lease.InstallIdentity, Reference, out ECDsa key);
            using (key)
            {
                using ECDsa other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                ECDsa material = key;
                if (replaceKey)
                {
                    material = other;
                }

                rewritten = LocalVoterCredentialMaterial.Encode(change(intent), material);
                File.Delete(PathFor("intent"));
                Files.WriteOnce(Reference, "intent", rewritten);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bundle);
            if (rewritten is not null)
            {
                CryptographicOperations.ZeroMemory(rewritten);
            }
        }
    }

    public void Dispose()
    {
        Lease.Dispose();
        Authority.Dispose();
    }

    public class FaultFiles(IOptions<InstallationOptions> options, LocalSecretStore secrets) : LocalVoterCredentialFiles(options, secrets)
    {
        public string? FailStage { get; set; }
        public bool AfterWrite { get; set; }
        public string? CapturedIntentDigest { get; private set; }
        public List<string> Writes { get; } = [];
        public Action<string>? BeforeWrite { get; set; }

        public override void WriteOnce(string reference, string stage, ReadOnlySpan<byte> bytes)
        {
            if (stage == "intent")
            {
                int length = BinaryPrimitives.ReadInt32LittleEndian(bytes);
                LocalVoterCredentialIntent intent = StrictOnboardingJson.Decode<LocalVoterCredentialIntent>(bytes.Slice(4, length));
                CapturedIntentDigest = VoterCredentialProofs.Digest(intent.Request);
            }

            BeforeWrite?.Invoke(stage);
            if (FailStage == stage && !AfterWrite)
            {
                throw new IOException("SimulatedBeforeLocalWrite");
            }

            base.WriteOnce(reference, stage, bytes);
            Writes.Add(stage);
            if (FailStage == stage && AfterWrite)
            {
                throw new IOException("SimulatedAfterLocalWrite");
            }
        }
    }

    public class CapturedLog : ILogger<LocalVoterCredentialPreparation>
    {
        public List<string> Messages { get; } = [];

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
            Messages.Add(formatter(state, exception));
        }
    }
}
