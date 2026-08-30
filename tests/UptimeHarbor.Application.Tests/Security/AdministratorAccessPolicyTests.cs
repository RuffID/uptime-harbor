using UptimeHarbor.Application.Abstractions.Persistence;
using UptimeHarbor.Application.Abstractions.UnitOfWork;
using UptimeHarbor.Application.Common;
using UptimeHarbor.Application.Monitoring;
using UptimeHarbor.Application.Notifications;
using UptimeHarbor.Application.Notifications.Models;
using UptimeHarbor.Application.ObjectAdministration;
using UptimeHarbor.Application.Security;
using UptimeHarbor.Application.Users;
using UptimeHarbor.Application.Users.Models;
using Xunit;

namespace UptimeHarbor.Tests.Application.Security;

public class AdministratorAccessPolicyTests
{
    [Fact]
    public void GetAccessError_ForUnauthenticatedUser_ReturnsUnauthorized()
    {
        AdministratorAccessPolicy policy = new(new AccessContext(false, false, false));

        ServiceError error = Assert.IsType<ServiceError>(policy.GetAccessError());

        Assert.Equal(ServiceErrorType.Unauthorized, error.Type);
        Assert.Equal("Требуется аутентификация.", error.Message);
    }

    [Fact]
    public void GetAccessError_ForDisabledUser_ReturnsForbidden()
    {
        AdministratorAccessPolicy policy = new(new AccessContext(true, true, false));

        ServiceError error = Assert.IsType<ServiceError>(policy.GetAccessError());

        Assert.Equal(ServiceErrorType.Forbidden, error.Type);
        Assert.Equal("Операция доступна только администратору.", error.Message);
    }

    [Fact]
    public void GetAccessError_ForOrdinaryUser_ReturnsForbidden()
    {
        AdministratorAccessPolicy policy = new(new AccessContext(true, false, true));

        ServiceError error = Assert.IsType<ServiceError>(policy.GetAccessError());

        Assert.Equal(ServiceErrorType.Forbidden, error.Type);
        Assert.Equal("Операция доступна только администратору.", error.Message);
    }

    [Fact]
    public void GetAccessError_ForAdministrator_ReturnsNoError()
    {
        AdministratorAccessPolicy policy = new(new AccessContext(true, true, true));

        Assert.Null(policy.GetAccessError());
    }

    [Theory]
    [InlineData(false, false, false, ServiceErrorType.Unauthorized, "Требуется аутентификация.")]
    [InlineData(true, true, false, ServiceErrorType.Forbidden, "Операция доступна только администратору.")]
    [InlineData(true, false, true, ServiceErrorType.Forbidden, "Операция доступна только администратору.")]
    public async Task ProtectedServices_UseSamePolicyBeforeDependencies(
        bool isAuthenticated,
        bool isAdministrator,
        bool isEnabled,
        ServiceErrorType expectedType,
        string expectedMessage)
    {
        AccessContext accessContext = new(isAuthenticated, isAdministrator, isEnabled);
        AdministratorAccessPolicy policy = new(accessContext);
        UserManagementService users = new(
            accessContext,
            policy,
            new ThrowingUserAccountStore(),
            new ThrowingVisibilityUnitOfWork());
        ObjectAdministrationService objects = new(
            policy,
            new ThrowingObjectAdministrationUnitOfWork(),
            new ThrowingChangePublisher());
        NotificationAdministrationService notifications = new(
            policy,
            new ThrowingNotificationSettingsUnitOfWork(),
            new ThrowingNotificationSender());

        ServiceError[] errors =
        [
            (await users.CreateUserAsync(
                "user",
                "password",
                TestContext.Current.CancellationToken)).Error!,
            (await objects.CreateServerAsync(
                "https://server.example/health",
                null,
                true,
                TestContext.Current.CancellationToken)).Error!,
            (await notifications.SendTestMessageAsync(
                Guid.NewGuid(),
                TestContext.Current.CancellationToken)).Error!
        ];

        Assert.All(errors, error =>
        {
            Assert.Equal(expectedType, error.Type);
            Assert.Equal(expectedMessage, error.Message);
        });
    }

    private class AccessContext(
        bool isAuthenticated,
        bool isAdministrator,
        bool isEnabled) : IUserAccessContext
    {
        public bool IsAuthenticated { get; } = isAuthenticated;
        public bool IsAdministrator { get; } = isAdministrator;
        public bool IsEnabled { get; } = isEnabled;
        public Guid UserId { get; } = Guid.NewGuid();
    }

    private class ThrowingUserAccountStore : IUserAccountStore
    {
        public Task<IReadOnlyList<UserAccountRecord>> GetAllAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Account store must not be called.");
        public Task<UserAccountRecord?> GetByIdAsync(Guid userId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Account store must not be called.");
        public Task<bool> HasOtherEnabledAdministratorAsync(Guid excludedUserId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Account store must not be called.");
        public Task<AccountStoreResult> CreateAsync(Guid userId, string login, string initialPassword, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Account store must not be called.");
        public Task<AccountStoreResult> SetEnabledAsync(Guid userId, bool isEnabled, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Account store must not be called.");
        public Task<AccountStoreResult> DeleteAsync(Guid userId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Account store must not be called.");
    }

    private class ThrowingVisibilityUnitOfWork : IVisibilitySettingsUnitOfWork
    {
        public IMonitoredServerRepository MonitoredServers =>
            throw new InvalidOperationException("Repository must not be called.");
        public IMonitoredContainerRepository MonitoredContainers =>
            throw new InvalidOperationException("Repository must not be called.");
        public IVisibilityPermissionRepository VisibilityPermissions =>
            throw new InvalidOperationException("Repository must not be called.");
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("SaveChanges must not be called.");
        public Task ExecuteInTransactionAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Unit of work must not be called.");
    }

    private class ThrowingObjectAdministrationUnitOfWork : IObjectAdministrationUnitOfWork
    {
        public IMonitoredServerRepository MonitoredServers =>
            throw new InvalidOperationException("Repository must not be called.");
        public IMonitoredContainerRepository MonitoredContainers =>
            throw new InvalidOperationException("Repository must not be called.");
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("SaveChanges must not be called.");
        public Task ExecuteInTransactionAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Unit of work must not be called.");
    }

    private class ThrowingNotificationSettingsUnitOfWork : INotificationSettingsUnitOfWork
    {
        public IMonitoredServerRepository MonitoredServers =>
            throw new InvalidOperationException("Repository must not be called.");
        public IMonitoredContainerRepository MonitoredContainers =>
            throw new InvalidOperationException("Repository must not be called.");
        public ITelegramChatRepository TelegramChats =>
            throw new InvalidOperationException("Repository must not be called.");
        public INotificationBindingRepository NotificationBindings =>
            throw new InvalidOperationException("Repository must not be called.");
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("SaveChanges must not be called.");
        public Task ExecuteInTransactionAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Unit of work must not be called.");
    }

    private class ThrowingNotificationSender : INotificationSender
    {
        public Task SendAsync(
            NotificationRecipient recipient,
            string message,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Telegram sender must not be called.");
    }

    private class ThrowingChangePublisher : IObjectAdministrationChangePublisher
    {
        public Task PublishObjectsChangedAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Publisher must not be called.");
    }
}
