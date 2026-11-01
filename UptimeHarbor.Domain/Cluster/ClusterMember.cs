namespace UptimeHarbor.Domain.Cluster;

public class ClusterMember : ProjectionRecord
{
    private ClusterMember()
    {
    }

    public ClusterMember(Guid nodeId, JournalPosition position, Guid storageGeneration,
        string peerUri, string raftMemberId, long membershipVersion, bool isVoting)
        : base(nodeId, position)
    {
        StorageGuards.Id(storageGeneration, nameof(storageGeneration));
        if (!Uri.TryCreate(peerUri, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArgumentException("A canonical HTTPS peer URI is required.", nameof(peerUri));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(membershipVersion);
        StorageGeneration = storageGeneration;
        PeerUri = StorageGuards.Text(uri.AbsoluteUri, 2048, nameof(peerUri));
        RaftMemberId = StorageGuards.Text(raftMemberId, 128, nameof(raftMemberId));
        MembershipVersion = membershipVersion;
        IsVoting = isVoting;
    }

    public Guid StorageGeneration { get; private set; }
    public string PeerUri { get; private set; } = string.Empty;
    public string RaftMemberId { get; private set; } = string.Empty;
    public long MembershipVersion { get; private set; }
    public bool IsVoting { get; private set; }
}
