using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Configuration;
using Serilog;
using Xunit;

namespace UptimeHarbor.Tests.Web.Configuration;

public class ConfigurationFilesTests
{
    [Fact]
    public void WebProject_ReferencesRealSerilogPackages()
    {
        XDocument project = XDocument.Load(GetRepositoryPath(
            "UptimeHarbor.Web",
            "UptimeHarbor.Web.csproj"));

        AssertPackageVersion(project, "Serilog.AspNetCore", "10.0.0");
        AssertPackageVersion(project, "Serilog.Settings.Configuration", "10.0.1");
        AssertPackageVersion(project, "Serilog.Sinks.File", "7.0.0");
    }

    [Fact]
    public void Program_LoadsMandatoryLocalConfigurationBeforeDependencyRegistration()
    {
        string program = File.ReadAllText(GetRepositoryPath("UptimeHarbor.Web", "Program.cs"));
        int configurationIndex = program.IndexOf("\"Config/config.json\"", StringComparison.Ordinal);
        int registrationIndex = program.IndexOf("builder.Services", StringComparison.Ordinal);

        Assert.True(configurationIndex >= 0);
        Assert.True(configurationIndex < registrationIndex);
        Assert.Contains("optional: false", program, StringComparison.Ordinal);
        Assert.Contains("reloadOnChange: false", program, StringComparison.Ordinal);
        Assert.DoesNotContain("config.example.json", program, StringComparison.Ordinal);
        Assert.Contains("builder.Host.UseSerilog", program, StringComparison.Ordinal);
        Assert.Contains(
            "ReadFrom.Configuration(builder.Configuration)",
            program,
            StringComparison.Ordinal);
    }

    [Fact]
    public void OldLoggingExtension_IsNotUsedOrPresent()
    {
        string program = File.ReadAllText(GetRepositoryPath("UptimeHarbor.Web", "Program.cs"));

        Assert.DoesNotContain("AddUptimeHarborLogging", program, StringComparison.Ordinal);
        Assert.False(File.Exists(GetRepositoryPath(
            "UptimeHarbor.Web",
            "Configuration",
            "LoggingBuilderExtensions.cs")));
    }

    [Fact]
    public void Program_AcceptsInternalHttpBehindReverseProxyWithoutHttpsRedirect()
    {
        string program = File.ReadAllText(GetRepositoryPath("UptimeHarbor.Web", "Program.cs"));
        int forwardedHeadersIndex = program.IndexOf(
            "app.UseForwardedHeaders()",
            StringComparison.Ordinal);
        int routingIndex = program.IndexOf("app.UseRouting()", StringComparison.Ordinal);

        Assert.True(forwardedHeadersIndex >= 0);
        Assert.True(forwardedHeadersIndex < routingIndex);
        Assert.DoesNotContain("UseHttpsRedirection", program, StringComparison.Ordinal);
    }

    [Fact]
    public void InfrastructureInitialization_AppliesMigrationsBeforeIdentityBootstrap()
    {
        string initialization = File.ReadAllText(GetRepositoryPath(
            "UptimeHarbor.Infrastructure",
            "InfrastructureInitializationExtensions.cs"));
        int migrationIndex = initialization.IndexOf(
            "CheckOrUpdateDatabaseAsync",
            StringComparison.Ordinal);
        int bootstrapIndex = initialization.IndexOf(
            "bootstrapper.InitializeAsync",
            StringComparison.Ordinal);

        Assert.True(migrationIndex >= 0);
        Assert.True(migrationIndex < bootstrapIndex);
        Assert.Contains(
            "DATABASE_STARTUP_TIMEOUT_SECONDS",
            initialization,
            StringComparison.Ordinal);
    }

