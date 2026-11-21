using System.Text.Json.Serialization;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;

namespace UptimeHarbor.Infrastructure.Installation.Renewal.Models;

/// <summary>Публичная часть локального intent; ключ хранится только внутри защищённого bundle.</summary>
public record LocalVoterCredentialIntent(int Version, Guid InstallIdentity, string KeyReference,
    VoterCredentialRequest Request, string CsrDer, Guid PreviousKeyOwner, string PreviousKeyReference)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ContinuationBoot? Process { get; init; }

    public sealed record ContinuationBoot(Guid BootId, string BootChallenge)
    {
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public StartupProcess? Startup { get; init; }
    }

    public sealed record StartupProcess(long Epoch, long RecoveryVersion, long MembershipVersion,
        string AudiencePeerAddress, string Origin);

    public override string ToString()
    {
        return $"LocalVoterCredentialIntent {Request.OperationId}";
    }
}
