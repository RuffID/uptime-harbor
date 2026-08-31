namespace UptimeHarbor.Application.Abstractions.UnitOfWork;

public class PersistenceConflictException : Exception
{
    public PersistenceConflictException(
        PersistenceConflictKind kind,
        Exception innerException)
        : base("Операция нарушает ограничение уникальности хранилища.", innerException)
    {
        Kind = kind;
    }

    public PersistenceConflictKind Kind { get; }
}
