using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using UptimeHarbor.Application.Common;
using UptimeHarbor.Application.Monitoring;
using UptimeHarbor.Application.Monitoring.Models;
using UptimeHarbor.Application.Notifications;
using UptimeHarbor.Application.Notifications.Models;
using UptimeHarbor.Domain.Monitoring;
using UptimeHarbor.Infrastructure.Monitoring;
using UptimeHarbor.Infrastructure.Persistence;
using UptimeHarbor.Infrastructure.Telegram;
using Xunit;

namespace UptimeHarbor.Tests.Infrastructure.Telegram;

public class TelegramOutboxDispatcherTests
{
    private static readonly DateTimeOffset NOW_UTC =
        new(2026, 8, 30, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Job_RemainsDurableUntilDelivery()
    {
        await using SqliteConnection connection = await OpenDatabaseAsync();
        await AddJobAsync(connection);

        await using UptimeHarborDbContext restartedContext = CreateContext(connection);
        NotificationOutboxJob job = await restartedContext.NotificationOutboxJobs
            .AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);

        Assert.Null(job.CompletedAtUtc);
        Assert.Null(job.FailedAtUtc);
        Assert.Equal(0, job.AttemptCount);
    }

    [Fact]
    public async Task RestartBetweenEnqueueAndDispatch_DoesNotLoseNotification()
    {
        await using SqliteConnection connection = await OpenDatabaseAsync();
        await AddJobAsync(connection);
        FakeDeliveryService deliveryService = new([CreateDelivery(Guid.NewGuid())]);
        RecordingSender sender = new();
        MutableTimeProvider timeProvider = new(NOW_UTC);
        using ServiceProvider restartedProvider = CreateProvider(
            connection,
            deliveryService,
            sender,
            timeProvider);
        TelegramOutboxDispatcher dispatcher = CreateDispatcher(restartedProvider, timeProvider);

        int dispatched = await dispatcher.DispatchDueAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, dispatched);
        Assert.Single(sender.AttemptedRecipientIds);
        Assert.Single(deliveryService.MarkedBindingIds);
        await using UptimeHarborDbContext verificationContext = CreateContext(connection);
        Assert.NotNull((await verificationContext.NotificationOutboxJobs.SingleAsync(
            TestContext.Current.CancellationToken)).CompletedAtUtc);
    }

    [Fact]
    public async Task OneChatFailure_DoesNotPreventOtherChatAttemptAndSuccessMarker()
    {
        await using SqliteConnection connection = await OpenDatabaseAsync();
        await AddJobAsync(connection);
        Guid failedChatId = Guid.NewGuid();
        Guid successfulChatId = Guid.NewGuid();
        FakeDeliveryService deliveryService = new(
        [
            CreateDelivery(failedChatId),
            CreateDelivery(successfulChatId)
        ]);
        RecordingSender sender = new(failedChatId);
        MutableTimeProvider timeProvider = new(NOW_UTC);
        using ServiceProvider provider = CreateProvider(connection, deliveryService, sender, timeProvider);
        TelegramOutboxDispatcher dispatcher = CreateDispatcher(provider, timeProvider);

        await dispatcher.DispatchDueAsync(TestContext.Current.CancellationToken);

        Assert.Contains(failedChatId, sender.AttemptedRecipientIds);
        Assert.Contains(successfulChatId, sender.AttemptedRecipientIds);
        Assert.Single(deliveryService.MarkedBindingIds);
    }

    [Fact]
    public async Task HangingChat_DoesNotDelayAnotherChatDelivery()
    {
        await using SqliteConnection connection = await OpenDatabaseAsync();
        await AddJobAsync(connection);
        Guid hangingChatId = Guid.NewGuid();
        Guid successfulChatId = Guid.NewGuid();
        FakeDeliveryService deliveryService = new(
        [
            CreateDelivery(hangingChatId),
            CreateDelivery(successfulChatId)
        ]);
        SelectivelyHangingSender sender = new(hangingChatId, successfulChatId);
        MutableTimeProvider timeProvider = new(NOW_UTC);
        using ServiceProvider provider = CreateProvider(connection, deliveryService, sender, timeProvider);
        TelegramOutboxDispatcher dispatcher = CreateDispatcher(provider, timeProvider);

        Task dispatch = dispatcher.DispatchDueAsync(TestContext.Current.CancellationToken);
        await sender.HangingStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        await sender.SuccessfulCompleted.Task.WaitAsync(TestContext.Current.CancellationToken);

        sender.ReleaseHanging.TrySetResult();
        await dispatch;
        Assert.Contains(successfulChatId, sender.AttemptedRecipientIds);
    }

    [Fact]
    public async Task Retry_StopsAfterConfiguredMaximumAttempts()
    {
        await using SqliteConnection connection = await OpenDatabaseAsync();
        await AddJobAsync(connection);
        FakeDeliveryService deliveryService = new([CreateDelivery(Guid.NewGuid())]);
        RecordingSender sender = new(failAll: true);
        MutableTimeProvider timeProvider = new(NOW_UTC);
        using ServiceProvider provider = CreateProvider(connection, deliveryService, sender, timeProvider);
        TelegramOutboxDispatcher dispatcher = CreateDispatcher(provider, timeProvider);

        for (int attempt = 0; attempt < TelegramOutboxDispatcher.MAXIMUM_ATTEMPTS; attempt++)
        {
            await dispatcher.DispatchDueAsync(TestContext.Current.CancellationToken);
            timeProvider.Advance(TimeSpan.FromMinutes(10));
        }

        int dispatchedAfterLimit = await dispatcher.DispatchDueAsync(
            TestContext.Current.CancellationToken);
        await using UptimeHarborDbContext verificationContext = CreateContext(connection);
        NotificationOutboxJob job = await verificationContext.NotificationOutboxJobs.SingleAsync(
            TestContext.Current.CancellationToken);
        Assert.Equal(TelegramOutboxDispatcher.MAXIMUM_ATTEMPTS, job.AttemptCount);
        Assert.NotNull(job.FailedAtUtc);
        Assert.Equal(0, dispatchedAfterLimit);
    }

    [Fact]
    public async Task HangingTelegramDelivery_DoesNotBlockNextMonitoringCycle()
    {
        await using SqliteConnection connection = await OpenDatabaseAsync();
        await AddJobAsync(connection);
        FakeDeliveryService deliveryService = new([CreateDelivery(Guid.NewGuid())]);
        HangingSender sender = new();
        MutableTimeProvider timeProvider = new(NOW_UTC);
        using ServiceProvider outboxProvider = CreateProvider(
            connection,
            deliveryService,
            sender,
            timeProvider);
        TelegramOutboxDispatcher dispatcher = CreateDispatcher(outboxProvider, timeProvider);
        Task dispatch = dispatcher.DispatchDueAsync(TestContext.Current.CancellationToken);
        await sender.Started.Task.WaitAsync(TestContext.Current.CancellationToken);

        CountingMonitoringService monitoringService = new();
        ServiceCollection monitoringServices = new();
        monitoringServices.AddSingleton<IMonitoringService>(monitoringService);
        monitoringServices.AddSingleton<IServerEndpointChecker>(new SuccessfulServerChecker());
        monitoringServices.AddSingleton<IContainerEndpointChecker>(new SuccessfulContainerChecker());
        using ServiceProvider monitoringProvider = monitoringServices.BuildServiceProvider();
        MonitoringCycleRunner runner = new(
            monitoringProvider.GetRequiredService<IServiceScopeFactory>(),
            Microsoft.Extensions.Options.Options.Create(new MonitoringOptions
            {
                IntervalSeconds = 30,
                TimeoutSeconds = 30,
                FailuresBeforeUnavailable = 3
            }),
            NullLogger<MonitoringCycleRunner>.Instance);

        await runner.RunCycleAsync(TestContext.Current.CancellationToken);
        await runner.RunCycleAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, monitoringService.RecordCount);
        sender.Release.TrySetResult();
        await dispatch;
    }

    private static async Task<SqliteConnection> OpenDatabaseAsync()
    {
        SqliteConnection connection = new("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using UptimeHarborDbContext context = CreateContext(connection);
        await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        return connection;
    }

    private static UptimeHarborDbContext CreateContext(SqliteConnection connection)
    {
        DbContextOptions<UptimeHarborDbContext> options =
            new DbContextOptionsBuilder<UptimeHarborDbContext>()
                .UseSqlite(connection)
                .Options;
        return new UptimeHarborDbContext(options);
    }

    private static async Task AddJobAsync(SqliteConnection connection)
    {
        await using UptimeHarborDbContext context = CreateContext(connection);
        context.NotificationOutboxJobs.Add(NotificationOutboxJob.Create(CreateEvent()));
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static ServiceProvider CreateProvider(
        SqliteConnection connection,
        INotificationDeliveryService deliveryService,
        INotificationSender sender,
        TimeProvider timeProvider)
    {
        ServiceCollection services = new();
        services.AddDbContext<UptimeHarborDbContext>(options => options.UseSqlite(connection));
        services.AddScoped<NotificationOutboxStore>();
        services.AddSingleton(deliveryService);
        services.AddSingleton(sender);
        services.AddSingleton(timeProvider);
        return services.BuildServiceProvider();
    }

    private static TelegramOutboxDispatcher CreateDispatcher(
        ServiceProvider provider,
        TimeProvider timeProvider)
    {
        return new TelegramOutboxDispatcher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            timeProvider,
            NullLogger<TelegramOutboxDispatcher>.Instance);
    }

    private static MonitoringStateChangedEvent CreateEvent()
    {
        return new MonitoringStateChangedEvent(
            Guid.NewGuid(),
            MonitoringObjectKind.Server,
            null,
            "server",
            null,
            MonitoringStatus.Unavailable,
            Guid.NewGuid(),
            3,
            NOW_UTC,
            null,
            MonitoringTransition.BecameUnavailable,
            false,
            EndpointCheckFailureKind.HttpStatus);
    }

    private static PendingNotificationDeliveryDto CreateDelivery(Guid chatId)
    {
        return new PendingNotificationDeliveryDto(
            Guid.NewGuid(),
            Guid.NewGuid(),
            NotificationDeliveryKind.Outage,
            new NotificationRecipient(chatId, "-100123"),
            "message");
    }

    private class FakeDeliveryService(IReadOnlyList<PendingNotificationDeliveryDto> deliveries)
        : INotificationDeliveryService
    {
        public List<Guid> MarkedBindingIds { get; } = [];

        public Task<IReadOnlyList<PendingNotificationDeliveryDto>> GetPendingDeliveriesAsync(
            MonitoringStateChangedEvent monitoringEvent,
            CancellationToken cancellationToken = default) => Task.FromResult(deliveries);

        public Task<ServiceResult> MarkDeliverySuccessfulAsync(
            Guid bindingId,
            Guid incidentId,
            NotificationDeliveryKind kind,
            CancellationToken cancellationToken = default)
        {
            lock (MarkedBindingIds)
            {
                MarkedBindingIds.Add(bindingId);
            }

            return Task.FromResult(ServiceResult.Ok());
        }
    }

    private class RecordingSender : INotificationSender
    {
        private readonly Guid? failedChatId;
        private readonly bool failAll;

        public RecordingSender(Guid failedChatId)
        {
            this.failedChatId = failedChatId;
        }

        public RecordingSender(bool failAll = false)
        {
            this.failAll = failAll;
        }

        public List<Guid> AttemptedRecipientIds { get; } = [];

        public Task SendAsync(
            NotificationRecipient recipient,
            string message,
            CancellationToken cancellationToken = default)
        {
            lock (AttemptedRecipientIds)
            {
                AttemptedRecipientIds.Add(recipient.Id);
            }

            if (failAll || recipient.Id == failedChatId)
            {
                throw new NotificationDeliveryException("failure");
            }

            return Task.CompletedTask;
        }
    }

    private class HangingSender : INotificationSender
    {
        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task SendAsync(
            NotificationRecipient recipient,
            string message,
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
        }
    }

    private class SelectivelyHangingSender(Guid hangingChatId, Guid successfulChatId)
        : INotificationSender
    {
        public TaskCompletionSource HangingStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SuccessfulCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseHanging { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<Guid> AttemptedRecipientIds { get; } = [];

        public async Task SendAsync(
            NotificationRecipient recipient,
            string message,
            CancellationToken cancellationToken = default)
        {
            lock (AttemptedRecipientIds)
            {
                AttemptedRecipientIds.Add(recipient.Id);
            }

            if (recipient.Id == hangingChatId)
            {
                HangingStarted.TrySetResult();
                await ReleaseHanging.Task.WaitAsync(cancellationToken);
                return;
            }

            if (recipient.Id == successfulChatId)
            {
                SuccessfulCompleted.TrySetResult();
            }
        }
    }

    private class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;

        public void Advance(TimeSpan duration)
        {
            utcNow = utcNow.Add(duration);
        }
    }

    private class CountingMonitoringService : IMonitoringService
    {
        private readonly MonitoringTargetDto target = new(
            Guid.NewGuid(),
            MonitoringObjectKind.Server,
            "https://server.example/health");

        public int RecordCount { get; private set; }

        public Task<IReadOnlyList<MonitoringTargetDto>> GetEnabledTargetsAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MonitoringTargetDto>>([target]);

        public Task<bool> RecordServerResultAsync(
            Guid serverId,
            string checkedEndpointUrl,
            ServerEndpointCheckResult result,
            int failuresBeforeUnavailable,
            CancellationToken cancellationToken = default)
        {
            RecordCount++;
            return Task.FromResult(true);
        }

        public Task<bool> RecordContainerResultAsync(
            Guid containerId,
            string checkedEndpointUrl,
            ContainerEndpointCheckResult result,
            int failuresBeforeUnavailable,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private class SuccessfulServerChecker : IServerEndpointChecker
    {
        public Task<ServerEndpointCheckResult> CheckAsync(
            string endpointUrl,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(ServerEndpointCheckResult.Succeeded("server", NOW_UTC));
    }

    private class SuccessfulContainerChecker : IContainerEndpointChecker
    {
        public Task<ContainerEndpointCheckResult> CheckAsync(
            string endpointUrl,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
