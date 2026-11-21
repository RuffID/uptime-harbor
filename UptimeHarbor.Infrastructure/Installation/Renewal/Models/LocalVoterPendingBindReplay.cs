using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Infrastructure.Installation.Renewal.Models;

/// <summary>Префикс защищённых записей, для которого sealed replay ещё не содержит решения Bind.</summary>
internal sealed record LocalVoterPendingBindReplay(LocalVoterCredentialIntent Material,
    LocalVoterCredentialBindIntent Bind, CommandResult Authorization, ProjectionHead Head);
