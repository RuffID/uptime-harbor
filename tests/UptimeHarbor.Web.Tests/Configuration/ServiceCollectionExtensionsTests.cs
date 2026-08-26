using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Monitoring;
using UptimeHarbor.Infrastructure.Telegram;
using UptimeHarbor.Web.Configuration;
using Xunit;

namespace UptimeHarbor.Tests.Web.Configuration;

public class ServiceCollectionExtensionsTests
{
    [Theory]
    [InlineData("Monitoring:IntervalSeconds", "0", "Monitoring:IntervalSeconds must be greater than zero.")]
    [InlineData("Monitoring:TimeoutSeconds", "0", "Monitoring:TimeoutSeconds must be greater than zero.")]
    [InlineData("Monitoring:FailuresBeforeUnavailable", "0", "Monitoring:FailuresBeforeUnavailable must be greater than zero.")]
    [InlineData("Telegram:BotToken", "", "Telegram:BotToken is required.")]
    public void AddApplicationConfiguration_WhenRequiredValueIsInvalid_ThrowsClearValidationError(
        string invalidKey,
        string invalidValue,
        string expectedError)
    {
        Dictionary<string, string?> settings = CreateValidSettings();
        settings[invalidKey] = invalidValue;
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();
        ServiceCollection services = new();
        services.AddApplicationConfiguration(configuration);

        using ServiceProvider serviceProvider = services.BuildServiceProvider();

        OptionsValidationException exception = Assert.Throws<OptionsValidationException>(
            () => ResolveOptionsForKey(serviceProvider, invalidKey));
        Assert.Contains(expectedError, exception.Failures);
    }

    [Fact]
    public void AddApplicationConfiguration_WhenSettingsAreValid_BindsAllOptions()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(CreateValidSettings())
            .Build();
        ServiceCollection services = new();
        services.AddApplicationConfiguration(configuration);

        using ServiceProvider serviceProvider = services.BuildServiceProvider();

        MonitoringOptions monitoring = serviceProvider
            .GetRequiredService<IOptions<MonitoringOptions>>()
            .Value;
        TelegramOptions telegram = serviceProvider
            .GetRequiredService<IOptions<TelegramOptions>>()
            .Value;

        Assert.Equal(30, monitoring.IntervalSeconds);
        Assert.Equal(30, monitoring.TimeoutSeconds);
        Assert.Equal(3, monitoring.FailuresBeforeUnavailable);
        Assert.Equal("test-token", telegram.BotToken);
    }

    private static Dictionary<string, string?> CreateValidSettings()
    {
        return new Dictionary<string, string?>
        {
            ["BootstrapAdmin:Login"] = "admin",
            ["BootstrapAdmin:Password"] = "test-password",
            ["Monitoring:IntervalSeconds"] = "30",
            ["Monitoring:TimeoutSeconds"] = "30",
            ["Monitoring:FailuresBeforeUnavailable"] = "3",
            ["Telegram:BotToken"] = "test-token"
        };
    }

    private static void ResolveOptionsForKey(IServiceProvider serviceProvider, string key)
    {
        if (key.StartsWith($"{MonitoringOptions.SECTION_NAME}:", StringComparison.Ordinal))
        {
            _ = serviceProvider.GetRequiredService<IOptions<MonitoringOptions>>().Value;
            return;
        }

        _ = serviceProvider.GetRequiredService<IOptions<TelegramOptions>>().Value;
    }
}
