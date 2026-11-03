using Microsoft.Extensions.Options;
using UptimeHarbor.Infrastructure.Installation;

namespace UptimeHarbor.Web.Security;

public class SetupBoundaryMiddleware(RequestDelegate next)
{
    private static readonly HashSet<string> ROUTES = new(StringComparer.Ordinal)
    {
        "/Setup", "/api/setup/session", "/api/setup/login", "/api/setup/logout", "/api/setup/status", "/api/setup/prepare",
        "/dist/pages/setup.js", "/dist/shared/http.js", "/lib/bootstrap/dist/css/bootstrap.min.css"
    };

    public static bool Allowed(PathString path)
    {
        return ROUTES.Contains(path.Value ?? string.Empty);
    }

    public async Task InvokeAsync(HttpContext context, IOptions<InstallationOptions> options,
        InstallationState state, RuntimeWriteGate writes)
    {
        bool ownedRoute = context.Request.Path == "/Setup" || context.Request.Path.StartsWithSegments("/api/setup")
            || context.Request.Path == "/dist/pages/setup.js";
        bool setupRoute = ownedRoute || options.Value.Setup && Allowed(context.Request.Path);
        if (setupRoute)
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!options.Value.Setup || state.CommonOpen)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            string actual = context.Request.Scheme + "://" + context.Request.Host.Value;
            bool mutation = !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method);
            if (actual != options.Value.BrowserOrigin
                || mutation && context.Request.Headers.Origin.ToString() != options.Value.BrowserOrigin)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }

            await next(context);
            return;
        }

        if (options.Value.Setup && !state.CommonOpen)
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            return;
        }

        long generation = state.Current?.RuntimeGeneration ?? 1;
        using IDisposable operation = writes.Enter(generation);
        using UptimeHarbor.Application.Installation.RuntimeGenerationContext generationContext = new(() => writes.Verify(generation));
        await next(context);
    }
}
