using System.Security.Cryptography;
using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Infrastructure.Cluster.Onboarding;

/// <summary>Одноразовые 60-секундные nonce текущего процесса с ограниченным объёмом памяти.</summary>
public class EnrollmentNonces(TimeProvider time)
{
    private readonly object sync = new();
    private readonly Dictionary<string, Entry> issued = new(StringComparer.Ordinal);
    private const int CAPACITY = 512;

    public string Issue(Guid attemptId, Guid bootId, string bootChallenge, long processEpoch, string action, string digest)
    {
        if (attemptId == Guid.Empty || bootId == Guid.Empty || !EnrollmentProofs.IsChallenge(bootChallenge)
            || processEpoch < 1 || digest.Length != 64 || !digest.All(char.IsAsciiHexDigit) || action.Length is < 1 or > 64)
        {
            throw new InvalidDataException("EnrollmentNonceBindingInvalid");
        }

        lock (sync)
        {
            long now = time.GetTimestamp();
            foreach (string expired in issued.Where(item => time.GetElapsedTime(item.Value.Started, now) >= TimeSpan.FromSeconds(60))
                .Select(item => item.Key).ToArray())
            {
                issued.Remove(expired);
            }

            if (issued.Count >= CAPACITY)
            {
                throw new InvalidOperationException("EnrollmentNonceCapacityExceeded");
            }

            string nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            issued.Add(nonce, new(attemptId, bootId, bootChallenge, processEpoch, action, digest, now));
            return nonce;
        }
    }

    public bool Consume(Guid attemptId, EnrollmentProof proof)
    {
        lock (sync)
        {
            if (!issued.TryGetValue(proof.Nonce, out Entry? entry))
            {
                return false;
            }

            issued.Remove(proof.Nonce);
            TimeSpan age = time.GetElapsedTime(entry.Started);
            return age >= TimeSpan.Zero && age < TimeSpan.FromSeconds(60) && entry.AttemptId == attemptId
                && entry.BootId == proof.BootId && entry.BootChallenge == proof.BootChallenge
                && entry.ProcessEpoch == proof.ProcessEpoch
                && entry.Action == proof.Action && entry.Digest == proof.RequestDigest;
        }
    }

    private record Entry(Guid AttemptId, Guid BootId, string BootChallenge, long ProcessEpoch, string Action, string Digest, long Started);
}
