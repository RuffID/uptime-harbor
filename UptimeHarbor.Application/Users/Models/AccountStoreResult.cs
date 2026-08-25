namespace UptimeHarbor.Application.Users.Models;

public class AccountStoreResult
{
    private AccountStoreResult(bool succeeded, IReadOnlyList<string> errors)
    {
        Succeeded = succeeded;
        Errors = errors;
    }

    public bool Succeeded { get; }

    public IReadOnlyList<string> Errors { get; }

    public static AccountStoreResult Success() => new(true, []);

    public static AccountStoreResult Failure(IEnumerable<string> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        List<string> materializedErrors = errors.Where(error => !string.IsNullOrWhiteSpace(error)).ToList();
        if (materializedErrors.Count == 0)
        {
            throw new ArgumentException("Необходимо указать хотя бы одну ошибку.", nameof(errors));
        }

        return new AccountStoreResult(false, materializedErrors);
    }
}
