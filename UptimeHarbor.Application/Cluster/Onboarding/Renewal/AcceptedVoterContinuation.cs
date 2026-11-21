using System.Text.Json.Serialization;
using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Application.Cluster.Onboarding.Renewal;

public record AcceptedVoterContinuation(RevisionCommand Request,
    RevisionCommand? Authorization = null, RevisionCommand? Registration = null)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RevisionCommand? Bind { get; init; }
}
