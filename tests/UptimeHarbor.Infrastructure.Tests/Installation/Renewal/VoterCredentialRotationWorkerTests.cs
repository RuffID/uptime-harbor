using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Installation;
using UptimeHarbor.Infrastructure.Installation.Renewal;
using UptimeHarbor.Tests.Infrastructure.Cluster.Renewal;
using Xunit;
using Scenario = UptimeHarbor.Tests.Infrastructure.Installation.Renewal.LocalVoterRenewalTests.Scenario;

namespace UptimeHarbor.Tests.Infrastructure.Installation.Renewal;

public class VoterCredentialRotationWorkerTests
{
    [Fact]
    public async Task SingletonWorkerRetiresAtOverlapBoundaryAndContinuesAfterLostResponse()
    {
        // Arrange
        using Scenario scenario = new(remoteIssuer: false);
        using WorkerFixture worker = new(scenario);
        CancellationToken token = TestContext.Current.CancellationToken;
        var authority = scenario.Local.Authority;
        List<string> events = [];
        scenario.Transport.Audit = new(new AuditCapture(events));
        scenario.Local.State.ConfirmCommonOpen(scenario.Local.State.Current!);
        await worker.StartAsync(token);
        var next = await worker.Time.NextAsync(token);
        var published = Assert.IsType<DynamicPeerTls.Snapshot>(scenario.Tls.Capture());
        var bound = VoterCredentialPolicy.Latest(authority.Scenario.State, authority.SubjectId)!;
        DateTimeOffset deadline = bound.OverlapUntilUtc!.Value;
        var previous = authority.Scenario.State.Operations.Credentials[scenario.Baseline];
        authority.Time.Now = deadline.AddTicks(-1);
        next.Fire();
        next = await worker.Time.NextAsync(token);
        Assert.Equal(2, scenario.Transport.Sent.Count);
        Assert.True(VoterCredentialPolicy.IsUsable(authority.Scenario.State, previous, authority.Time.Now));

        // Act
        authority.Time.Now = deadline;
        scenario.Transport.FailAfter = "retire-response";
        next.Fire();
        next = await worker.Time.NextAsync(token);
        RevisionCommand retire = Assert.Single(scenario.Transport.Sent, command => command.Action == "RetireOldCredential");
        var decision = authority.Scenario.State.Results[retire.OperationId];
        var retired = VoterCredentialPolicy.Latest(authority.Scenario.State, authority.SubjectId)!;
        Assert.Equal(deadline, retired.RetiredAtUtc);
        Assert.False(VoterCredentialPolicy.IsUsable(authority.Scenario.State, previous, deadline));
        Assert.Same(published, scenario.Tls.Capture());

        authority.Scenario.State = authority.Scenario.State with { Term = decision.Term + 1, Index = decision.Index + 1 };
        authority.Publish();
        next.Fire();
        next = await worker.Time.NextAsync(token);
        Assert.Equal(decision, authority.Scenario.State.Results[retire.OperationId]);
        Assert.Single(scenario.Transport.Sent, command => command.Action == "RetireOldCredential");

        authority.Time.Now = bound.Renewal.Issuance.NotAfterUtc.AddDays(-7);
        scenario.PublishProcess(epoch: 4);
        next.Fire();
        await worker.Time.NextAsync(token);

        // Assert
        RevisionCommand nextAuthorize = scenario.Transport.Sent.Last(command => command.Action == "AuthorizeVoterCredential");
        Assert.Equal(published.CredentialId, nextAuthorize.EnrollmentTrust!.Rotation!.Renewal.PreviousCredentialId);
        Assert.Equal(published.CredentialRevision, nextAuthorize.EnrollmentTrust.Rotation.Renewal.PreviousCredentialRevision);
        Assert.NotEqual(published.CredentialId, scenario.Tls.Capture()!.CredentialId);
        Assert.Equal(2, authority.Journal.Commits);
        string retiredEvent = Assert.Single(events, message => message.Contains("CertificateRetired", StringComparison.Ordinal));
        Assert.DoesNotContain(retire.EnrollmentTrust!.Rotation!.InstallationProofDigest!, retiredEvent);
        Assert.DoesNotContain(published.Fingerprint, retiredEvent);
        Assert.Equal(2, events.Count(message => message.Contains("CertificateAuthorized", StringComparison.Ordinal)));
        Assert.Equal(2, events.Count(message => message.Contains("CertificateBound", StringComparison.Ordinal)));
        await worker.StopAsync(token);
    }