    [Fact]
    public void AppSettings_ContainsSafeHumanReadableConsoleAndRollingFileConfiguration()
    {
        string settingsPath = GetRepositoryPath("UptimeHarbor.Web", "appsettings.json");
        string settings = File.ReadAllText(settingsPath);
        using JsonDocument document = JsonDocument.Parse(settings);
        JsonElement root = document.RootElement;

        Assert.Equal(["Serilog"], root.EnumerateObject().Select(property => property.Name));

        JsonElement serilog = root.GetProperty("Serilog");
        Assert.Equal(
            "Information",
            serilog.GetProperty("MinimumLevel").GetProperty("Default").GetString());
        Assert.Equal(
            "Warning",
            serilog.GetProperty("MinimumLevel")
                .GetProperty("Override")
                .GetProperty("Microsoft.AspNetCore")
                .GetString());
        Assert.Contains(
            "FromLogContext",
            serilog.GetProperty("Enrich")
                .EnumerateArray()
                .Select(value => value.GetString()));

        JsonElement[] sinks = serilog.GetProperty("WriteTo").EnumerateArray().ToArray();
        Assert.Equal(2, sinks.Length);

        JsonElement consoleSink = Assert.Single(
            sinks,
            sink => string.Equals(
                sink.GetProperty("Name").GetString(),
                "Console",
                StringComparison.Ordinal));
        const string humanReadableOutputTemplate =
            "[{Timestamp:u}] [{Level:u4}][{SourceContext}] - "
                + "{Message:lj} {Properties:j}{NewLine}{Exception}";
        Assert.Equal(
            humanReadableOutputTemplate,
            consoleSink.GetProperty("Args").GetProperty("outputTemplate").GetString());
        Assert.False(consoleSink.GetProperty("Args").TryGetProperty("formatter", out _));

        JsonElement fileSink = Assert.Single(
            sinks,
            sink => string.Equals(
                sink.GetProperty("Name").GetString(),
                "File",
                StringComparison.Ordinal));
        JsonElement fileArguments = fileSink.GetProperty("Args");
        string? logPath = fileArguments.GetProperty("path").GetString();
        Assert.Equal("Logs/log_.log", logPath);
        Assert.False(Path.IsPathRooted(logPath));
        Assert.DoesNotContain("wwwroot", logPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            humanReadableOutputTemplate,
            fileArguments.GetProperty("outputTemplate").GetString());
        Assert.False(fileArguments.TryGetProperty("formatter", out _));
        Assert.Equal("Day", fileArguments.GetProperty("rollingInterval").GetString());
        Assert.Equal(31, fileArguments.GetProperty("retainedFileCountLimit").GetInt32());
        Assert.Equal(10_485_760, fileArguments.GetProperty("fileSizeLimitBytes").GetInt64());
        Assert.True(fileArguments.GetProperty("rollOnFileSizeLimit").GetBoolean());
        Assert.False(fileArguments.GetProperty("shared").GetBoolean());
        Assert.DoesNotContain("ConnectionStrings", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("Password", settings, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("BotToken", settings, StringComparison.Ordinal);
        Assert.DoesNotContain("Telegram", settings, StringComparison.Ordinal);
    }

    [Fact]
    public void LogDirectory_IsExcludedFromArtifactsAndProtectedInContainer()
    {
        XDocument project = XDocument.Load(GetRepositoryPath(
            "UptimeHarbor.Web",
            "UptimeHarbor.Web.csproj"));
        string? defaultItemExcludes = project
            .Descendants("DefaultItemExcludes")
            .Single()
            .Value;
        string dockerfile = File.ReadAllText(GetRepositoryPath(
            "UptimeHarbor.Web",
            "Dockerfile"));
        string[] ignoreRules = File.ReadAllLines(GetRepositoryPath(".gitignore"));

        Assert.Contains("Logs\\**", defaultItemExcludes, StringComparison.Ordinal);
        Assert.Contains("[Ll]ogs/", ignoreRules);
        Assert.Contains("mkdir -p /app/Logs", dockerfile, StringComparison.Ordinal);
        Assert.Contains("chown $APP_UID /app/Logs", dockerfile, StringComparison.Ordinal);
        Assert.Contains("chmod 700 /app/Logs", dockerfile, StringComparison.Ordinal);
        Assert.Contains("VOLUME [\"/app/Logs\"]", dockerfile, StringComparison.Ordinal);
    }

    [Fact]
    public void AppSettings_CreatesSerilogLoggerFromConfiguration()
    {
        string settingsPath = GetRepositoryPath("UptimeHarbor.Web", "appsettings.json");
        using FileStream stream = File.OpenRead(settingsPath);
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddJsonStream(stream)
            .Build();

        using Serilog.Core.Logger logger = new LoggerConfiguration()
            .ReadFrom.Configuration(configuration)
            .CreateLogger();

        Assert.NotNull(logger);
    }

    [Fact]
    public void ConfigurationExample_ContainsCompleteSafeStartupSettings()
    {
        // Arrange
        string example = File.ReadAllText(GetRepositoryPath(
            "UptimeHarbor.Web",
            "Config",
            "config.example.json"));
        using JsonDocument document = JsonDocument.Parse(example);
        JsonElement root = document.RootElement;

        string[] expectedSections =
        [
            "AllowedHosts",
            "BootstrapAdmin",
            "Cluster",
            "ConnectionStrings",
            "ForwardedHeaders",
            "Monitoring",
            "Telegram"
        ];

        // Act
        string[] actualSections = root
            .EnumerateObject()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();

        // Assert
        Assert.Equal(expectedSections, actualSections);
        Assert.Equal("Disabled", root.GetProperty("Cluster").GetProperty("Mode").GetString());

        JsonElement bootstrap = root.GetProperty("BootstrapAdmin");
        Assert.Equal("replace-me", bootstrap.GetProperty("Login").GetString());
        Assert.Equal("replace-me", bootstrap.GetProperty("Password").GetString());

        string? connectionString = root
            .GetProperty("ConnectionStrings")
            .GetProperty("SqlServer")
            .GetString();
        Assert.NotNull(connectionString);
        Assert.Contains("Server=localhost", connectionString, StringComparison.Ordinal);
        Assert.Contains("Password=CHANGE_ME", connectionString, StringComparison.Ordinal);

        JsonElement monitoring = root.GetProperty("Monitoring");
        Assert.True(monitoring.GetProperty("IntervalSeconds").GetInt32() > 0);
        Assert.True(monitoring.GetProperty("TimeoutSeconds").GetInt32() > 0);
        Assert.True(monitoring.GetProperty("FailuresBeforeUnavailable").GetInt32() > 0);

        Assert.Equal(
            "000000000:replace-with-bot-token",
            root.GetProperty("Telegram").GetProperty("BotToken").GetString());
        Assert.Equal(
            ["172.20.0.0/16"],
            root.GetProperty("ForwardedHeaders")
                .GetProperty("KnownNetworks")
                .EnumerateArray()
                .Select(value => value.GetString()));
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("AllowedHosts").GetString()));
        Assert.DoesNotContain("C:\\", example, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/home/", example, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GitIgnore_IgnoresOnlyLocalConfigurationFile()
    {
        string[] rules = File.ReadAllLines(GetRepositoryPath(".gitignore"))
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToArray();

        Assert.Contains("/UptimeHarbor.Web/Config/config.json", rules);
        Assert.DoesNotContain("/UptimeHarbor.Web/Config/", rules);
        Assert.DoesNotContain("/UptimeHarbor.Web/Config/*", rules);
        Assert.DoesNotContain("*.json", rules);
        Assert.DoesNotContain(
            rules,
            rule => rule.Contains("config.example.json", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void WebProject_ExcludesLocalAndExampleConfigurationFromRuntimeArtifacts()
    {
        XDocument project = XDocument.Load(GetRepositoryPath(
            "UptimeHarbor.Web",
            "UptimeHarbor.Web.csproj"));

        AssertConfigurationIsNotCopied(project, "Config/config.json");
        AssertConfigurationIsNotCopied(project, "Config/config.example.json");
    }

    private static void AssertPackageVersion(
        XDocument project,
        string packageName,
        string expectedVersion)
    {
        XElement package = Assert.Single(
            project.Descendants("PackageReference"),
            element => string.Equals(
                element.Attribute("Include")?.Value,
                packageName,
                StringComparison.Ordinal));
        Assert.Equal(expectedVersion, package.Attribute("Version")?.Value);
    }

    private static void AssertConfigurationIsNotCopied(XDocument project, string relativePath)
    {
        XElement content = Assert.Single(
            project.Descendants("Content"),
            element => string.Equals(
                element.Attribute("Update")?.Value,
                relativePath,
                StringComparison.Ordinal));
        Assert.Equal("Never", content.Element("CopyToOutputDirectory")?.Value);
        Assert.Equal("Never", content.Element("CopyToPublishDirectory")?.Value);
    }

    private static string GetRepositoryPath(params string[] segments)
    {
        return Path.GetFullPath(Path.Combine(
            [
                AppContext.BaseDirectory,
                "..", "..", "..", "..", "..",
                .. segments
            ]));
    }
}
