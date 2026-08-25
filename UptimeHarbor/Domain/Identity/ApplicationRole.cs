using EFCoreLibrary.Abstractions.Entity;
using Microsoft.AspNetCore.Identity;

namespace UptimeHarbor.Domain.Identity;

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

        Id = id;
        Name = name.Trim();
    }
}
