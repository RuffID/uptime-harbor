using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Common;
using UptimeHarbor.Web.Http;
using Xunit;

namespace UptimeHarbor.Tests.Web;

public class JsonResultMapperTests
{
    [Fact]
    public void ToJsonResult_ForGenericSuccess_ReturnsDataEnvelope()
    {
        JsonResult result = JsonResultMapper.ToJsonResult(ServiceResult<string>.Ok("value"));

        using JsonDocument json = SerializeValue(result);
        Assert.Equal(StatusCodes.Status200OK, result.StatusCode);
        Assert.True(json.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("value", json.RootElement.GetProperty("data").GetString());
    }

    [Fact]
    public void ToJsonResult_ForSuccessWithoutData_ReturnsSuccessEnvelope()
    {
        JsonResult result = JsonResultMapper.ToJsonResult(ServiceResult.Ok());

        using JsonDocument json = SerializeValue(result);
        Assert.Equal(StatusCodes.Status200OK, result.StatusCode);
        Assert.True(json.RootElement.GetProperty("success").GetBoolean());
        Assert.False(json.RootElement.TryGetProperty("data", out _));
    }

    [Theory]
    [InlineData(ServiceErrorType.Validation, StatusCodes.Status400BadRequest)]
    [InlineData(ServiceErrorType.Unauthorized, StatusCodes.Status401Unauthorized)]
    [InlineData(ServiceErrorType.Forbidden, StatusCodes.Status403Forbidden)]
    [InlineData(ServiceErrorType.NotFound, StatusCodes.Status404NotFound)]
    [InlineData(ServiceErrorType.Conflict, StatusCodes.Status409Conflict)]
    public void ToJsonResult_ForFailure_MapsSemanticErrorToHttp(
        ServiceErrorType errorType,
        int expectedStatusCode)
    {
        JsonResult result = JsonResultMapper.ToJsonResult(
            ServiceResult.Fail(new ServiceError(errorType, "Ошибка.", "endpointUrl")));

        using JsonDocument json = SerializeValue(result);
        Assert.Equal(expectedStatusCode, result.StatusCode);
        Assert.False(json.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("Ошибка.", json.RootElement.GetProperty("message").GetString());
        Assert.Equal("endpointUrl", json.RootElement.GetProperty("field").GetString());
    }

    [Fact]
    public void ToJsonResult_ForFailureWithoutField_PreservesBaseEnvelope()
    {
        JsonResult result = JsonResultMapper.ToJsonResult(
            ServiceResult.Fail(ServiceErrorType.NotFound, "Не найдено."));

        using JsonDocument json = SerializeValue(result);
        Assert.Equal(StatusCodes.Status404NotFound, result.StatusCode);
        Assert.False(json.RootElement.TryGetProperty("field", out _));
    }

    [Fact]
    public void ToJsonResult_ForPersistenceConflict_ReturnsSafe409Envelope()
    {
        JsonResult result = JsonResultMapper.ToJsonResult(
            ServiceResult<string>.Fail(
                ServiceErrorType.Conflict,
                "Telegram chat ID уже добавлен.",
                "telegramChatId"));

        using JsonDocument json = SerializeValue(result);
        string serialized = json.RootElement.GetRawText();
        Assert.Equal(StatusCodes.Status409Conflict, result.StatusCode);
        Assert.False(json.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(
            "Telegram chat ID уже добавлен.",
            json.RootElement.GetProperty("message").GetString());
        Assert.DoesNotContain("Server=", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("IX_", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SqlException", serialized, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ToJsonResult_ForUnknownErrorType_FailsFast()
    {
        ServiceResult result = ServiceResult.Fail(
            new ServiceError((ServiceErrorType)int.MaxValue, "Неизвестная ошибка."));

        Assert.Throws<ArgumentOutOfRangeException>(() => JsonResultMapper.ToJsonResult(result));
    }

    private static JsonDocument SerializeValue(JsonResult result)
    {
        ServiceCollection services = new();
        services.AddControllers();
        using ServiceProvider provider = services.BuildServiceProvider();
        JsonSerializerOptions options = provider
            .GetRequiredService<IOptions<JsonOptions>>()
            .Value
            .JsonSerializerOptions;
        return JsonDocument.Parse(JsonSerializer.Serialize(result.Value, options));
    }
}
