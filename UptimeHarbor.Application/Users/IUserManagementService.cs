using UptimeHarbor.Application.Common;
using UptimeHarbor.Application.Users.Models;

namespace UptimeHarbor.Application.Users;

public interface IUserManagementService
{
    Task<ServiceResult<IReadOnlyList<UserAccountDto>>> GetUsersAsync(CancellationToken cancellationToken = default);

    Task<ServiceResult<UserAccountDto>> CreateUserAsync(
        string login,
        string initialPassword,
        CancellationToken cancellationToken = default);

    Task<ServiceResult> SetEnabledAsync(Guid userId, bool isEnabled, CancellationToken cancellationToken = default);

    Task<ServiceResult> DeleteAsync(Guid userId, CancellationToken cancellationToken = default);

    Task<ServiceResult> SetVisibilityAsync(
        Guid userId,
        IReadOnlyCollection<Guid> serverIds,
        IReadOnlyCollection<Guid> containerIds,
        CancellationToken cancellationToken = default);
}
