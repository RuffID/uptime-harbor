using System.Reflection;
using UptimeHarbor.Application.Abstractions.Persistence;
using UptimeHarbor.Application.Dashboard;
using UptimeHarbor.Application.Dashboard.Models;
using UptimeHarbor.Application.Visibility;
using UptimeHarbor.Application.Visibility.Models;
using Xunit;

namespace UptimeHarbor.Tests.Application.Persistence;

public class PersistenceReadContractTests
{
    [Fact]
    public void RepositoryCollectionReads_ReturnIReadOnlyListInsteadOfList()
    {
        Type[] repositoryInterfaces =
        [
            typeof(IMonitoredServerRepository),
            typeof(IMonitoredContainerRepository),
            typeof(IVisibilityPermissionRepository),
            typeof(ITelegramChatRepository),
            typeof(INotificationBindingRepository)
        ];
        MethodInfo[] collectionReads = repositoryInterfaces
            .SelectMany(type => type.GetMethods())
            .Where(method => method.ReturnType.IsGenericType)
            .Where(method => method.ReturnType.GetGenericTypeDefinition() == typeof(Task<>))
            .Where(method => method.ReturnType.GenericTypeArguments[0].IsGenericType)
            .Where(method =>
            {
                Type resultType = method.ReturnType.GenericTypeArguments[0];
                Type genericType = resultType.GetGenericTypeDefinition();
                return genericType == typeof(IReadOnlyList<>) || genericType == typeof(List<>);
            })
            .ToArray();

        Assert.NotEmpty(collectionReads);
        Assert.All(collectionReads, method =>
        {
            Type resultType = method.ReturnType.GenericTypeArguments[0];
            Assert.Equal(typeof(IReadOnlyList<>), resultType.GetGenericTypeDefinition());
        });
    }

    [Fact]
    public void SpecializedReadQueries_ReturnMaterializedReadOnlyModels()
    {
        MethodInfo dashboardMethod = typeof(IDashboardSnapshotReadQuery)
            .GetMethod(nameof(IDashboardSnapshotReadQuery.GetSnapshotAsync))!;
        MethodInfo recipientMethod = typeof(IMonitoringRecipientReadQuery)
            .GetMethod(nameof(IMonitoringRecipientReadQuery.GetCandidatesAsync))!;

        Assert.Equal(
            typeof(Task<DashboardReadSnapshot>),
            dashboardMethod.ReturnType);
        Assert.Equal(
            typeof(Task<IReadOnlyList<MonitoringRecipientReadRecord>>),
            recipientMethod.ReturnType);
        Assert.All(
            typeof(DashboardReadSnapshot).GetProperties(),
            property => Assert.Equal(
                typeof(IReadOnlyList<>),
                property.PropertyType.GetGenericTypeDefinition()));
    }

    [Fact]
    public void ApplicationPublicContracts_DoNotExposeIQueryable()
    {
        Type applicationAssemblyMarker = typeof(IDashboardSnapshotReadQuery);
        IEnumerable<MethodInfo> methods = applicationAssemblyMarker.Assembly
            .GetTypes()
            .Where(type => type.IsPublic)
            .SelectMany(type => type.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly));

        Assert.DoesNotContain(methods, method =>
            ContainsQueryable(method.ReturnType)
            || method.GetParameters().Any(parameter => ContainsQueryable(parameter.ParameterType)));
    }

    private static bool ContainsQueryable(Type type)
    {
        if (type == typeof(IQueryable) || type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IQueryable<>))
        {
            return true;
        }

        return type.IsGenericType && type.GenericTypeArguments.Any(ContainsQueryable);
    }
}
