using Microsoft.AspNetCore.Identity;
using UptimeHarbor.Application.Common;
using UptimeHarbor.Application.Users;
using UptimeHarbor.Domain.Cluster;

namespace UptimeHarbor.Infrastructure.Identity;

public class AccountCredentialService(UserManager<ApplicationUser> users, IPasswordHasher<ApplicationUser> hasher)
    : IAccountCredentialService
{
    public async Task<ServiceResult<ProtectedCredential>> PrepareAsync(Guid userId, string login, string password,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ApplicationUser user = new(userId, login);
        foreach (IPasswordValidator<ApplicationUser> validator in users.PasswordValidators)
        {
            IdentityResult validation = await validator.ValidateAsync(users, user, password);
            if (!validation.Succeeded)
            {
                return ServiceResult<ProtectedCredential>.Fail(ServiceErrorType.Validation,
                    "Пароль не соответствует требованиям безопасности.", "password");
            }
        }

        ProtectedCredential credential = new(hasher.HashPassword(user, password), 1,
            Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"));
        return ServiceResult<ProtectedCredential>.Ok(credential);
    }

    public bool Verify(Guid userId, string login, ProtectedCredential credential, string password)
    {
        if (credential.FormatVersion != 1)
        {
            return false;
        }

        ApplicationUser user = new(userId, login);
        PasswordVerificationResult result = hasher.VerifyHashedPassword(user, credential.PasswordHash, password);
        // SuccessRehashNeeded не разрешает локальную мутацию общего hash: смена идёт только журналом.
        return result != PasswordVerificationResult.Failed;
    }
}
