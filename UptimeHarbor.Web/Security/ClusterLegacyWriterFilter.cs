using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Common;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Web.Http;

namespace UptimeHarbor.Web.Security;

/// <summary>В сетке legacy read/write не подменяет accepted read model и command pipeline.</summary>
public class ClusterLegacyWriterFilter(IOptions<ClusterOptions> options, ClusterAdministrationAccess access) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (!options.Value.Enabled)
        {
            await next();
            return;
        }

        ServiceError? error = access.GetError();
        if (error is not null)
        {
            context.Result = JsonResultMapper.ToJsonResult(ServiceResult.Fail(error));
            return;
        }

        string method = ((ControllerActionDescriptor)context.ActionDescriptor).MethodInfo.Name;
        bool pipeline = method is "SubmitAsync" or "SendTestMessageAsync";
        bool sharedAccounts = context.Controller is Controllers.AdminUsersController
            && method is "GetAsync" or "GetOperation";
        if (!pipeline && !sharedAccounts)
        {
            context.Result = JsonResultMapper.ToJsonResult(ServiceResult.Fail(ServiceErrorType.Conflict,
                "Используйте /api/admin/cluster: общая конфигурация и наблюдения по observers читаются из принятой истории; изменения требуют OperationId и базовой ревизии."));
            return;
        }

        await next();
    }
}
