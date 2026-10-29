using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UptimeHarbor.Application.Common;
using UptimeHarbor.Application.Notifications;
using UptimeHarbor.Application.Notifications.Models;
using UptimeHarbor.Application.Security;
using UptimeHarbor.Web.Controllers.Models;
using UptimeHarbor.Web.Http;

namespace UptimeHarbor.Web.Controllers;

[ApiController]
[Route("api/admin/notifications")]
[Authorize(Roles = ApplicationRoles.ADMINISTRATOR)]
public class AdminNotificationsController(INotificationAdministrationService administrationService,
    IAcceptedTestDelivery? acceptedDelivery = null)
    : ControllerBase
{
    [HttpGet]
    public async Task<JsonResult> GetSettingsAsync(CancellationToken cancellationToken)
    {
        ServiceResult<NotificationSettingsDto> result =
            await administrationService.GetSettingsAsync(cancellationToken);
        return JsonResultMapper.ToJsonResult(result);
    }

    [HttpPost("chats")]
    [ValidateAntiForgeryToken]
    public async Task<JsonResult> CreateChatAsync(
        TelegramChatRequest request,
        CancellationToken cancellationToken)
    {
        ServiceResult<TelegramChatDto> result = await administrationService.CreateChatAsync(
            request.TelegramChatId,
            request.Name ?? string.Empty,
            cancellationToken);
        return JsonResultMapper.ToJsonResult(result);
    }

    [HttpPut("chats/{chatId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<JsonResult> UpdateChatAsync(
        Guid chatId,
        TelegramChatRequest request,
        CancellationToken cancellationToken)
    {
        ServiceResult<TelegramChatDto> result = await administrationService.UpdateChatAsync(
            chatId,
            request.TelegramChatId,
            request.Name ?? string.Empty,
            cancellationToken);
        return JsonResultMapper.ToJsonResult(result);
    }

    [HttpDelete("chats/{chatId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<JsonResult> DeleteChatAsync(
        Guid chatId,
        CancellationToken cancellationToken)
    {
        ServiceResult result = await administrationService.DeleteChatAsync(chatId, cancellationToken);
        return JsonResultMapper.ToJsonResult(result);
    }

    [HttpPost("chats/{chatId:guid}/test")]
    [ValidateAntiForgeryToken]
    public async Task<JsonResult> SendTestMessageAsync(
        Guid chatId,
        CancellationToken cancellationToken)
    {
        ServiceResult result = await administrationService.SendTestMessageAsync(chatId, cancellationToken);
        JsonResult response = JsonResultMapper.ToJsonResult(result);
        if (result.Success && acceptedDelivery is not null)
        {
            response.StatusCode = StatusCodes.Status202Accepted;
        }

        return response;
    }

    [HttpPost("bindings")]
    [ValidateAntiForgeryToken]
    public async Task<JsonResult> CreateBindingAsync(
        NotificationBindingRequest request,
        CancellationToken cancellationToken)
    {
        ServiceResult<NotificationBindingDto> result = await administrationService.CreateBindingAsync(
            request.ChatId,
            request.ObjectKind,
            request.ObjectId,
            cancellationToken);
        return JsonResultMapper.ToJsonResult(result);
    }

    [HttpDelete("bindings/{bindingId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<JsonResult> DeleteBindingAsync(
        Guid bindingId,
        CancellationToken cancellationToken)
    {
        ServiceResult result = await administrationService.DeleteBindingAsync(
            bindingId,
            cancellationToken);
        return JsonResultMapper.ToJsonResult(result);
    }
}
