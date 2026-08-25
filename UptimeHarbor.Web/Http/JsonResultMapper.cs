using Microsoft.AspNetCore.Mvc;
using UptimeHarbor.Application.Common;

namespace UptimeHarbor.Web.Http;

public static class JsonResultMapper
{
    public static JsonResult ToJsonResult(ServiceResult result)
    {
        return result.Success
            ? new JsonResult(new { success = true }) { StatusCode = StatusCodes.Status200OK }
            : CreateFailure(result.Error);
    }

    public static JsonResult ToJsonResult<T>(ServiceResult<T> result)
    {
        return result.Success
            ? new JsonResult(new { success = true, data = result.Data })
            {
                StatusCode = StatusCodes.Status200OK
            }
            : CreateFailure(result.Error);
    }

    private static JsonResult CreateFailure(ServiceError? error)
    {
        ServiceError requiredError = error
            ?? throw new InvalidOperationException("Неуспешный результат должен содержать ошибку.");
        int statusCode = requiredError.Type switch
        {
            ServiceErrorType.Validation => StatusCodes.Status400BadRequest,
            ServiceErrorType.Unauthorized => StatusCodes.Status401Unauthorized,
            ServiceErrorType.Forbidden => StatusCodes.Status403Forbidden,
            ServiceErrorType.NotFound => StatusCodes.Status404NotFound,
            ServiceErrorType.Conflict => StatusCodes.Status409Conflict,
            _ => throw new ArgumentOutOfRangeException(
                nameof(requiredError.Type),
                requiredError.Type,
                "Неизвестный тип ошибки.")
        };

        return new JsonResult(new { success = false, message = requiredError.Message })
        {
            StatusCode = statusCode
        };
    }
}
