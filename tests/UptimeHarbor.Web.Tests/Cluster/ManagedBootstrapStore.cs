using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Infrastructure.Installation;

namespace UptimeHarbor.Tests.Web.Cluster;

/// <summary>Только память вместо локального SQL; общий tracker моделируется одним объектом.</summary>
public class ManagedBootstrapStore : ILocalSetupUnitOfWork, ILocalInstallationRepository,
    ILocalSetupAccountStore, IEnrollmentSigningRepository, IEnrollmentSigningUnitOfWork
{
    public LocalInstallationProfile? Profile { get; set; }
    public LocalSetupAccount Account { get; } = new() { Id = Guid.NewGuid(), PasswordHash = "setup-only", Revision = 1 };
    public Dictionary<Guid, LocalAttemptStep> Steps { get; } = [];
    public Dictionary<Guid, EnrollmentIssuerReference> Issuers { get; } = [];
    public Dictionary<Guid, EnrollmentIssuedArtifact> Issues { get; } = [];
    public ILocalInstallationRepository Profiles => this;
    public IEnrollmentSigningRepository Signing => this;

    public Task<LocalInstallationProfile?> ReadAsync(CancellationToken token)
    {
        return Task.FromResult(Profile);
    }

    Task<LocalSetupAccount?> ILocalSetupAccountStore.ReadAsync(CancellationToken token)
    {
        return Task.FromResult<LocalSetupAccount?>(Account);
    }

    public void Create(LocalInstallationProfile profile)
    {
        Profile = profile;
    }

    public void Create(LocalSetupAccount account)
    {
        throw new NotSupportedException();
    }

    public Task<bool> HasProgressAsync(CancellationToken token)
    {
        return Task.FromResult(Steps.Count > 0);
    }

    public Task ValidateProgressAsync(LocalInstallationProfile profile, CancellationToken token)
    {
        return Task.CompletedTask;
    }

    public Task ReplaceAsync(LocalInstallationProfile profile, long expectedRevision, CancellationToken token)
    {
        if (Profile?.ProfileRevision != expectedRevision || profile.ProfileRevision != expectedRevision + 1)
        {
            throw new InvalidOperationException("TestProfileRevisionConflict");
        }

        Profile = profile;
        return Task.CompletedTask;
    }

    public Task<LocalAttemptStep?> FindStepAsync(Guid operationId, CancellationToken token)
    {
        return Task.FromResult(Steps.GetValueOrDefault(operationId));
    }

    public void AddStep(LocalAttemptStep step)
    {
        Steps.Add(step.OperationId, step);
    }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(1);
    }

    public async Task ExecuteInTransactionAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken = default)
    {
        await action(cancellationToken);
        await SaveChangesAsync(cancellationToken);
    }

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
        return Task.FromResult(Issues.GetValueOrDefault(issueId));
    }

    public void AddIssuer(EnrollmentIssuerReference issuer)
    {
        Issuers.Add(issuer.Epoch, issuer);
    }

    public void AddIssue(EnrollmentIssuedArtifact artifact)
    {
        Issues.Add(artifact.IssueId, artifact);
    }
}
