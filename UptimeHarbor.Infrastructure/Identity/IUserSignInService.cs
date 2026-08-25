namespace UptimeHarbor.Infrastructure.Identity;

public interface IUserSignInService
{
    Task<bool> PasswordSignInAsync(
        string login,
        string password,
        bool isPersistent,
        CancellationToken cancellationToken = default);

    Task SignOutAsync();
}
