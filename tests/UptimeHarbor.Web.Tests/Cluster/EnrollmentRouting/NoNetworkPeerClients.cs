namespace UptimeHarbor.Tests.Web.Cluster.EnrollmentRouting;

/// <summary>Локальные regression fixtures никогда не получают реальный HTTP client.</summary>
public class NoNetworkPeerClients : IHttpClientFactory
{
    public HttpClient CreateClient(string name)
    {
        throw new InvalidOperationException("TestUnexpectedPeerNetwork");
    }
}
