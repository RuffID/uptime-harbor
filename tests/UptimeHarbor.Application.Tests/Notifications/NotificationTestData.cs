using UptimeHarbor.Application.Abstractions.Persistence;
using UptimeHarbor.Application.Abstractions.UnitOfWork;
using UptimeHarbor.Application.Notifications;
using UptimeHarbor.Application.Notifications.Models;
using UptimeHarbor.Application.Security;
using UptimeHarbor.Domain.Monitoring;
using UptimeHarbor.Domain.Notifications;

namespace UptimeHarbor.Tests.Application.Notifications;

internal class NotificationTestData
{
    public NotificationTestData(bool isAdministrator = true, DateTimeOffset? nowUtc = null)
    {
        Servers = new ServerRepository();
        Containers = new ContainerRepository();
        Chats = new ChatRepository();
        Bindings = new BindingRepository();
        UnitOfWork = new FakeUnitOfWork(Servers, Containers, Chats, Bindings);
        Sender = new FakeSender();
        TimeProvider = new FixedTimeProvider(
            nowUtc ?? new DateTimeOffset(2026, 8, 25, 12, 0, 0, TimeSpan.Zero));
        DeliveryService = new NotificationDeliveryService(UnitOfWork, TimeProvider);
        AdministrationService = new NotificationAdministrationService(
            new AccessContext(isAdministrator),
            UnitOfWork,
            Sender);
    }

    public ServerRepository Servers { get; }

    public ContainerRepository Containers { get; }

    public ChatRepository Chats { get; }

    public BindingRepository Bindings { get; }

    public FakeUnitOfWork UnitOfWork { get; }

    public FakeSender Sender { get; }

    public FixedTimeProvider TimeProvider { get; }

    public NotificationDeliveryService DeliveryService { get; }

    public NotificationAdministrationService AdministrationService { get; }

    public MonitoredServer AddServer(string? name = null)
    {
        MonitoredServer server = new(
            Guid.NewGuid(),
            $"https://server-{Servers.Items.Count}.example/health",
            "Описание сервера",
            true);
        if (name is not null)
        {
            server.RecordSuccessfulCheck(name, TimeProvider.GetUtcNow().AddMinutes(-1));
        }
        Servers.Items.Add(server);
        return server;
    }

    public MonitoredContainer AddContainer(MonitoredServer server, string? name = null)
    {
        MonitoredContainer container = new(
            Guid.NewGuid(),
            server.Id,
            $"https://container-{Containers.Items.Count}.example/health",
            "Описание контейнера",
            true);
        if (name is not null)
        {
            container.RecordSuccessfulCheck(
                name,
                TimeProvider.GetUtcNow().AddHours(-2),
                TimeProvider.GetUtcNow().AddMinutes(-1));
        }
        Containers.Items.Add(container);
        return container;
    }

    public TelegramChat AddChat(long telegramChatId, string name)
    {
        TelegramChat chat = new(Guid.NewGuid(), telegramChatId, name);
        Chats.Items.Add(chat);
        return chat;
    }

    internal class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    internal class AccessContext(bool isAdministrator) : IUserAccessContext
    {
        public bool IsAuthenticated => true;
        public bool IsAdministrator { get; } = isAdministrator;
        public bool IsEnabled => true;
        public Guid UserId { get; } = Guid.NewGuid();
    }

    internal class FakeSender : INotificationSender
    {
        public List<(NotificationRecipient Recipient, string Message)> Sent { get; } = [];
        public HashSet<Guid> FailedRecipientIds { get; } = [];

        public Task SendAsync(
            NotificationRecipient recipient,
            string message,
            CancellationToken cancellationToken = default)
        {
            if (FailedRecipientIds.Contains(recipient.Id))
            {
                throw new NotificationDeliveryException("test failure");
            }
            Sent.Add((recipient, message));
            return Task.CompletedTask;
        }
    }

    internal class FakeUnitOfWork(
        ServerRepository servers,
        ContainerRepository containers,
        ChatRepository chats,
        BindingRepository bindings)
        : INotificationSettingsUnitOfWork
    {
        public IMonitoredServerRepository MonitoredServers { get; } = servers;
        public IMonitoredContainerRepository MonitoredContainers { get; } = containers;
        public ITelegramChatRepository TelegramChats { get; } = chats;
        public INotificationBindingRepository NotificationBindings { get; } = bindings;
        public int SaveCount { get; private set; }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            SaveCount++;
            return Task.FromResult(1);
        }

