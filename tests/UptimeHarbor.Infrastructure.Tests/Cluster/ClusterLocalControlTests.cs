using System.Text.Json;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Models;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Cluster;

public class ClusterLocalControlTests
{
    [Fact]
    public void ValidSignedGrantOpensOnlyMatchingRecoveryAndIsNotReusableAfterFault()
    {
        // Arrange
        using ClusterControlFixture fixture = new();
        fixture.Sign("enable.json", fixture.Request("Enable") with { OperationsRevision = 3 });

        // Act
        bool wrongRevision = fixture.Control.CanWork(2, Guid.Empty);
        bool enabled = fixture.Control.CanWork(3, Guid.Empty);
        fixture.Readiness.InvalidateRecovery();
        bool stale = fixture.Control.CanWork(3, Guid.Empty);
        fixture.Sign("enable.json", fixture.Request("Enable") with { OperationsRevision = 3 });
        bool renewed = fixture.Control.CanWork(3, Guid.Empty);

        // Assert
        Assert.False(wrongRevision);
        Assert.True(enabled);
        Assert.False(stale);
        Assert.True(renewed);
    }

    [Fact]
    public void RestartAndBlockedMarkerCannotReuseEnableFromPreviousProcess()
    {
        // Arrange
        using ClusterControlFixture fixture = new();
        fixture.Sign("enable.json", fixture.Request("Enable"));
        Assert.True(fixture.Control.CanWork(0, Guid.Empty));
        ClusterLocalControl restarted = fixture.NewControl();

        // Act
        bool oldProcessGrant = restarted.CanWork(0, Guid.Empty);
        File.WriteAllText(Path.Combine(fixture.Configuration.Directory, "blocked"), "operator revoked control");
        bool blocked = fixture.Control.CanWork(0, Guid.Empty);

        // Assert
        Assert.False(oldProcessGrant);
        Assert.False(blocked);
    }

    [Fact]
    public void MaintenanceSurvivesLongPauseAndRestartAndRequiresNewExplicitResumeAndEnable()
    {
        // Arrange
        using ClusterControlFixture fixture = new();
        Guid maintenance = Guid.NewGuid();
        fixture.Sign("enable.json", fixture.Request("Enable"));
        Assert.True(fixture.Control.CanWork(0, Guid.Empty));
        fixture.Control.LatchMaintenance(maintenance);
        fixture.Time.Now = fixture.Time.Now.AddYears(2);
        ClusterLocalControl restarted = fixture.NewControl();

        // Act
        bool before = restarted.CanRunConsensus(maintenance);
        fixture.Sign("resume.json", fixture.Request("ResumeConsensus", restarted) with { MaintenanceId = maintenance });
        bool resumed = restarted.CanRunConsensus(maintenance);
        bool workBeforeEnable = restarted.CanWork(4, Guid.Empty);
        fixture.Sign("enable.json", fixture.Request("Enable", restarted) with { OperationsRevision = 4 });
        bool enabled = restarted.CanWork(4, Guid.Empty);

        // Assert
        Assert.False(before);
        Assert.True(resumed);
        Assert.True(restarted.CanLeaveMaintenance(maintenance));
        Assert.False(workBeforeEnable);
        Assert.True(enabled);
        Assert.False(File.Exists(Path.Combine(fixture.Configuration.Directory, "maintenance")));
    }

    [Theory]
    [InlineData("identity")]
    [InlineData("expired")]
    [InlineData("future")]
    [InlineData("challenge")]
    public void WrongOrStaleProofDoesNotOpenGate(string failure)
    {
        // Arrange
        using ClusterControlFixture fixture = new();
        ClusterControlRequest request = fixture.Request("Enable");
        request = failure switch
        {
            "identity" => request with { StorageGeneration = Guid.NewGuid() },
            "expired" => request with { IssuedAtUtc = fixture.Time.Now.AddMinutes(-11), ExpiresAtUtc = fixture.Time.Now.AddMinutes(-1) },
            "future" => request with { IssuedAtUtc = fixture.Time.Now.AddMinutes(1) },
            "challenge" => request with { Challenge = Guid.NewGuid() },
            _ => throw new ArgumentException(nameof(failure))
        };
        fixture.Sign("enable.json", request);

        // Act
        bool enabled = fixture.Control.CanWork(0, Guid.Empty);

        // Assert
        Assert.False(enabled);
    }

    [Fact]
    public void TamperedSignedPayloadAndChangedOperatorKeyAreRejected()
    {
        // Arrange
        using ClusterControlFixture fixture = new();
        fixture.Sign("enable.json", fixture.Request("Enable"));
        string file = Path.Combine(fixture.Configuration.Directory, "enable.json");
        SignedClusterControl original = JsonSerializer.Deserialize<SignedClusterControl>(File.ReadAllBytes(file))!;
        byte[] changed = Convert.FromBase64String(original.Payload);
        changed[0] ^= 1;
        File.WriteAllBytes(file, JsonSerializer.SerializeToUtf8Bytes(original with { Payload = Convert.ToBase64String(changed) }));

        // Act
        Exception? tampered = Record.Exception(() => fixture.Control.CanWork(0, Guid.Empty));
        fixture.Sign("enable.json", fixture.Request("Enable"));
        fixture.Configuration.PublicKeyDigest = new string('0', 64);
        Exception? wrongKey = Record.Exception(() => fixture.Control.CanWork(0, Guid.Empty));

        // Assert
        Assert.IsType<InvalidDataException>(tampered);
        Assert.IsType<InvalidDataException>(wrongKey);
    }

    [Fact]
    public void ManualIsolationRequiresEveryFormerInstanceAndCredentialRevocation()
    {
        // Arrange
        using ClusterControlFixture fixture = new();
        Guid second = Guid.NewGuid();
        Guid third = Guid.NewGuid();
        Dictionary<Guid, Guid> former = new()
        {
            [fixture.Options.NodeId] = fixture.Options.StorageGeneration, [second] = Guid.NewGuid(), [third] = Guid.NewGuid()
        };
        ManualIsolationConfirmation proof = new(Guid.NewGuid(), Guid.NewGuid(), fixture.Time.Now,
            former.Select(item => new IsolatedClusterInstance(item.Key, item.Value, true, true, true, true, true, new string('A', 64))).ToArray(),
            123, true, true, new string('B', 64));

        // Act
        string valid = ManualIsolationValidation.Validate(proof, former, fixture.Time.Now, 123, true);
        Exception? missing = Record.Exception(() => ManualIsolationValidation.Validate(proof with { Instances = proof.Instances.Take(1).ToArray() },
            former, fixture.Time.Now, 123, true));
        Exception? token = Record.Exception(() => ManualIsolationValidation.Validate(proof with { PreviousBotTokenRevoked = false },
            former, fixture.Time.Now, 123, true));
        Exception? timeoutOnly = Record.Exception(() => ManualIsolationValidation.Validate(proof with
        {
            Instances = proof.Instances.Select(item => item with { Stopped = false }).ToArray()
        }, former, fixture.Time.Now, 123, true));

        // Assert
        Assert.Equal(64, valid.Length);
        Assert.IsType<InvalidDataException>(missing);
        Assert.IsType<InvalidDataException>(token);
        Assert.IsType<InvalidDataException>(timeoutOnly);
    }
}
