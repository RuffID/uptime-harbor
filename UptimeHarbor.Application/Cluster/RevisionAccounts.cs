using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Security;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Application.Cluster;

public static class RevisionAccounts
{
    public static AcceptedAccount? Find(RevisionImage image, Guid userId)
    {
        if (!image.Values.TryGetValue(userId, out AcceptedValue? value) || value.Kind != DeletedEntityKind.Account)
        {
            return null;
        }

        return new AcceptedAccount(userId, value.Revision, value.Value.Enabled, true, value.Deleted);
    }

    public static SharedAccount Materialize(AcceptedValue value)
    {
        ConfigurationValue data = value.Value;
        ProtectedCredential credential = new(data.PasswordHash!, data.PasswordHashFormat,
            data.SecurityStamp!, data.ConcurrencyStamp!);
        return new SharedAccount(value.Id, data.RoleId, data.Name!, credential,
            data.Enabled, value.Revision, value.DeletedAtUtc);
    }
}