        public async Task ExecuteInTransactionAsync(
            Func<CancellationToken, Task> action,
            CancellationToken cancellationToken = default)
        {
            await action(cancellationToken);
            await SaveChangesAsync(cancellationToken);
        }
    }

    internal class ServerRepository : IMonitoredServerRepository
    {
        public List<MonitoredServer> Items { get; } = [];
        public Task<List<MonitoredServer>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult(Items.ToList());
        public Task<List<MonitoredServer>> GetEnabledAsync(CancellationToken cancellationToken = default) => Task.FromResult(Items.Where(item => item.IsMonitoringEnabled).ToList());
        public Task<MonitoredServer?> GetItemByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(Items.SingleOrDefault(item => item.Id == id));
        public void Create(MonitoredServer item) => Items.Add(item);
        public void CreateRange(IEnumerable<MonitoredServer> entities) => Items.AddRange(entities);
        public void Delete(MonitoredServer item) => Items.Remove(item);
        public void DeleteRange(IEnumerable<MonitoredServer> entities) => Items.RemoveAll(entities.Contains);
    }

    internal class ContainerRepository : IMonitoredContainerRepository
    {
        public List<MonitoredContainer> Items { get; } = [];
        public Task<List<MonitoredContainer>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult(Items.ToList());
        public Task<List<MonitoredContainer>> GetEnabledAsync(CancellationToken cancellationToken = default) => Task.FromResult(Items.Where(item => item.IsMonitoringEnabled).ToList());
        public Task<List<MonitoredContainer>> GetByServerIdAsync(Guid serverId, CancellationToken cancellationToken = default) => Task.FromResult(Items.Where(item => item.ServerId == serverId).ToList());
        public Task<MonitoredContainer?> GetItemByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(Items.SingleOrDefault(item => item.Id == id));
        public void Create(MonitoredContainer item) => Items.Add(item);
        public void CreateRange(IEnumerable<MonitoredContainer> entities) => Items.AddRange(entities);
        public void Delete(MonitoredContainer item) => Items.Remove(item);
        public void DeleteRange(IEnumerable<MonitoredContainer> entities) => Items.RemoveAll(entities.Contains);
    }

    internal class ChatRepository : ITelegramChatRepository
    {
        public List<TelegramChat> Items { get; } = [];
        public Task<List<TelegramChat>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult(Items.ToList());
        public Task<TelegramChat?> GetItemByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(Items.SingleOrDefault(item => item.Id == id));
        public Task<TelegramChat?> GetByTelegramChatIdAsync(long telegramChatId, CancellationToken cancellationToken = default) => Task.FromResult(Items.SingleOrDefault(item => item.TelegramChatId == telegramChatId));
        public void Create(TelegramChat item) => Items.Add(item);
        public void CreateRange(IEnumerable<TelegramChat> entities) => Items.AddRange(entities);
        public void Delete(TelegramChat item) => Items.Remove(item);
        public void DeleteRange(IEnumerable<TelegramChat> entities) => Items.RemoveAll(entities.Contains);
    }

    internal class BindingRepository : INotificationBindingRepository
    {
        public List<NotificationBinding> Items { get; } = [];
        public Task<List<NotificationBinding>> GetAllAsync(CancellationToken cancellationToken = default) => Task.FromResult(Items.ToList());
        public Task<NotificationBinding?> GetItemByIdAsync(Guid id, CancellationToken cancellationToken = default) => Task.FromResult(Items.SingleOrDefault(item => item.Id == id));
        public Task<List<NotificationBinding>> GetByChatIdAsync(Guid telegramChatId, CancellationToken cancellationToken = default) => Task.FromResult(Items.Where(item => item.TelegramChatId == telegramChatId).ToList());
        public Task<List<NotificationBinding>> GetByServerIdAsync(Guid serverId, CancellationToken cancellationToken = default) => Task.FromResult(Items.Where(item => item.ServerId == serverId).ToList());
        public Task<List<NotificationBinding>> GetByContainerIdAsync(Guid containerId, CancellationToken cancellationToken = default) => Task.FromResult(Items.Where(item => item.ContainerId == containerId).ToList());
        public void Create(NotificationBinding item) => Items.Add(item);
        public void CreateRange(IEnumerable<NotificationBinding> entities) => Items.AddRange(entities);
        public void Delete(NotificationBinding item) => Items.Remove(item);
        public void DeleteRange(IEnumerable<NotificationBinding> entities) => Items.RemoveAll(entities.Contains);
    }
}
