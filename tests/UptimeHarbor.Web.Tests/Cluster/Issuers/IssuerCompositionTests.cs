using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using UptimeHarbor.Application;
using UptimeHarbor.Infrastructure;
using UptimeHarbor.Infrastructure.Cluster.Onboarding;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Issuers;
using UptimeHarbor.Web.Cluster;
using UptimeHarbor.Web.Configuration;
using UptimeHarbor.Web.Pages;
using Xunit;

namespace UptimeHarbor.Tests.Web.Cluster.Issuers;

public class IssuerCompositionTests
{
    [Fact]
    public async Task ManualGraphResolvesIssuerWithoutHostNativeStorageKeyOrNetwork()
    {
        // Arrange
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Testing", ContentRootPath = Path.GetTempPath(), Args = []
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:SqlServer"] = "Server=unused.invalid;Database=unused;Integrated Security=true;Encrypt=true"
        });
        builder.Services.AddApplication().AddApplicationConfiguration(builder.Configuration)
            .AddInfrastructure(builder.Configuration).AddWebPresentation().AddClusterWeb(builder.Configuration);
        await using ServiceProvider provider = builder.Services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true, ValidateScopes = true
        });
        await using AsyncServiceScope scope = provider.CreateAsyncScope();

        // Act
        EnrollmentIssuerAdministration administration = scope.ServiceProvider.GetRequiredService<EnrollmentIssuerAdministration>();
        EnrollmentSigner signer = scope.ServiceProvider.GetRequiredService<EnrollmentSigner>();
        EnrollmentIssuerStorage storage = provider.GetRequiredService<EnrollmentIssuerStorage>();

        // Assert
        Assert.NotNull(administration);
        Assert.NotNull(signer);
        Assert.Throws<InvalidOperationException>(() => storage.Open(create: false));
        Assert.Null(provider.GetRequiredService<ClusterRaftHandle>().Current);
    }

    [Fact]
    public async Task StandaloneInfrastructureKeepsItsGraphClosedWithoutWebDependencies()
    {
        // Arrange
        ServiceCollection services = new();
        services.AddLogging();
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:SqlServer"] = "Server=unused.invalid;Database=unused;Integrated Security=true;Encrypt=true"
        }).Build();
        services.AddInfrastructure(configuration);

        // Act
        await using ServiceProvider provider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true, ValidateScopes = true
        });

        // Assert
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(EnrollmentIssuerAdministration));
        Assert.Null(provider.GetService<UptimeHarbor.Application.Cluster.Onboarding.IEnrollmentIssuerRegistrationBarrier>());
    }
}
