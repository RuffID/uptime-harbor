using UptimeHarbor.Application;
using UptimeHarbor.Infrastructure;
using UptimeHarbor.Web.Configuration;
using UptimeHarbor.Web.Hubs;
using UptimeHarbor.Web.Pages;

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

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();
app.UseAuthorization();

app.MapStaticAssets();
app.MapControllers();
app.MapRazorPages()
   .WithStaticAssets();
app.MapHub<MonitoringHub>("/hubs/monitoring");
app.MapHealthChecks("/health");

app.Run();
