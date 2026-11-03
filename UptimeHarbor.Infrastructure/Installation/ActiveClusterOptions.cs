using System.Text.Json;
using Microsoft.Extensions.Options;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Application.Installation;

namespace UptimeHarbor.Infrastructure.Installation;

/// <summary>Читает immutable опубликованный профиль, не изменяет operator options/config.</summary>
public class ActiveClusterOptions(IOptionsFactory<ClusterOptions> factory, InstallationState state) : IOptions<ClusterOptions>
{
    private readonly ClusterOptions initial = factory.Create(Options.DefaultName);
    private readonly object sync = new();
    private long revision = -1;
    private ClusterOptions? active;

    public ClusterOptions Value
    {
        get
        {
            LocalInstallationProfile? profile = state.Current;
            if (profile is null)
            {
                return initial;
            }

            lock (sync)
            {
                if (revision != profile.ProfileRevision)
                {
                    ClusterOptions next = JsonSerializer.Deserialize<ClusterOptions>(JsonSerializer.Serialize(initial))!;
                    if (profile.Mode is "Bootstrap" or "Join")
                    {
                        next.Mode = profile.Mode;
                        next.ClusterId = profile.ClusterId ?? Guid.Empty;
                        next.Generation = profile.ClusterGeneration ?? 0;
                        next.CredentialId = profile.CredentialId ?? Guid.Empty;
                        next.ClientCaFingerprint = profile.TrustAnchorFingerprint ?? string.Empty;
                        next.NodeId = profile.NodeId ?? Guid.Empty;
                        next.StorageGeneration = profile.StorageGeneration ?? Guid.Empty;
                        next.PublicUri = profile.PeerAddress ?? string.Empty;
                        next.ExpectedBotId = profile.BotId;
                    }

                    active = next;
                    revision = profile.ProfileRevision;
                }

                return active!;
            }
        }
    }
}
