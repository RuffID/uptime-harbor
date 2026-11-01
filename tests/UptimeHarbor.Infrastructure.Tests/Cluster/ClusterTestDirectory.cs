namespace UptimeHarbor.Tests.Infrastructure.Cluster;

internal class ClusterTestDirectory : IDisposable
{
    private readonly string parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "uptime-harbor-cluster-tests"));
    public string Location { get; }

    public ClusterTestDirectory()
    {
        Location = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Location);
    }

    public void Dispose()
    {
        string target = Path.GetFullPath(Location);
        if (!target.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Unexpected test cleanup path.");
        }

        Directory.Delete(target, true);
    }
}
