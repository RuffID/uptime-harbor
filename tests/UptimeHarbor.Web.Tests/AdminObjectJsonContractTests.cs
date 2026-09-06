using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.ObjectAdministration.Models;
using UptimeHarbor.Domain.Monitoring;
using UptimeHarbor.Application.Common;
using UptimeHarbor.Web.Http;
using Xunit;

namespace UptimeHarbor.Tests.Web;

public class AdminObjectJsonContractTests
{
    [Fact]
    public void AdminObjectDtos_UseNumericMonitoringStatusWithMvcOptions()
    {
        ServiceCollection services = new();
        services.AddControllers();
        using ServiceProvider provider = services.BuildServiceProvider();
        JsonSerializerOptions options = provider
            .GetRequiredService<IOptions<JsonOptions>>()
            .Value
            .JsonSerializerOptions;

        AdminServerDto server = new(
            Guid.NewGuid(),
            "Production server",
            "https://server.example/health",
            "server",
            true,
            "Server",
            MonitoringStatus.Available,
            0,
            7);
        AdminContainerDto container = new(
            Guid.NewGuid(),
            server.Id,
            "https://server.example/container/health",
            null,
            true,
            "Container",
            MonitoringStatus.Unavailable,
            2,
            9);
        AdminServerDto unknownServer = server with { Status = MonitoringStatus.Unknown };

        JsonResult serverResult = JsonResultMapper.ToJsonResult(
            ServiceResult<IReadOnlyList<AdminServerDto>>.Ok([server]));
        JsonResult containerResult = JsonResultMapper.ToJsonResult(
            ServiceResult<IReadOnlyList<AdminContainerDto>>.Ok([container]));
        JsonResult unknownResult = JsonResultMapper.ToJsonResult(
            ServiceResult<IReadOnlyList<AdminServerDto>>.Ok([unknownServer]));
        using JsonDocument serverJson = JsonDocument.Parse(JsonSerializer.Serialize(serverResult.Value, options));
        using JsonDocument containerJson = JsonDocument.Parse(JsonSerializer.Serialize(containerResult.Value, options));
        using JsonDocument unknownJson = JsonDocument.Parse(JsonSerializer.Serialize(unknownResult.Value, options));

        JsonElement serverPayload = serverJson.RootElement.GetProperty("data")[0];
        JsonElement containerPayload = containerJson.RootElement.GetProperty("data")[0];
        Assert.Equal("Production server", serverPayload.GetProperty("name").GetString());
        Assert.Equal("Server", serverPayload.GetProperty("lastKnownName").GetString());
        Assert.Equal(JsonValueKind.Number, serverPayload.GetProperty("status").ValueKind);
        Assert.Equal((int)MonitoringStatus.Available, serverPayload.GetProperty("status").GetInt32());
        Assert.Equal(JsonValueKind.Number, containerPayload.GetProperty("status").ValueKind);
        Assert.Equal((int)MonitoringStatus.Unavailable, containerPayload.GetProperty("status").GetInt32());
        Assert.Equal((int)MonitoringStatus.Unknown, unknownJson.RootElement.GetProperty("data")[0].GetProperty("status").GetInt32());
    }

    [Fact]
    public void EmptyAdminObjectList_UsesArrayDataEnvelope()
    {
        ServiceCollection services = new();
        services.AddControllers();
        using ServiceProvider provider = services.BuildServiceProvider();
        JsonSerializerOptions options = provider
            .GetRequiredService<IOptions<JsonOptions>>()
            .Value
            .JsonSerializerOptions;

        object envelope = new { success = true, data = Array.Empty<AdminServerDto>() };
        using JsonDocument json = JsonDocument.Parse(JsonSerializer.Serialize(envelope, options));

        Assert.True(json.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(JsonValueKind.Array, json.RootElement.GetProperty("data").ValueKind);
        Assert.Empty(json.RootElement.GetProperty("data").EnumerateArray());
    }
}
