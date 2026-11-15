using System.Collections.Immutable;
using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Infrastructure.Installation;
using UptimeHarbor.Infrastructure.Persistence;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Installation;

public class ProgressValidationTests
{
    [Fact]
    public async Task HistoryBeyondTenThousandIsValidated()
    {
        // Arrange
        using ProgressValidationFixture fixture = new(10001);

        // Act
        await fixture.Store.ValidateProgressAsync(fixture.Profile, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(10001, fixture.ReadIds.Count);
        Assert.Equal(fixture.Steps.Select(item => item.Id), fixture.ReadIds);
        Assert.All(fixture.PageSizes, size => Assert.InRange(size, 0, 256));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(255)]
    [InlineData(256)]
    [InlineData(257)]
    [InlineData(9999)]
    [InlineData(10000)]
    [InlineData(10240)]
    public async Task EveryStepIsReadExactlyOnceInBoundedPages(int count)
    {
        // Arrange
        using ProgressValidationFixture fixture = new(count);
        fixture.AddAttempt();
        fixture.Steps.Reverse();
        Guid[] expected = fixture.Steps.Select(item => item.Id).Order().ToArray();

        // Act
        await fixture.Store.ValidateProgressAsync(fixture.Profile, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(expected, fixture.ReadIds);
        Assert.All(fixture.PageSizes, size => Assert.InRange(size, 0, 256));
        Assert.Equal(count / 256 + 1, fixture.PageSizes.Count);
    }

    [Theory]
    [InlineData(255, "digest")]
    [InlineData(256, "digest")]
    [InlineData(10000, "digest")]
    [InlineData(255, "foreign-row")]
    [InlineData(256, "foreign-row")]
    [InlineData(10000, "foreign-row")]
    [InlineData(255, "payload-id")]
    [InlineData(256, "payload-id")]
    [InlineData(10000, "payload-id")]
    [InlineData(10000, "profile-revision")]
    [InlineData(10000, "runtime-generation")]
    [InlineData(10000, "kind")]
    [InlineData(10000, "missing-evidence")]
    [InlineData(10000, "corrupt-json")]
    public async Task CorruptionAtPageBoundariesAndTailStillFailsClosed(int index, string damage)
    {
        // Arrange
        using ProgressValidationFixture fixture = new(10001);
        string expectedError = "LocalCommandStepBindingInvalid";
        switch (damage)
        {
            case "digest":
                fixture.ChangeStep(index, step => step with { RequestDigest = new string('F', 64) });
                break;
            case "foreign-row":
                fixture.Steps[index].AttemptId = Guid.NewGuid();
                expectedError = "ForeignLocalAttemptStep";
                break;
            case "payload-id":
                fixture.ChangeStep(index, step => step with { OperationId = Guid.NewGuid() });
                expectedError = "LocalStepIdentityMismatch";
                break;
            case "profile-revision":
                fixture.ChangeStep(index, step => step with { ProfileRevision = fixture.Profile.ProfileRevision + 1 });
                break;
            case "runtime-generation":
                fixture.ChangeStep(index, step => step with { RuntimeGeneration = fixture.Profile.RuntimeGeneration + 1 });
                break;
            case "kind":
                fixture.ChangeStep(index, step => step with { Kind = "Enrollment/ResumeOnboardingAttempt" });
                break;
            case "missing-evidence":
                fixture.ChangeStep(index, step => step with { Evidence = null });
                expectedError = "LocalStepEvidenceMissing";
                break;
            case "corrupt-json":
                fixture.Steps[index].Payload = "{";
                break;
        }

        // Act
        Exception? failure = await Record.ExceptionAsync(() => fixture.Store.ValidateProgressAsync(
            fixture.Profile, TestContext.Current.CancellationToken));

        // Assert
        if (damage == "corrupt-json")
        {
            Assert.IsAssignableFrom<JsonException>(failure);
        }
        else
        {
            Assert.Equal(expectedError, Assert.IsType<InvalidDataException>(failure).Message);
        }

        Assert.All(fixture.PageSizes, size => Assert.InRange(size, 0, 256));
    }

    [Theory]
    [InlineData("row")]
    [InlineData("node")]
    [InlineData("storage")]
    [InlineData("key")]
    [InlineData("multiple")]
    public async Task ForeignAttemptStillRejectsBeforeReadingSteps(string damage)
    {
        // Arrange
        using ProgressValidationFixture fixture = new(1);
        fixture.AddAttempt();
        LocalAttemptRecord row = fixture.Attempts[0];
        LocalOnboardingAttempt attempt = JsonSerializer.Deserialize<LocalOnboardingAttempt>(row.Payload)!;
        string expectedError = "LocalAttemptIdentityMismatch";
        switch (damage)
        {
            case "row":
                row.Id = Guid.NewGuid();
                expectedError = "ForeignLocalAttempt";
                break;
            case "node":
                attempt = attempt with { Request = attempt.Request with { NodeId = Guid.NewGuid() } };
                break;
            case "storage":
                attempt = attempt with { Request = attempt.Request with { StorageGeneration = Guid.NewGuid() } };
                break;
            case "key":
                attempt = attempt with { PrivateKeyReference = "foreign-key" };
                break;
            case "multiple":
                fixture.Attempts.Add(new() { Id = Guid.NewGuid(), Revision = 1, Payload = row.Payload });
                expectedError = "ForeignLocalAttempt";
                break;
        }

        row.Payload = JsonSerializer.Serialize(attempt);

        // Act
        InvalidDataException failure = await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Store.ValidateProgressAsync(fixture.Profile, TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(expectedError, failure.Message);
        Assert.Empty(fixture.ReadIds);
    }

    [Fact]
    public async Task OriginalFixedSetupStepDoesNotRequireOnlineCommandEvidence()
    {
        // Arrange
        using ProgressValidationFixture fixture = new(1);
        fixture.Profile = fixture.Profile with
        {
            OperationIds = ImmutableDictionary<string, Guid>.Empty.Add("bootstrap", fixture.Steps[0].Id)
        };
        fixture.ChangeStep(0, step => step with { Kind = "bootstrap", Evidence = null });

        // Act
        await fixture.Store.ValidateProgressAsync(fixture.Profile, TestContext.Current.CancellationToken);

        // Assert
        Assert.Single(fixture.ReadIds);
    }

    [Fact]
    public async Task OnlineLifecycleStepKeepsItsOriginalCommandBindings()
    {
        // Arrange
        using OnlineLifecycleFixture online = new();
        using ProgressValidationFixture fixture = new(0);
        OnlineGrant grant = online.Sign(online.Grant(OnlineGrantPurpose.Process));
        RevisionCommand command = online.Command("RegisterOnlineProcess", grant);
        fixture.Profile = fixture.Profile with
        {
            ClusterId = grant.ClusterId, ClusterGeneration = grant.Generation, AttemptId = grant.AttemptId,
            NodeId = command.OriginNodeId, StorageGeneration = grant.StorageGeneration
        };
        LocalAttemptStep step = new(command.OperationId, grant.AttemptId, command.Action,
            RevisionCodec.Digest(command), "AcceptedOnlineProcess", fixture.Profile.ProfileRevision,
            fixture.Profile.RuntimeGeneration, online.Now, JsonSerializer.Serialize(command));
        fixture.Steps.Add(new() { Id = step.OperationId, AttemptId = step.AttemptId, Payload = JsonSerializer.Serialize(step) });

        // Act
        await fixture.Store.ValidateProgressAsync(fixture.Profile, TestContext.Current.CancellationToken);

        // Assert
        Assert.Single(fixture.ReadIds);

        // Arrange
        fixture.Profile = fixture.Profile with { StorageGeneration = Guid.NewGuid() };

        // Act
        InvalidDataException failure = await Assert.ThrowsAsync<InvalidDataException>(() =>
            fixture.Store.ValidateProgressAsync(fixture.Profile, TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal("LocalCommandStepBindingInvalid", failure.Message);
    }

    [Fact]
    public async Task CancellationStopsBeforeReadingTheNextPage()
    {
        // Arrange
        using ProgressValidationFixture fixture = new(257);
        using CancellationTokenSource cancellation = new();
        fixture.OnStepPage = page =>
        {
            if (page == 1)
            {
                cancellation.Cancel();
            }
        };

        // Act
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Store.ValidateProgressAsync(fixture.Profile, cancellation.Token));

        // Assert
        Assert.Equal(256, fixture.ReadIds.Count);
        Assert.Equal(new[] { 256, 0 }, fixture.PageSizes);
    }

    [Fact]
    public async Task ProductionPageQueriesTranslateToSqlServerWithoutOpeningConnection()
    {
        // Arrange
        using ProgressValidationFixture fixture = new(257);
        using UptimeHarborDbContext sql = new(new DbContextOptionsBuilder<UptimeHarborDbContext>().UseSqlServer().Options);

        // Act
        await fixture.Store.ValidateProgressAsync(fixture.Profile, TestContext.Current.CancellationToken);
        string first = fixture.TranslateStepQuery(0, sql);
        string second = fixture.TranslateStepQuery(1, sql);

        // Assert
        Assert.Contains("TOP(", first, StringComparison.Ordinal);
        Assert.Contains("ORDER BY", first, StringComparison.Ordinal);
        Assert.Contains("WHERE", second, StringComparison.Ordinal);
        Assert.Contains("ORDER BY", second, StringComparison.Ordinal);
        Assert.DoesNotContain("OFFSET", second, StringComparison.Ordinal);
        Assert.Equal(ConnectionState.Closed, sql.Database.GetDbConnection().State);
    }
}
