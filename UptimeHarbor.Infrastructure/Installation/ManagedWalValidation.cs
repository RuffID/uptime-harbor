using System.Buffers.Binary;
using DotNext.Net.Cluster;

namespace UptimeHarbor.Infrastructure.Installation;

/// <summary>Проверяет исходные метаданные WAL DotNext 6.10.0 до конструктора, способного пересоздать их.</summary>
internal static class ManagedWalValidation
{
    private const int CHECKPOINT_BYTES = sizeof(uint) + 2 * sizeof(long);

    public static void RequireCheckpoint(string root, long minimumIndex, long minimumTerm)
    {
        // Формат закреплённой версии: vote flag/member/term и checkpoint v1/commit/last.
        // Это startup-проверка под installation lock, до запуска native writers.
        byte[] state = ReadExact(Path.Combine(root, "wal", "state"), 1 + ClusterMemberId.Size + sizeof(long));
        long term = BinaryPrimitives.ReadInt64LittleEndian(state.AsSpan(1 + ClusterMemberId.Size));
        if (state[0] > 1 || term < minimumTerm)
        {
            throw new InvalidDataException("ManagedWalTermRollbackOrInvalid");
        }

        byte[] checkpoint = ReadExact(Path.Combine(root, "wal", "checkpoint"), CHECKPOINT_BYTES);
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(checkpoint);
        long committed = BinaryPrimitives.ReadInt64LittleEndian(checkpoint.AsSpan(sizeof(uint)));
        long last = BinaryPrimitives.ReadInt64LittleEndian(checkpoint.AsSpan(sizeof(uint) + sizeof(long)));
        if (version != 1 || committed < minimumIndex || last < committed)
        {
            throw new InvalidDataException("ManagedWalCheckpointRollbackOrInvalid");
        }
    }

    private static byte[] ReadExact(string path, int length)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != length)
        {
            throw new InvalidDataException("ManagedWalIncompleteOfflineRecoveryRequired");
        }

        ProtectedInstallationFiles.VerifyPermissions(path);
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] bytes = new byte[length];
        stream.ReadExactly(bytes);
        return bytes;
    }
}
