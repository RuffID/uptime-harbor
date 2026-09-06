using UptimeHarbor.Application;
using UptimeHarbor.Infrastructure;
using UptimeHarbor.Web.Configuration;
using UptimeHarbor.Web.Hubs;
using UptimeHarbor.Web.Pages;
using UptimeHarbor.Web.Security;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Configuration.AddJsonFile("Config/config.json", optional: false, reloadOnChange: false);

builder.Host.UseSerilog((_, loggerConfiguration) =>
    loggerConfiguration.ReadFrom.Configuration(builder.Configuration));

if (builder.Environment.IsDevelopment())
{
    builder.Host.UseDefaultServiceProvider(options =>
    {
        options.ValidateScopes = true;
        options.ValidateOnBuild = true;
    });
}

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
app.UseRouting();
app.UseSecurityHeaders();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets();
app.MapControllers();
app.MapRazorPages().WithStaticAssets();

app.MapHub<MonitoringHub>("/hubs/monitoring");

app.Run();