    private sealed class AuditCapture(List<string> events) : ILogger<OnboardingAudit>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            events.Add(formatter(state, exception));
        }
    }

    [Fact]
    public async Task WorkerWaitsForCommonOpenDueDateMajorityAndIssuerThenPublishesOnce()
    {
        // Arrange
        using Scenario scenario = new();
        VoterCredentialAuthorityFixture authority = scenario.Local.Authority;
        DateTimeOffset due = authority.Scenario.State.Operations.Credentials[scenario.Baseline].ExpiresAtUtc.AddDays(-7);
        authority.Time.Now = due.AddTicks(-1);
        scenario.PublishProcess(epoch: 3);
        using WorkerFixture worker = new(scenario);
        CancellationToken token = TestContext.Current.CancellationToken;

        // Act
        await worker.StartAsync(token);
        ManualWorkerTime.Tick next = await worker.Time.NextAsync(token);

        // Assert
        Assert.Equal(0, worker.Scopes);
        Assert.Equal(0, authority.SubjectBarrier.Calls);

        // Act
        scenario.Local.State.ConfirmCommonOpen(scenario.Local.State.Current!);
        next.Fire();
        next = await worker.Time.NextAsync(token);

        // Assert
        Assert.Empty(scenario.Local.Files.Writes);
        Assert.Empty(scenario.Transport.Sent);

        // Act
        authority.Time.Now = due;
        authority.SubjectBarrier.Available = false;
        next.Fire();
        next = await worker.Time.NextAsync(token);

        // Assert
        Assert.Empty(scenario.Local.Files.Writes);
        Assert.Empty(scenario.Transport.Sent);

        // Act
        authority.SubjectBarrier.Available = true;
        authority.Remote.Available = false;
        next.Fire();
        next = await worker.Time.NextAsync(token);

        // Assert
        RevisionCommand authorize = Assert.Single(scenario.Transport.Sent);
        Assert.Empty(authority.Journal.Issued);

        // Act
        authority.Remote.Available = true;
        next.Fire();
        next = await worker.Time.NextAsync(token);

        // Assert
        DynamicPeerTls.Snapshot published = Assert.IsType<DynamicPeerTls.Snapshot>(scenario.Tls.Capture());
        Assert.Equal(authorize.EnrollmentTrust!.Rotation!.Renewal.Issuance.IssueId, published.CredentialId);

        // Act
        next.Fire();
        await worker.Time.NextAsync(token);

        // Assert
        Assert.Same(published, scenario.Tls.Capture());
        Assert.Equal(["AuthorizeVoterCredential", "BindVoterCredential"], scenario.Transport.Sent.Select(item => item.Action));
        Assert.Equal(1, authority.Journal.Commits);
        await worker.StopAsync(token);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorkerStopsBeforeDurableIntentWhenRuntimeClosesDuringBarrier(bool closeGeneration)
    {
        // Arrange
        using Scenario scenario = new();
        using WorkerFixture worker = new(scenario);
        InstallationState state = scenario.Local.State;
        LocalInstallationProfile profile = state.Current!;
        state.ConfirmCommonOpen(profile);
        CancellationToken token = TestContext.Current.CancellationToken;
        Task? drain = null;
        scenario.Local.Authority.SubjectBarrier.After = call =>
        {
            if (call != 2)
            {
                return;
            }

            if (closeGeneration)
            {
                drain = worker.Gate.CloseAsync();
            }
            else
            {
                state.Publish(profile with { Phase = InstallationPhase.Paused, ProfileRevision = profile.ProfileRevision + 1 });
            }
        };

        // Act
        await worker.StartAsync(token);
        await worker.Time.NextAsync(token);

        // Assert
        Assert.Equal(2, scenario.Local.Authority.SubjectBarrier.Calls);
        Assert.Empty(scenario.Local.Files.Writes);
        Assert.Empty(scenario.Transport.Sent);
        Assert.Equal(0, scenario.Local.Authority.Remote.Calls);
        await worker.StopAsync(token);
        if (closeGeneration)
        {
            Assert.NotNull(drain);
            await drain.WaitAsync(token);
        }
    }

    [Fact]
    public async Task WorkerResumesAcceptedBindAfterCancellationWithoutResendingOrIssuing()
    {
        // Arrange
        using Scenario scenario = new();
        using WorkerFixture worker = new(scenario);
        CancellationToken token = TestContext.Current.CancellationToken;
        using CancellationTokenSource run = CancellationTokenSource.CreateLinkedTokenSource(token);
        TaskCompletionSource accepted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        scenario.Local.State.ConfirmCommonOpen(scenario.Local.State.Current!);
        scenario.Transport.AfterCommit = command =>
        {
            if (command.Action == "BindVoterCredential")
            {
                run.Cancel();
                accepted.TrySetResult();
            }
        };

        // Act
        await worker.StartAsync(run.Token);
        await accepted.Task.WaitAsync(TimeSpan.FromSeconds(15), token);
        await worker.StopAsync(token);

        // Assert
        Assert.Equal(scenario.Baseline, scenario.Tls.Capture()!.CredentialId);
        Assert.DoesNotContain("bind-result", scenario.Local.Files.Writes);
        RevisionCommand bind = scenario.Transport.Sent[1];

        // Act
        await worker.StartAsync(token);
        await worker.Time.NextAsync(token);

        // Assert
        Assert.Equal(bind.EnrollmentTrust!.Rotation!.Renewal.Issuance.IssueId, scenario.Tls.Capture()!.CredentialId);
        Assert.Equal(["AuthorizeVoterCredential", "BindVoterCredential"], scenario.Transport.Sent.Select(item => item.Action));
        Assert.Equal(1, scenario.Local.Authority.Remote.Calls);
        await worker.StopAsync(token);
    }

    private sealed class WorkerFixture : IDisposable
    {
        private readonly ServiceProvider services;
        private readonly VoterCredentialRotationWorker worker;
        public ManualWorkerTime Time { get; } = new();
        public RuntimeWriteGate Gate { get; } = new();
        public int Scopes { get; private set; }

        public WorkerFixture(Scenario scenario)
        {
            services = new ServiceCollection().AddScoped(_ =>
            {
                Scopes++;
                return scenario.Renewal;
            }).BuildServiceProvider(validateScopes: true);
            worker = new(services.GetRequiredService<IServiceScopeFactory>(), scenario.Local.State, Gate,
                Options.Create(scenario.Local.Authority.SubjectOptions), Time, NullLogger<VoterCredentialRotationWorker>.Instance);
            long generation = scenario.Local.State.Current!.RuntimeGeneration;
            Gate.Open(generation);
            worker.ConfigureGeneration(Gate, generation);
        }

        public Task StartAsync(CancellationToken token)
        {
            return worker.StartAsync(token);
        }

        public Task StopAsync(CancellationToken token)
        {
            return worker.StopAsync(token);
        }

        public void Dispose()
        {
            worker.Dispose();
            services.Dispose();
        }
    }

    private sealed class ManualWorkerTime : TimeProvider
    {
        private readonly Channel<Tick> ticks = Channel.CreateUnbounded<Tick>();

        public Task<Tick> NextAsync(CancellationToken token)
        {
            return ticks.Reader.ReadAsync(token).AsTask().WaitAsync(TimeSpan.FromSeconds(15), token);
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Assert.Equal(TimeSpan.FromMinutes(1), dueTime);
            Assert.Equal(Timeout.InfiniteTimeSpan, period);
            Tick tick = new(callback, state);
            Assert.True(ticks.Writer.TryWrite(tick));
            return tick;
        }

        public sealed class Tick(TimerCallback callback, object? state) : ITimer
        {
            public void Fire()
            {
                callback(state);
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                throw new NotSupportedException();
            }

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync()
            {
                return ValueTask.CompletedTask;
            }
        }
    }
}
