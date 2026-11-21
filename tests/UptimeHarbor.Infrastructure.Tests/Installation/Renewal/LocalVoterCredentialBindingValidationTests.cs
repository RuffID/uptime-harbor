using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Installation.Renewal.Models;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Installation.Renewal;

public class LocalVoterCredentialBindingValidationTests
{
    [Theory]
    [InlineData("intent", false)]
    [InlineData("authorization", false)]
    [InlineData("artifact", false)]
    [InlineData("bind-intent", false)]
    [InlineData("intent", true)]
    [InlineData("authorization", true)]
    [InlineData("artifact", true)]
    [InlineData("bind-intent", true)]
    [InlineData("bind-result", true)]
    public async Task MissingOrCorruptMaterialAfterBind_RefusesWithoutSigner(string stage, bool corrupt)
    {
        // Arrange
        using LocalVoterCredentialBindingFixture fixture = new();
        await fixture.PrepareAsync();
        await fixture.BindAsync();
        if (corrupt)
        {
            File.WriteAllText(fixture.Local.PathFor(stage), "corrupt");
        }
        else
        {
            File.Delete(fixture.Local.PathFor(stage));
        }

        fixture.Reopen();

        // Act
        Exception? bindError = await Record.ExceptionAsync(() => fixture.BindAsync());
        Exception? readError = await Record.ExceptionAsync(() => fixture.ReadAsync());

        // Assert
        Assert.NotNull(bindError);
        Assert.NotNull(readError);
        Assert.Single(fixture.Transport.Sent);
        Assert.Equal(1, fixture.Local.Authority.Remote.Calls);
        Assert.Equal(1, fixture.Local.Authority.Journal.Commits);
    }

    [Theory]
    [InlineData("operation")]
    [InlineData("authorization")]
    [InlineData("authorization-index")]
    [InlineData("request-digest")]
    [InlineData("artifact-digest")]
    [InlineData("revision")]
    [InlineData("stamp")]
    public async Task AlteredProtectedBindIntent_CannotReplayEvenWithValidAead(string field)
    {
        // Arrange
        using LocalVoterCredentialBindingFixture fixture = new();
        await fixture.PrepareAsync();
        await fixture.BindAsync();
        LocalVoterCredentialBindIntent intent = fixture.Intent;
        LocalVoterCredentialBindIntent changed;
        if (field == "operation")
        {
            changed = intent with { Command = intent.Command with { OperationId = Guid.NewGuid() } };
        }
        else if (field == "authorization")
        {
            changed = intent with { AuthorizationOperationId = Guid.NewGuid() };
        }
        else if (field == "authorization-index")
        {
            changed = intent with { AuthorizationIndex = intent.AuthorizationIndex + 1 };
        }
        else if (field == "request-digest")
        {
            changed = intent with { RequestDigest = new string('A', 64) };
        }
        else if (field == "artifact-digest")
        {
            changed = intent with { ArtifactDigest = new string('B', 64) };
        }
        else if (field == "revision")
        {
            changed = intent with { Command = intent.Command with { BaseRevision = 123 } };
        }
        else if (field == "stamp")
        {
            changed = intent with { Command = intent.Command with
                { EnrollmentTrust = intent.Command.EnrollmentTrust! with { CoordinatorAtUtc = fixture.Local.Authority.Time.Now, CoordinatorTerm = 1 } } };
        }
        else
        {
            throw new InvalidOperationException("UnknownTestField");
        }
        fixture.Rewrite("bind-intent", changed);
        fixture.Reopen();

        // Act
        Exception? error = await Record.ExceptionAsync(() => fixture.BindAsync());

        // Assert
        Assert.NotNull(error);
        Assert.Single(fixture.Transport.Sent);
        Assert.Equal(1, fixture.Local.Authority.Remote.Calls);
        Assert.Equal(2, fixture.Logs.Messages.Count);
    }

