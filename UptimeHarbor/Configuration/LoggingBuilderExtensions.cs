using System.Text.Json;

namespace UptimeHarbor.Configuration;

public static class LoggingBuilderExtensions
{
    public static ILoggingBuilder AddUptimeHarborLogging(this ILoggingBuilder logging)
    {
        ArgumentNullException.ThrowIfNull(logging);

        logging.ClearProviders();
        logging.AddJsonConsole(options =>
        {
            options.IncludeScopes = true;
            options.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
            options.UseUtcTimestamp = true;
            options.JsonWriterOptions = new JsonWriterOptions
            {
                Indented = false
            };
        });

        return logging;
    }
}
