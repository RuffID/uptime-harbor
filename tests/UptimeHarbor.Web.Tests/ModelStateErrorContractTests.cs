using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace UptimeHarbor.Tests.Web;

public class ModelStateErrorContractTests
{
    [Fact]
    public void InvalidModelStateResponseFactory_MalformedJson_ReturnsSafeBodyEnvelope()
    {
        ModelStateDictionary modelState = new();
        modelState.TryAddModelException(
            "$",
            new JsonException("Internal parser detail for Some.Internal.ClrType."));

        (JsonResult result, JsonDocument json) = ExecuteFactory(modelState);
        using (json)
        {
            Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
            Assert.False(json.RootElement.GetProperty("success").GetBoolean());
            Assert.Equal(
                "Некорректное тело запроса.",
                json.RootElement.GetProperty("message").GetString());
            Assert.False(json.RootElement.TryGetProperty("field", out _));
            Assert.DoesNotContain("Internal parser", json.RootElement.GetRawText(), StringComparison.Ordinal);
            Assert.DoesNotContain("ClrType", json.RootElement.GetRawText(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void InvalidModelStateResponseFactory_MissingBody_ReturnsSafeBodyEnvelope()
    {
        ModelStateDictionary modelState = new();
        modelState.AddModelError(string.Empty, "A non-empty request body is required.");

        (JsonResult result, JsonDocument json) = ExecuteFactory(modelState);
        using (json)
        {
            Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
            Assert.Equal(
                "Некорректное тело запроса.",
                json.RootElement.GetProperty("message").GetString());
            Assert.False(json.RootElement.TryGetProperty("field", out _));
        }
    }

    [Fact]
    public void InvalidModelStateResponseFactory_WrongFieldType_ReturnsDeterministicFieldEnvelope()
    {
        ModelStateDictionary modelState = new();
        modelState.TryAddModelException(
            "$.version",
            new JsonException("The JSON value could not be converted to System.Int64."));

        (JsonResult result, JsonDocument json) = ExecuteFactory(modelState);
        using (json)
        {
            Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
            Assert.Equal(
                "Поле содержит значение неверного типа.",
                json.RootElement.GetProperty("message").GetString());
            Assert.Equal("version", json.RootElement.GetProperty("field").GetString());
            Assert.DoesNotContain("System.Int64", json.RootElement.GetRawText(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void InvalidModelStateResponseFactory_ValidationError_ReturnsCamelCaseField()
    {
        ModelStateDictionary modelState = new();
        modelState.AddModelError("Login", "The Login field is required.");

        (JsonResult result, JsonDocument json) = ExecuteFactory(modelState);
        using (json)
        {
            Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
            Assert.Equal(
                "Поле заполнено некорректно.",
                json.RootElement.GetProperty("message").GetString());
            Assert.Equal("login", json.RootElement.GetProperty("field").GetString());
        }
    }

    private static (JsonResult Result, JsonDocument Json) ExecuteFactory(
        ModelStateDictionary modelState)
    {
        ServiceCollection services = new();
        UptimeHarbor.Web.Pages.ServiceCollectionExtensions.AddWebPresentation(services);
        using ServiceProvider provider = services.BuildServiceProvider();
        ApiBehaviorOptions apiOptions = provider
            .GetRequiredService<IOptions<ApiBehaviorOptions>>()
            .Value;
        JsonSerializerOptions jsonOptions = provider
            .GetRequiredService<IOptions<JsonOptions>>()
            .Value
            .JsonSerializerOptions;
        ActionContext actionContext = new(
            new DefaultHttpContext(),
            new RouteData(),
            new ActionDescriptor(),
            modelState);

        JsonResult result = Assert.IsType<JsonResult>(
            apiOptions.InvalidModelStateResponseFactory(actionContext));
        JsonDocument json = JsonDocument.Parse(
            JsonSerializer.Serialize(result.Value, jsonOptions));
        return (result, json);
    }
}
