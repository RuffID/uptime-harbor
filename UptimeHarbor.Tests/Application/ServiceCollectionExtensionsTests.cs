using Microsoft.Extensions.DependencyInjection;
using UptimeHarbor.Application;

namespace UptimeHarbor.Tests.Application;

public class ServiceCollectionExtensionsTests
{
    [Fact]
    public void AddApplication_RegistersSystemTimeProvider()
    {
        ServiceCollection services = new();

        using ServiceProvider serviceProvider = services
            .AddApplication()
            .BuildServiceProvider();

        TimeProvider timeProvider = serviceProvider.GetRequiredService<TimeProvider>();

        Assert.Same(TimeProvider.System, timeProvider);
        Assert.Equal(TimeSpan.Zero, timeProvider.GetUtcNow().Offset);
    }
}
