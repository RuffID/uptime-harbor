namespace UptimeHarbor.Application.Installation;

public record InstallationInventory(bool SchemaCurrent, IReadOnlyDictionary<string, bool> NonemptyTables);
