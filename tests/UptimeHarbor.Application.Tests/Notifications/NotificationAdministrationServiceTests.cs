using UptimeHarbor.Application.Abstractions.UnitOfWork;
using UptimeHarbor.Application.Common;
using UptimeHarbor.Application.Monitoring;
using UptimeHarbor.Application.Notifications;
using UptimeHarbor.Application.Notifications.Models;
using UptimeHarbor.Domain.Monitoring;
using UptimeHarbor.Domain.Notifications;
using Xunit;

namespace UptimeHarbor.Tests.Application.Notifications;

public class NotificationAdministrationServiceTests
{
    [Fact]
    public async Task Administrator_CanManageChatsAndBindings()
    {
        NotificationTestData data = new();
        MonitoredServer server = data.AddServer();

        ServiceResult<TelegramChatDto> created = await data.AdministrationService.CreateChatAsync(
            -100123,
            "Операторы",
            TestContext.Current.CancellationToken);
        ServiceResult<NotificationBindingDto> binding = await data.AdministrationService.CreateBindingAsync(
            created.Data!.Id,
            MonitoringObjectKind.Server,
            server.Id,
            TestContext.Current.CancellationToken);
        ServiceResult<TelegramChatDto> updated = await data.AdministrationService.UpdateChatAsync(
            created.Data.Id,
            -100124,
            "Дежурные",
            TestContext.Current.CancellationToken);

        Assert.True(created.Success);
        Assert.True(binding.Success);
        Assert.True(updated.Success);
        Assert.Equal("Дежурные", updated.Data!.Name);
        Assert.Single(data.Bindings.Items);
    }

    [Fact]
    public async Task TestMessage_IsAvailableToAdministrator()
    {
        NotificationTestData data = new();
        TelegramChat chat = data.AddChat(-100123, "Операторы");

        ServiceResult result = await data.AdministrationService.SendTestMessageAsync(
            chat.Id,
            TestContext.Current.CancellationToken);

        Assert.True(result.Success);
        Assert.Single(data.Sender.Sent);
        Assert.Equal(chat.Id, data.Sender.Sent[0].Recipient.Id);
    }

    [Theory]
    [InlineData(NotificationDeliveryFailure.InvalidConfiguration)]
    [InlineData(NotificationDeliveryFailure.ChatNotFound)]
    [InlineData(NotificationDeliveryFailure.Forbidden)]
    public async Task TestMessage_ReturnsSafeActionableDeliveryError(NotificationDeliveryFailure failure)
    {
        // Arrange
        NotificationTestData data = new();
        TelegramChat chat = data.AddChat(-100123, "Операторы");
        data.Sender.Failure = new NotificationDeliveryException(failure);

        // Act
        ServiceResult result = await data.AdministrationService.SendTestMessageAsync(chat.Id, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(result.Success);
        Assert.Equal(ServiceErrorType.Conflict, result.Error!.Type);
        Assert.Equal(data.Sender.Failure.UserMessage, result.Error.Message);
    }

    [Fact]
    public async Task TestMessage_DoesNotExposeUnclassifiedExceptionMessage()
    {
        // Arrange
        NotificationTestData data = new();
        TelegramChat chat = data.AddChat(-100123, "Операторы");
        data.Sender.Failure = new NotificationDeliveryException("secret token and response body");

        // Act
        ServiceResult result = await data.AdministrationService.SendTestMessageAsync(chat.Id, TestContext.Current.CancellationToken);

        // Assert
        Assert.False(result.Success);
        Assert.DoesNotContain("secret", result.Error!.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConcurrentDuplicateChat_ReturnsSafeConflict()
    {
        NotificationTestData data = new(
            saveException: new PersistenceConflictException(
                PersistenceConflictKind.DuplicateTelegramChatId,
                new InvalidOperationException(
                    "Server=secret; duplicate key in telegram_chats.")));

        ServiceResult<TelegramChatDto> result = await data.AdministrationService.CreateChatAsync(
            -100123,
            "Операторы",
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ServiceErrorType.Conflict, result.Error!.Type);
        Assert.Equal("telegramChatId", result.Error.Field);
        Assert.DoesNotContain("secret", result.Error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("telegram_chats", result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConcurrentDuplicateBinding_ReturnsSafeConflict()
    {
        NotificationTestData data = new(
            saveException: new PersistenceConflictException(
                PersistenceConflictKind.DuplicateNotificationBinding,
                new InvalidOperationException(
                    "Violation of IX_notification_bindings_TelegramChatId_ServerId.")));
        MonitoredServer server = data.AddServer();
        TelegramChat chat = data.AddChat(-100123, "Операторы");

        ServiceResult<NotificationBindingDto> result =
            await data.AdministrationService.CreateBindingAsync(
                chat.Id,
                MonitoringObjectKind.Server,
                server.Id,
                TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ServiceErrorType.Conflict, result.Error!.Type);
        Assert.DoesNotContain("IX_notification", result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnexpectedPersistenceConflictKind_IsNotMasked()
    {
        PersistenceConflictException exception = new(
            PersistenceConflictKind.DuplicateNotificationBinding,
            new InvalidOperationException("Unexpected conflict."));
        NotificationTestData data = new(saveException: exception);

        PersistenceConflictException thrown =
            await Assert.ThrowsAsync<PersistenceConflictException>(() =>
                data.AdministrationService.CreateChatAsync(
                    -100123,
                    "Операторы",
                    TestContext.Current.CancellationToken));

        Assert.Same(exception, thrown);
    }

    [Theory]
    [InlineData("List")]
    [InlineData("CreateChat")]
    [InlineData("UpdateChat")]
    [InlineData("DeleteChat")]
    [InlineData("TestChat")]
    [InlineData("CreateBinding")]
    [InlineData("DeleteBinding")]
    public async Task OrdinaryUser_CannotManageNotifications(string operation)
    {
        NotificationTestData data = new(isAdministrator: false);
        MonitoredServer server = data.AddServer();
        TelegramChat chat = data.AddChat(-100123, "Операторы");
        NotificationBinding binding = NotificationBinding.ForServer(Guid.NewGuid(), chat.Id, server.Id);
        data.Bindings.Items.Add(binding);

        ServiceError error = operation switch
        {
            "List" => (await data.AdministrationService.GetSettingsAsync(TestContext.Current.CancellationToken)).Error!,
            "CreateChat" => (await data.AdministrationService.CreateChatAsync(-100124, "Новый", TestContext.Current.CancellationToken)).Error!,
            "UpdateChat" => (await data.AdministrationService.UpdateChatAsync(chat.Id, -100124, "Новый", TestContext.Current.CancellationToken)).Error!,
            "DeleteChat" => (await data.AdministrationService.DeleteChatAsync(chat.Id, TestContext.Current.CancellationToken)).Error!,
            "TestChat" => (await data.AdministrationService.SendTestMessageAsync(chat.Id, TestContext.Current.CancellationToken)).Error!,
            "CreateBinding" => (await data.AdministrationService.CreateBindingAsync(chat.Id, MonitoringObjectKind.Server, server.Id, TestContext.Current.CancellationToken)).Error!,
            "DeleteBinding" => (await data.AdministrationService.DeleteBindingAsync(binding.Id, TestContext.Current.CancellationToken)).Error!,
            _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, "Неизвестная операция.")
        };

        Assert.Equal(ServiceErrorType.Forbidden, error.Type);
        Assert.Equal(0, data.UnitOfWork.SaveCount);
        Assert.Empty(data.Sender.Sent);
    }
}
