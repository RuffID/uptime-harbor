using System.Text.Json;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Infrastructure.Installation;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Installation;

public class LocalProfileEqualityTests
{
    [Fact]
    public void DurableRoundTripPreservesIdentityWithNewImmutableCollectionsButRejectsChangedState()
    {
        // Arrange
        using GenesisFixture fixture = new();
        LocalInstallationProfile original = fixture.Local.Store.Profile!;
        LocalInstallationProfile read = JsonSerializer.Deserialize<LocalInstallationProfile>(JsonSerializer.Serialize(original))!;

        // Act
        bool same = LocalProfileEquality.Same(original, read);
        bool changedKey = LocalProfileEquality.Same(original, read with { KeyReference = "changed" });
        bool changedRevision = LocalProfileEquality.Same(original, read with { ProfileRevision = read.ProfileRevision + 1 });
        bool changedOperation = LocalProfileEquality.Same(original, read with
        {
            OperationIds = read.OperationIds!.SetItem("bootstrap", Guid.NewGuid())
        });

        // Assert
        Assert.NotSame(original.OperationIds, read.OperationIds);
        Assert.True(same);
        Assert.False(changedKey);
        Assert.False(changedRevision);
        Assert.False(changedOperation);
    }
}
