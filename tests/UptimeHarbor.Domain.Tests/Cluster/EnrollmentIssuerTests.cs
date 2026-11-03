using UptimeHarbor.Domain.Cluster;
using Xunit;

namespace UptimeHarbor.Tests.Domain.Cluster;

public class EnrollmentIssuerTests
{
    private static readonly DateTimeOffset START = new(2026, 11, 3, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RegistrationHasFixedPurposeAndNoPrivateMaterial()
    {
        // Arrange
        Guid epoch = Guid.NewGuid();
        JournalPosition position = new(new ClusterIdentity(Guid.NewGuid(), 1), 3, 1);

        // Act
        EnrollmentIssuer issuer = new(epoch, position, Guid.NewGuid(), Guid.NewGuid(), "public-cert",
            new string('a', 64), new string('b', 64), START, START.AddYears(3));

        // Assert
        Assert.Equal(epoch, issuer.Id);
        Assert.Equal(position, issuer.GetPosition());
        Assert.Equal("UptimeHarbor.EnrollmentPeer.v1", issuer.Purpose);
        Assert.Equal(new string('A', 64), issuer.Fingerprint);
        Assert.Equal(new string('B', 64), issuer.SpkiDigest);
        Assert.Equal($"EnrollmentIssuer {epoch}", issuer.ToString());
        Assert.Null(issuer.RetiredAtUtc);
        Assert.Null(issuer.RevokedAtUtc);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1097)]
    public void RegistrationRejectsEmptyReversedOrExcessiveLifetime(int days)
    {
        // Arrange
        JournalPosition position = new(new ClusterIdentity(Guid.NewGuid(), 1), 3, 1);

        // Act
        Exception? failure = Record.Exception(() => new EnrollmentIssuer(Guid.NewGuid(), position,
            Guid.NewGuid(), Guid.NewGuid(), "public-cert", new string('A', 64), new string('B', 64), START, START.AddDays(days)));

        // Assert
        Assert.IsType<ArgumentException>(failure);
    }

    [Fact]
    public void PrivateKeyAndEmptyStorageAreRejected()
    {
        // Arrange
        JournalPosition position = new(new ClusterIdentity(Guid.NewGuid(), 1), 3, 1);

        // Act
        Exception? privateMaterial = Record.Exception(() => new EnrollmentIssuer(Guid.NewGuid(), position,
            Guid.NewGuid(), Guid.NewGuid(), "-----BEGIN PRIVATE KEY-----", new string('A', 64), new string('B', 64), START, START.AddYears(3)));
        Exception? missingStorage = Record.Exception(() => new EnrollmentIssuer(Guid.NewGuid(), position,
            Guid.NewGuid(), Guid.Empty, "public-cert", new string('A', 64), new string('B', 64), START, START.AddYears(3)));

        // Assert
        Assert.IsType<ArgumentException>(privateMaterial);
        Assert.IsType<ArgumentException>(missingStorage);
    }

    [Fact]
    public void RetirementAndRevocationAreIrreversibleAndPreserveFirstTimestamp()
    {
        // Arrange
        EnrollmentIssuer issuer = CreateIssuer();

        // Act
        issuer.Retire(START.AddDays(1));
        issuer.Retire(START.AddDays(2));
        issuer.Revoke(START.AddDays(3));
        issuer.Revoke(START.AddDays(4));

        // Assert
        Assert.Equal(START.AddDays(1), issuer.RetiredAtUtc);
        Assert.Equal(START.AddDays(3), issuer.RevokedAtUtc);
    }

    [Fact]
    public void InvalidTransitionDoesNotPartiallyChangeIssuer()
    {
        // Arrange
        EnrollmentIssuer issuer = CreateIssuer();

        // Act
        Exception? beforeIssue = Record.Exception(() => issuer.Retire(START.AddTicks(-1)));
        Exception? nonUtc = Record.Exception(() => issuer.Revoke(START.ToOffset(TimeSpan.FromHours(1))));

        // Assert
        Assert.IsType<ArgumentException>(beforeIssue);
        Assert.IsType<ArgumentException>(nonUtc);
        Assert.Null(issuer.RetiredAtUtc);
        Assert.Null(issuer.RevokedAtUtc);
    }

    private static EnrollmentIssuer CreateIssuer()
    {
        return new EnrollmentIssuer(Guid.NewGuid(), new(new ClusterIdentity(Guid.NewGuid(), 1), 3, 1),
            Guid.NewGuid(), Guid.NewGuid(), "public-cert", new string('A', 64), new string('B', 64), START, START.AddYears(3));
    }
}
