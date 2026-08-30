using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using UptimeHarbor.Application.Common;

namespace UptimeHarbor.Web.Http;

public static class ModelStateErrorResponseFactory
{
    public static IActionResult Create(ActionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        KeyValuePair<string, ModelStateEntry?> firstFailure = context.ModelState
            .Where(entry => entry.Value?.Errors.Count > 0)
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .FirstOrDefault();
        ModelStateEntry entry = firstFailure.Value
            ?? throw new InvalidOperationException(
                "InvalidModelStateResponseFactory вызвана без ошибок model state.");
        string? field = NormalizeField(firstFailure.Key);
        bool isInputFormatError = entry.Errors.Any(error => error.Exception is not null);
        string message = field switch
        {
            null => "Некорректное тело запроса.",
            _ when isInputFormatError => "Поле содержит значение неверного типа.",
            _ => "Поле заполнено некорректно."
        };
        ServiceResult failure = field is null
            ? ServiceResult.Fail(ServiceErrorType.Validation, message)
            : ServiceResult.Fail(ServiceErrorType.Validation, message, field);
        return JsonResultMapper.ToJsonResult(failure);
    }

    private static string? NormalizeField(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || string.Equals(key, "$", StringComparison.Ordinal))
        {
            return null;
        }

        string normalized = key.StartsWith("$.", StringComparison.Ordinal)
            ? key[2..]
            : key;
        int separatorIndex = normalized.LastIndexOf('.');
        if (separatorIndex >= 0)
        {
            normalized = normalized[(separatorIndex + 1)..];
        }

        return JsonNamingPolicy.CamelCase.ConvertName(normalized);
    }
}
