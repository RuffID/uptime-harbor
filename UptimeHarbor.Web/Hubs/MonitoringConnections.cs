using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Security;
using UptimeHarbor.Infrastructure.Identity;

namespace UptimeHarbor.Web.Hubs;

/// <summary>Локальные подписки; apply и недостоверное recovery немедленно отзывают старые credentials.</summary>
public class MonitoringConnections
{
    private readonly ConcurrentDictionary<string, HubCallerContext> connections = new();
    private readonly AcceptedAccountSession sessions;
    private readonly ILogger<MonitoringConnections> logger;

    public MonitoringConnections(RevisionStateStore state, AcceptedAccountSession sessions,
        ILogger<MonitoringConnections> logger)
    {
        this.sessions = sessions;
        this.logger = logger;
        state.Changed += Revalidate;
    }

    public bool Register(HubCallerContext context)
    {
        if (!sessions.IsValid(context.User))
        {
            context.Abort();
            return false;
        }

        connections[context.ConnectionId] = context;
        // Apply мог произойти между первой проверкой и добавлением соединения.
        Revalidate();
        return connections.ContainsKey(context.ConnectionId);
    }

    public void Remove(string connectionId)
    {
        connections.TryRemove(connectionId, out _);
    }

    public IReadOnlyList<string> AuthorizedIds(IReadOnlyList<Guid>? users = null)
    {
        Revalidate();
        HashSet<string>? allowed = users?.Select(user => user.ToString()).ToHashSet();
        return connections.Values.Where(context => allowed is null || allowed.Contains(context.UserIdentifier!))
            .Select(context => context.ConnectionId).ToArray();
    }

    private void Revalidate()
    {
        foreach (HubCallerContext context in connections.Values)
        {
            if (sessions.IsValid(context.User))
            {
                continue;
            }

            if (!connections.TryRemove(context.ConnectionId, out _))
            {
                continue;
            }

            try
            {
                context.Abort();
            }
            catch
            {
                logger.LogWarning("SignalR session abort failed for connection {ConnectionId}.", context.ConnectionId);
            }
        }
    }
}