    [Theory]
    [InlineData("operation", false)]
    [InlineData("digest", false)]
    [InlineData("index", false)]
    [InlineData("applied", false)]
    [InlineData("term", false)]
    [InlineData("outcome", false)]
    [InlineData("reason", false)]
    [InlineData("operation", true)]
    [InlineData("digest", true)]
    [InlineData("index", true)]
    [InlineData("applied", true)]
    [InlineData("term", true)]
    [InlineData("outcome", true)]
    [InlineData("reason", true)]
    public async Task AlteredLocalOrAcceptedResult_RefusesExactBinding(string field, bool accepted)
    {
        // Arrange
        using LocalVoterCredentialBindingFixture fixture = new();
        await fixture.PrepareAsync();
        await fixture.BindAsync();
        LocalVoterCredentialBinding original = fixture.Result;
        CommandResult changed = Change(original.Result, field);
        if (accepted)
        {
            RevisionImage image = fixture.Local.Authority.Scenario.State;
            fixture.Local.Authority.Scenario.State = image with
            {
                Results = image.Results.SetItem(original.Result.OperationId, changed)
            };
            fixture.Local.Authority.Publish();
        }
        else
        {
            fixture.Rewrite("bind-result", original with { Result = changed });
        }

        fixture.Reopen();

        // Act
        Exception? bindError = await Record.ExceptionAsync(() => fixture.BindAsync());
        Exception? readError = await Record.ExceptionAsync(() => fixture.ReadAsync());

        // Assert
        Assert.NotNull(bindError);
        Assert.NotNull(readError);
        Assert.Single(fixture.Transport.Sent);
        Assert.Equal(2, fixture.Logs.Messages.Count);
        Assert.Equal(1, fixture.Local.Authority.Remote.Calls);
    }

    [Theory]
    [InlineData("bind-operation")]
    [InlineData("revision")]
    [InlineData("credential")]
    [InlineData("credential-missing")]
    [InlineData("result-missing")]
    [InlineData("authorization-missing")]
    [InlineData("future-time")]
    public async Task AlteredAcceptedBindingGraph_DoesNotAcceptSavedLocalResult(string field)
    {
        // Arrange
        using LocalVoterCredentialBindingFixture fixture = new();
        await fixture.PrepareAsync();
        await fixture.BindAsync();
        RevisionImage image = fixture.Local.Authority.Scenario.State;
        Guid issue = fixture.Local.Authority.Request.Renewal.Issuance.IssueId;
        VoterCredentialRotation rotation = image.OnboardingContracts!.VoterCredentialRotations![issue];
        if (field is "bind-operation" or "revision" or "future-time")
        {
            VoterCredentialRotation changed = rotation;
            if (field == "bind-operation")
            {
                changed = rotation with { BindOperationId = Guid.NewGuid() };
            }
            else if (field == "revision")
            {
                changed = rotation with { CredentialRevision = rotation.CredentialRevision + 1 };
            }
            else
            {
                changed = rotation with { BoundAtUtc = fixture.Local.Authority.Time.Now.AddMinutes(1) };
            }

            image = image with { OnboardingContracts = image.OnboardingContracts with
                { VoterCredentialRotations = image.OnboardingContracts.VoterCredentialRotations.SetItem(issue, changed) } };
        }
        else if (field is "result-missing" or "authorization-missing")
        {
            Guid operationId = rotation.AuthorizationOperationId;
            if (field == "result-missing")
            {
                operationId = rotation.BindOperationId!.Value;
            }

            image = image with { Results = image.Results.Remove(operationId) };
        }
        else
        {
            OperationalCredential credential = image.Operations.Credentials[issue];
            if (field == "credential-missing")
            {
                image = image with { Operations = image.Operations with { Credentials = image.Operations.Credentials.Remove(issue) } };
            }
            else
            {
                image = image with { Operations = image.Operations with
                    { Credentials = image.Operations.Credentials.SetItem(issue, credential with { PublicCertificate = Convert.ToBase64String([1, 2, 3]) }) } };
            }
        }

        fixture.Local.Authority.Scenario.State = image;
        fixture.Local.Authority.Publish();
        fixture.Reopen();

        // Act
        Exception? error = await Record.ExceptionAsync(() => fixture.ReadAsync());

        // Assert
        Assert.NotNull(error);
        Assert.Single(fixture.Transport.Sent);
        Assert.Equal(1, fixture.Local.Authority.Remote.Calls);
    }

    internal static CommandResult Change(CommandResult result, string field)
    {
        if (field == "operation")
        {
            return result with { OperationId = Guid.NewGuid() };
        }

        if (field == "digest")
        {
            return result with { Digest = new string('A', 64) };
        }

        if (field == "index")
        {
            return result with { Index = result.Index + 1 };
        }

        if (field == "applied")
        {
            return result with { AppliedRevision = result.AppliedRevision + 1 };
        }

        if (field == "term")
        {
            return result with { Term = result.Term + 1 };
        }

        if (field == "outcome")
        {
            return result with { Outcome = OperationOutcome.Rejected };
        }

        if (field == "reason")
        {
            return result with { Reason = "AlteredResult" };
        }

        throw new InvalidOperationException("UnknownTestField");
    }
}
