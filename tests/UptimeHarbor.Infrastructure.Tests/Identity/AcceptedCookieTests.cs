using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure;
using UptimeHarbor.Infrastructure.Identity;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Identity;

public class AcceptedCookieTests
{
    [Fact]
    public async Task RegisteredCookieValidatorUsesAcceptedCredentialsAndNodeLocalProtectionDiscriminator()
    {
        // Arrange
        Guid nodeId = Guid.NewGuid();
        string path = Path.GetTempPath();
        IConfiguration configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:SqlServer"] = "Server=unused;Database=unused;Integrated Security=true",
            ["Cluster:Mode"] = "Restart", ["Cluster:ClusterId"] = Guid.NewGuid().ToString(),
            ["Cluster:Generation"] = "1", ["Cluster:NodeId"] = nodeId.ToString(),
            ["Cluster:StorageGeneration"] = Guid.NewGuid().ToString(), ["Cluster:CredentialId"] = Guid.NewGuid().ToString(),
            ["Cluster:PublicUri"] = "https://peer.example/internal/raft", ["Cluster:StoragePath"] = path,
            ["Cluster:ClientCertificatePath"] = Path.Combine(path, "unused-cert.pem"),
            ["Cluster:ClientKeyPath"] = Path.Combine(path, "unused-key.pem"),
            ["Cluster:ClientCaPath"] = Path.Combine(path, "unused-ca.pem"),
            ["Cluster:ClientCaFingerprint"] = new string('A', 64), ["Cluster:ExpectedBotId"] = "1"
        }).Build();
        using AcceptedIdentityFixture identity = new();
        SharedAccount account = await identity.CreateAsync();
        ServiceCollection services = new();
        services.AddLogging();
        services.AddInfrastructure(configuration);
        services.AddSingleton(identity.State);
        services.AddSingleton(identity.Sessions);
        services.AddSingleton<IAuthenticationService>(identity.Authentication);
        using ServiceProvider provider = services.BuildServiceProvider();
        using IServiceScope scope = provider.CreateScope();
        CookieAuthenticationOptions options = provider.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentityConstants.ApplicationScheme);
        DefaultHttpContext http = new() { RequestServices = scope.ServiceProvider };
        AuthenticationScheme scheme = new(IdentityConstants.ApplicationScheme, null, typeof(CookieAuthenticationHandler));
        AuthenticationTicket ticket = new(identity.Sessions.Create(account, scheme.Name), scheme.Name);
        CookieValidatePrincipalContext valid = new(http, scheme, options, ticket);

        // Act
        await options.Events.OnValidatePrincipal(valid);
        identity.Publish(new SharedAccount(account.UserId, account.RoleId, "admin", account.Credential, false, 2));
        CookieValidatePrincipalContext disabled = new(http, scheme, options, ticket);
        await options.Events.OnValidatePrincipal(disabled);

        // Assert
        Assert.NotNull(valid.Principal);
        Assert.Null(disabled.Principal);
        Assert.Equal($"UptimeHarbor/{nodeId:N}", provider.GetRequiredService<IOptions<DataProtectionOptions>>().Value.ApplicationDiscriminator);
        Assert.IsType<AcceptedUserSignInService>(scope.ServiceProvider.GetRequiredService<IUserSignInService>());
    }
}
