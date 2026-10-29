using System.Globalization;

namespace UptimeHarbor.Infrastructure.Cluster;

/// <summary>Durable отказ от повторного прикладного запроса; бизнес-идемпотентность принадлежит reducer.</summary>
public class PeerReplayInbox : IDisposable
{
    private readonly object sync = new();
    private readonly Dictionary<(Guid Node, Guid Request), DateTimeOffset> entries = new();
    private readonly FileStream file;
    private readonly string path;
    private readonly int capacity;

    public PeerReplayInbox(string path, int capacity, DateTimeOffset now)
    {
        this.capacity = capacity;
        this.path = path;
        file = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
            4096, FileOptions.WriteThrough);
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            using StreamReader reader = new(path);
            while (reader.ReadLine() is string line)
            {
                string[] parts = line.Split(' ');
                if (parts.Length != 3 || !Guid.TryParse(parts[0], out Guid node)
                    || !Guid.TryParse(parts[1], out Guid request) || !long.TryParse(parts[2], out long ticks))
                {
                    throw new InvalidDataException("Peer inbox is corrupt.");
                }

                DateTimeOffset at = new(ticks, TimeSpan.Zero);
                if (now - at < TimeSpan.FromMinutes(2))
                {
                    entries.Add((node, request), at);
                }
            }
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    public bool TryAccept(Guid node, Guid request, DateTimeOffset now)
    {
        lock (sync)
        {
            foreach (var entry in entries.ToArray())
            {
                if (now - entry.Value >= TimeSpan.FromMinutes(2))
                {
                    entries.Remove(entry.Key);
                }
            }

            if (entries.ContainsKey((node, request)) || entries.Count >= capacity)
            {
                return false;
            }

            string temporary = path + ".tmp";
            using (FileStream pending = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None,
                4096, FileOptions.WriteThrough))
            {
                using StreamWriter writer = new(pending, leaveOpen: true);
                foreach (var entry in entries)
                {
                    writer.WriteLine($"{entry.Key.Node:D} {entry.Key.Request:D} {entry.Value.UtcTicks.ToString(CultureInfo.InvariantCulture)}");
                }

                writer.WriteLine($"{node:D} {request:D} {now.UtcTicks.ToString(CultureInfo.InvariantCulture)}");
                writer.Flush();
                pending.Flush(true);
            }

            File.Move(temporary, path, overwrite: true);
            entries.Add((node, request), now);
            return true;
        }
    }

    public void Dispose()
    {
        file.Dispose();
    }
}
