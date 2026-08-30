using UptimeHarbor.Application;
using UptimeHarbor.Infrastructure;
using UptimeHarbor.Web.Configuration;
using UptimeHarbor.Web.Hubs;
using UptimeHarbor.Web.Pages;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

if (builder.Environment.IsDevelopment())
{
    builder.Host.UseDefaultServiceProvider(options =>
    {
        options.ValidateScopes = true;
        options.ValidateOnBuild = true;
    });
}

builder.Logging.AddUptimeHarborLogging();

builder.Services
    .AddApplication()
    .AddApplicationConfiguration(builder.Configuration)
    .AddInfrastructure(builder.Configuration)
    .AddWebPresentation();

var app = builder.Build();

await app.Services.InitializeInfrastructureAsync();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseForwardedHeaders();
app.UseHttpsRedirection();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets();
app.MapControllers();
app.MapRazorPages()
   .WithStaticAssets();
app.MapHub<MonitoringHub>("/hubs/monitoring");
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("live")
});
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready")
});
app.MapHealthChecks("/health", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready")
});

app.Run();
