namespace UptimeHarbor.Application.Users.Models;

public class AccountStoreResult
{
    private AccountStoreResult(
        bool succeeded,
        AccountStoreFailureKind? failureKind,
        IReadOnlyList<string> errors)
    {
        Succeeded = succeeded;
        FailureKind = failureKind;
        Errors = errors;
    }

    public bool Succeeded { get; }

    public IReadOnlyList<string> Errors { get; }

    public AccountStoreFailureKind? FailureKind { get; }

    public static AccountStoreResult Success() => new(true, null, []);

    public static AccountStoreResult Failure(
        AccountStoreFailureKind failureKind,
        IEnumerable<string> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        List<string> materializedErrors = errors.Where(error => !string.IsNullOrWhiteSpace(error)).ToList();
        if (materializedErrors.Count == 0)
        {
            throw new ArgumentException("Необходимо указать хотя бы одну ошибку.", nameof(errors));
        }

        return new AccountStoreResult(false, failureKind, materializedErrors);
    }
}
