using System.Text.Json;
using UptimeHarbor.Application.Installation;

namespace UptimeHarbor.Infrastructure.Installation;

/// <summary>Durable round-trip создаёт новые immutable collections; record reference equality для них недостаточна.</summary>
public static class LocalProfileEquality
{
    public static bool Same(LocalInstallationProfile? left, LocalInstallationProfile? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null)
        {
            return false;
        }

        return JsonElement.DeepEquals(JsonSerializer.SerializeToElement(left), JsonSerializer.SerializeToElement(right));
    }
}
