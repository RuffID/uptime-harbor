using EFCoreLibrary.Abstractions.Entity;
using Microsoft.AspNetCore.Identity;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Infrastructure.Identity;

public class ApplicationRole : IdentityRole<Guid>, IEntity<Guid>
{
    private ApplicationRole()
    {
    }

    public ApplicationRole(Guid id, string name)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Идентификатор роли не может быть пустым.", nameof(id));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (!string.Equals(name, SharedAccount.ADMINISTRATOR, StringComparison.Ordinal))
        {
            throw new ArgumentException("Only Administrator is supported.", nameof(name));
        }

        Id = id;
        Name = SharedAccount.ADMINISTRATOR;
        NormalizedName = "ADMINISTRATOR";
    }
}
