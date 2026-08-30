using Microsoft.Extensions.DependencyInjection;
using UptimeHarbor.Application;
using UptimeHarbor.Application.Security;
using Xunit;

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

    [Fact]
    public void AddApplication_RegistersAdministratorAccessPolicyAsScoped()
    {
        ServiceCollection services = new();

        services.AddApplication();

        ServiceDescriptor descriptor = Assert.Single(
            services,
            item => item.ServiceType == typeof(IAdministratorAccessPolicy));
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
        Assert.Equal(typeof(AdministratorAccessPolicy), descriptor.ImplementationType);
    }
}
