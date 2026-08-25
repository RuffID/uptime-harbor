using EFCoreLibrary.Abstractions.Entity;
using Microsoft.AspNetCore.Identity;

namespace UptimeHarbor.Domain.Identity;

public class ApplicationUser : IdentityUser<Guid>, IEntity<Guid>
{
    private ApplicationUser()
    {
    }

    public ApplicationUser(Guid id, string login)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Идентификатор пользователя не может быть пустым.", nameof(id));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(login);

        Id = id;
        UserName = login.Trim();
        IsEnabled = true;
    }

    public bool IsEnabled { get; private set; }

    public void Enable()
    {
        IsEnabled = true;
    }

    public void Disable()
    {
        IsEnabled = false;
    }
}
