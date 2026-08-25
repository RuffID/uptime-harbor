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

        return services;
    }
}
