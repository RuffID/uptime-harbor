using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Security;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Web.Controllers;
using UptimeHarbor.Web.Http;
using UptimeHarbor.Web.Pages;
using UptimeHarbor.Web.Security;
using Xunit;

namespace UptimeHarbor.Tests.Web.Cluster;

public class AdministrationSecurityTests
{
    [Fact]
    public void AcceptedPendingAndConflictUseRealMvcSerialization_WithoutProtectedAccountPayload()
    {
        // Arrange
        Guid actor = Guid.NewGuid();
        ConfigurationValue account = new() { Name = "admin", RoleId = Guid.NewGuid(), PasswordHash = "secret-hash",
            SecurityStamp = "secret-security", ConcurrencyStamp = "secret-concurrency" };
        AcceptedValue accepted = new(actor, DeletedEntityKind.Account, 1, account);
        RevisionImage image = new(Guid.NewGuid(), 1) { Index = 1, Term = 1,
            Values = ImmutableDictionary<Guid, AcceptedValue>.Empty.Add(actor, accepted),
            Versions = ImmutableDictionary<string, AcceptedValue>.Empty.Add(RevisionReducer.VersionKey(actor, 1), accepted) };
        RevisionCommand proposal = new() { ClusterId = image.ClusterId, ClusterGeneration = 1,
            OperationId = Guid.NewGuid(), EntityId = Guid.NewGuid(), EntityKind = DeletedEntityKind.Account,
            OriginNodeId = Guid.NewGuid(), ActorUserId = actor, ActorRevision = 1, BaseRevision = 0,
            Action = "Upsert", AccountAction = "Create", Value = account, CreatedAtUtc = DateTimeOffset.UnixEpoch };
        image = new RevisionReducer().Apply(image, proposal, 2, 1);
        RevisionStateStore state = new(); state.Publish(image);
        User user = new(actor);
        ClusterAdministrationAccess access = new(new AcceptedAdministratorAccessPolicy(user, state), user, state);
        ClusterAdministrationQuery query = new(access, state, user, TimeProvider.System);
        ServiceCollection services = new(); services.AddLogging(); services.AddWebPresentation();
        using ServiceProvider provider = services.BuildServiceProvider();
        JsonSerializerOptions options = provider.GetRequiredService<IOptions<JsonOptions>>().Value.JsonSerializerOptions;

        // Act
        JsonResult result = JsonResultMapper.ToJsonResult(query.Read(Guid.NewGuid()));
        string json = JsonSerializer.Serialize(result.Value, options);

        // Assert
        Assert.Contains("conflicts", json);
        Assert.Contains("firstAccepted", json);
        Assert.Contains("admin", json);
        foreach (string secret in new[] { "passwordHash", "securityStamp", "concurrencyStamp", "secret-", "proposedPayload", "basePayload", "acceptedPayload", "credentials", "protectedPayload" })
        {
            Assert.DoesNotContain(secret, json, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Theory]
    [InlineData(true, true, false)]
    [InlineData(true, false, false)]
    [InlineData(false, false, true)]
    public async Task LegacyReadAndWriteCannotBypassAcceptedModel(bool enabled, bool active, bool expectedCall)
    {
        // Arrange
        Guid actor = Guid.NewGuid();
        RevisionStateStore state = State(actor, active);
        User user = new(actor);
        ClusterAdministrationAccess access = new(new AcceptedAdministratorAccessPolicy(user, state), user, state);
        ClusterLegacyWriterFilter filter = new(Options.Create(new ClusterOptions { Mode = enabled ? "Restart" : "Disabled" }), access);
        ActionContext action = new(new DefaultHttpContext(), new RouteData(), new ControllerActionDescriptor
        {
            MethodInfo = typeof(AdminObjectsController).GetMethod(nameof(AdminObjectsController.GetServersAsync))!
        });
        action.HttpContext.Request.Method = "GET";
        ActionExecutingContext context = new(action, [], new Dictionary<string, object?>(), new object());
        bool called = false;

        // Act
        await filter.OnActionExecutionAsync(context, () => {
            called = true;
            return Task.FromResult(new ActionExecutedContext(action, [], context.Controller));
        });

        // Assert
        Assert.Equal(expectedCall, called);
        if (!expectedCall) Assert.IsType<JsonResult>(context.Result);
    }

    [Fact]
    public async Task SharedUserReadIsAllowedForActiveAcceptedAdministrator()
    {
        // Arrange
        Guid actor = Guid.NewGuid();
        RevisionStateStore state = State(actor, true);
        User user = new(actor);
        var options = Options.Create(new ClusterOptions { Mode = "Restart" });
        ClusterLegacyWriterFilter filter = new(options, new(new AcceptedAdministratorAccessPolicy(user, state), user, state));
        AdminUsersController controller = new(null!, null!, options);
        ActionContext action = new(new DefaultHttpContext(), new RouteData(), new ControllerActionDescriptor
        {
            MethodInfo = typeof(AdminUsersController).GetMethod(nameof(AdminUsersController.GetAsync))!
        });
        action.HttpContext.Request.Method = "GET";
        ActionExecutingContext context = new(action, [], new Dictionary<string, object?>(), controller);
        bool called = false;

        // Act
        await filter.OnActionExecutionAsync(context, () => {
            called = true;
            return Task.FromResult(new ActionExecutedContext(action, [], controller));
        });

        // Assert
        Assert.True(called);
        Assert.Null(context.Result);
    }

    [Fact]
    public void NewMutationRoutesRequireAntiforgery_AndReadControllerRequiresAdministrator()
    {
        // Arrange
        Type type = typeof(ClusterAdministrationController);

        // Act
        string? role = type.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), true)
            .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>().Single().Roles;

        // Assert
        Assert.Equal(ApplicationRoles.ADMINISTRATOR, role);
        foreach (var method in type.GetMethods().Where(method => method.IsDefined(typeof(HttpPostAttribute), true)))
        {
            Assert.True(method.IsDefined(typeof(ValidateAntiForgeryTokenAttribute), true));
        }
    }

    private static RevisionStateStore State(Guid actor, bool enabled)
    {
        RevisionStateStore state = new();
        state.Publish(new(Guid.NewGuid(), 1) { Values = ImmutableDictionary<Guid, AcceptedValue>.Empty.Add(actor,
            new(actor, DeletedEntityKind.Account, 1, new() { Name = "admin", Enabled = enabled, RoleId = Guid.NewGuid(),
                PasswordHash = "hash", SecurityStamp = "stamp", ConcurrencyStamp = "concurrency" })) });
        return state;
    }

    private class User(Guid id) : IUserAccessContext
    {
        public Guid UserId => id;
        public bool IsAuthenticated => true;
        public bool IsEnabled => false;
        public bool IsAdministrator => false;
    }
}
