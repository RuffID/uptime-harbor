using Microsoft.Extensions.Logging;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Installation.Renewal;
using UptimeHarbor.Infrastructure.Installation.Renewal.Models;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Installation.Renewal;

public class LocalVoterCredentialBindingFixture : IDisposable
{
    public LocalVoterCredentialFixture Local { get; }
    public BindTransport Transport { get; }
    public CapturedLog Logs { get; } = new();
    public LocalVoterCredentialBindingCoordinator Coordinator { get; private set; }
    public CancellationToken Token => TestContext.Current.CancellationToken;
    public LocalVoterCredentialBindIntent Intent => StrictOnboardingJson.Decode<LocalVoterCredentialBindIntent>(
        Local.Files.Read(Local.Reference, "bind-intent")!);
    public LocalVoterCredentialBinding Result => StrictOnboardingJson.Decode<LocalVoterCredentialBinding>(
        Local.Files.Read(Local.Reference, "bind-result")!);

    public LocalVoterCredentialBindingFixture(bool manual = false)
    {
        Local = new(manual);
        Transport = new(this);
        Coordinator = CreateCoordinator();
    }

    public LocalVoterCredentialBindingCoordinator CreateCoordinator()
    {
        return new(Local.Store, Local.Files, Local.State, Local.Authority.SubjectAuthority,
            Transport, Local.Authority.Time, Logs);
    }

    public async Task PrepareAsync()
    {
        await Local.AuthorizeAsync();
        await Local.SignAsync();
        await Local.PinAsync();
    }

    public Task<LocalVoterCredentialBinding> BindAsync()
    {
        return Coordinator.BindAsync(Local.Reference, Local.Authority.SigningCall(), Token);
    }

    public Task<LocalVoterCredentialBinding> ReadAsync()
    {
        return Coordinator.ReadBoundAsync(Local.Reference, Token);
    }

    public void Reopen()
    {
        Local.Reopen();
        Coordinator = CreateCoordinator();
    }

    public void Rewrite<T>(string stage, T value)
    {
        File.Delete(Local.PathFor(stage));
        Local.Files.WriteOnce(Local.Reference, stage, StrictOnboardingJson.Encode(value));
    }

    public void Retire()
    {
        RevisionCommand bind = Intent.Command;
        RevisionCommand retire = bind with
        {
            OperationId = Guid.NewGuid(), Action = "RetireOldCredential",
            EnrollmentTrust = bind.EnrollmentTrust! with
            {
                CoordinatorAtUtc = Local.Authority.Time.Now, CoordinatorTerm = Local.Authority.Scenario.State.Term,
                Rotation = new(bind.EnrollmentTrust.Rotation!.Renewal, null, new string('A', 64))
            }
        };
        Assert.Equal(OperationOutcome.Committed, Local.Authority.Scenario.Apply(retire).Outcome);
        Local.Authority.Publish();
    }

    public void Dispose()
    {
        Local.Dispose();
    }

    public class BindTransport(LocalVoterCredentialBindingFixture fixture) : IRevisionTransport
    {
        public List<RevisionCommand> Sent { get; } = [];
        public bool FailBeforeCommit { get; set; }
        public bool LoseResponse { get; set; }
        public bool DoNotCommit { get; set; }
        public Func<CommandResult, CommandResult>? AlterResponse { get; set; }
        public Action? AfterCommit { get; set; }
        public Action<RevisionCommand>? BeforeCommit { get; set; }

        public Task<IReadOnlyList<CommandResult>> SendAsync(IReadOnlyList<RevisionCommand> commands, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            RevisionCommand command = Assert.Single(commands);
            Assert.Equal(fixture.Intent.Command, command);
            BeforeCommit?.Invoke(command);
            Sent.Add(command);
            if (FailBeforeCommit)
            {
                throw new IOException("SimulatedUnknownCommit");
            }

            if (DoNotCommit)
            {
                CommandResult unknown = new(command.OperationId, RevisionCodec.Digest(command), OperationOutcome.Committed,
                    fixture.Local.Authority.Scenario.State.Index + 1, 1, fixture.Local.Authority.Scenario.State.Index + 1);
                return Task.FromResult<IReadOnlyList<CommandResult>>([unknown]);
            }

            RevisionCommand stamped = command with
            {
                EnrollmentTrust = command.EnrollmentTrust! with
                {
                    CoordinatorAtUtc = fixture.Local.Authority.Time.Now,
                    CoordinatorTerm = fixture.Local.Authority.Scenario.State.Term
                }
            };
            CommandResult result = fixture.Local.Authority.Scenario.Apply(stamped);
            Assert.Equal(OperationOutcome.Committed, result.Outcome);
            fixture.Local.Authority.Publish();
            AfterCommit?.Invoke();
            if (LoseResponse)
            {
                throw new IOException("SimulatedLostBindResponse");
            }

            if (AlterResponse is not null)
            {
                result = AlterResponse(result);
            }

            return Task.FromResult<IReadOnlyList<CommandResult>>([result]);
        }
    }

    public class CapturedLog : ILogger<LocalVoterCredentialBindingCoordinator>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel level)
        {
            return true;
        }

        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(formatter(state, exception));
        }
    }
}
