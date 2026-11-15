using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Cluster;

public class OnboardingBoundaryTests
{
    [Fact]
    public void EnrollmentLimitsFiveRejectionsPerOriginAndThirtyRequestsPerEndpointAndResetsAtMinuteBoundary()
    {
        // Arrange
        Clock time = new();
        EnrollmentRateLimiter limiter = new(time);
        List<bool> acquired = [];
        List<bool> blocked = [];

        // Act
        for (int origin = 0; origin < 6; origin++)
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                acquired.Add(limiter.TryAcquire("test-" + origin));
                limiter.Rejected("test-" + origin);
            }

            blocked.Add(limiter.TryAcquire("test-" + origin));
        }

        bool endpointFull = limiter.TryAcquire("other-test");
        time.Timestamp = TimeSpan.FromMinutes(1).Ticks - 1;
        bool before = limiter.TryAcquire("other-test");
        time.Timestamp++;
        bool after = limiter.TryAcquire("other-test");

        // Assert
        Assert.All(acquired, Assert.True);
        Assert.All(blocked, result => Assert.False(result));
        Assert.False(endpointFull);
        Assert.False(before);
        Assert.True(after);
    }

    [Fact]
    public void SuccessfulEnrollmentRequestsFromOneOriginOnlyConsumeEndpointLimit()
    {
        // Arrange
        Clock time = new();
        EnrollmentRateLimiter limiter = new(time);

        // Act
        bool[] acquired = Enumerable.Range(0, 30).Select(_ => limiter.TryAcquire("valid")).ToArray();
        bool sameOrigin = limiter.TryAcquire("valid");
        bool otherOrigin = limiter.TryAcquire("other");

        // Assert
        Assert.All(acquired, Assert.True);
        Assert.False(sameOrigin);
        Assert.False(otherOrigin);
    }

    [Fact]
    public void RejectedOriginDoesNotBlockOtherOriginsAndResetsAtMinuteBoundary()
    {
        // Arrange
        Clock time = new();
        EnrollmentRateLimiter limiter = new(time);
        List<bool> acquired = [];

        // Act
        foreach (int _ in Enumerable.Range(0, 5))
        {
            acquired.Add(limiter.TryAcquire("invalid"));
            limiter.Rejected("invalid");
        }

        bool blocked = limiter.TryAcquire("invalid");
        bool unaffected = limiter.TryAcquire("valid");
        time.Timestamp = TimeSpan.FromMinutes(1).Ticks - 1;
        bool before = limiter.TryAcquire("invalid");
        time.Timestamp++;
        bool after = limiter.TryAcquire("invalid");

        // Assert
        Assert.All(acquired, Assert.True);
        Assert.False(blocked);
        Assert.True(unaffected);
        Assert.False(before);
        Assert.True(after);
    }

    [Fact]
    public void ArchivedInvitationsRemainHistoryAndAllAncestorsRequireNewCompatibility()
    {
        // Arrange
        OnboardingStorageFixture fixture = new();
        RecoveryBaseline ancestor = Baseline(fixture.Image) with { PreviousOnboarding = fixture.Image.Onboarding };
        RecoveryBaseline parent = Baseline(fixture.Image) with { Ancestors = [ancestor] };
        RevisionImage recovered = new(Guid.NewGuid(), 3)
        {
            Index = fixture.Image.Index, Term = fixture.Image.Term, Results = fixture.Image.Results, RecoveryBaseline = parent
        };

        // Act
        OnboardingSnapshot.Validate(recovered);
        Exception? old = Record.Exception(() => OnboardingProtocol.ValidateSnapshot(recovered, new ClusterOptions()));
        RecoveryBaseline corrupt = ancestor with { PreviousOnboarding = ancestor.PreviousOnboarding! with { Version = 7 } };
        Exception? invalid = Record.Exception(() => OnboardingSnapshot.Validate(recovered with
        {
            RecoveryBaseline = parent with { Ancestors = [corrupt] }
        }));

        // Assert
        Assert.Null(recovered.Onboarding);
        Assert.IsType<InvalidDataException>(old);
        Assert.IsType<InvalidDataException>(invalid);
        Assert.Equal(fixture.Image.Onboarding, ancestor.PreviousOnboarding);
    }

    private static RecoveryBaseline Baseline(RevisionImage source)
    {
        return new(Guid.NewGuid(), source.ClusterId, source.Generation, source.Index, source.Term, source.Digest!, null,
            Guid.NewGuid(), Guid.NewGuid(), new string('A', 64), DateTimeOffset.UnixEpoch, source.Operations);
    }

    private class Clock : TimeProvider
    {
        public long Timestamp { get; set; }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp()
        {
            return Timestamp;
        }
    }
}
