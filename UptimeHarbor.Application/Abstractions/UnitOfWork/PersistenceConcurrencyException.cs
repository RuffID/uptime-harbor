namespace UptimeHarbor.Application.Abstractions.UnitOfWork;

public class PersistenceConcurrencyException : Exception
{
    public PersistenceConcurrencyException(Exception innerException)
        : base("Сохранённые данные были изменены другим процессом.", innerException)
    {
    }
}
