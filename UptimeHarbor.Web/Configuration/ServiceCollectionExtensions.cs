using UptimeHarbor.Application.Monitoring;
using UptimeHarbor.Infrastructure.Telegram;
using Microsoft.AspNetCore.HttpOverrides;
using System.Net;
using System.Net.Sockets;

namespace UptimeHarbor.Web.Configuration;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddApplicationConfiguration(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services
            .AddOptions<MonitoringOptions>()
            .Bind(configuration.GetSection(MonitoringOptions.SECTION_NAME))
            .Validate(
                options => options.IntervalSeconds > 0,
                $"{MonitoringOptions.SECTION_NAME}:IntervalSeconds must be greater than zero.")
            .Validate(
                options => options.TimeoutSeconds > 0,
                $"{MonitoringOptions.SECTION_NAME}:TimeoutSeconds must be greater than zero.")
            .Validate(
                options => options.FailuresBeforeUnavailable > 0,
                $"{MonitoringOptions.SECTION_NAME}:FailuresBeforeUnavailable must be greater than zero.")
            .ValidateOnStart();

        services
            .AddOptions<TelegramOptions>()
            .Bind(configuration.GetSection(TelegramOptions.SECTION_NAME))
            .Validate(
                options => !string.IsNullOrWhiteSpace(options.BotToken),
                $"{TelegramOptions.SECTION_NAME}:BotToken is required.")
            .ValidateOnStart();

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor
                | ForwardedHeaders.XForwardedProto
                | ForwardedHeaders.XForwardedHost;
            options.ForwardLimit = 1;
            string[] networks = configuration
                .GetSection("ForwardedHeaders:KnownNetworks")
                .Get<string[]>()
                ?? throw new InvalidOperationException(
                    "ForwardedHeaders:KnownNetworks must contain the proxy network.");
            foreach (string network in networks)
            {
                string[] parts = network.Split('/', 2, StringSplitOptions.TrimEntries);
                if (parts.Length != 2
                    || !IPAddress.TryParse(parts[0], out IPAddress? address)
                    || !int.TryParse(parts[1], out int prefixLength)
                    || address.AddressFamily != AddressFamily.InterNetwork
                    || prefixLength is < 0 or > 32)
                {
                    throw new InvalidOperationException(
                        $"ForwardedHeaders:KnownNetworks contains invalid CIDR '{network}'.");
                }

                options.KnownIPNetworks.Add(new System.Net.IPNetwork(address, prefixLength));
            }
        });

        return services;
    }
}
