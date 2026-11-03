using UptimeHarbor.Application.Abstractions.UnitOfWork;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using System.Security.Cryptography;
using System.Text;

namespace UptimeHarbor.Infrastructure.Installation;

/// <summary>Исходный intent сохраняется до authority reserve; повтор возвращает точные прежние bytes/IDs.</summary>
public class OnboardingProgress(IOnboardingProgressUnitOfWork work, LocalSecretStore secrets)
{
    public async Task<LocalOnboardingAttempt> PrepareAsync(LocalOnboardingAttempt intent, CancellationToken token)
    {
        if (EnrollmentRequestCodec.Digest(intent.Request) != intent.RequestDigest)
        {
            throw new InvalidDataException("AttemptDigestMismatch");
        }

        if (intent.ProtectedInvitation is not null)
        {
            byte[] invitation = secrets.ReadInvitation(intent.AttemptId, intent.ProtectedInvitation);
            CryptographicOperations.ZeroMemory(invitation);
        }

        byte[] privateKey = secrets.Read(intent.AttemptId, "peer", intent.PrivateKeyReference);
        try
        {
            using ECDsa key = ECDsa.Create();
            key.ImportFromPem(Encoding.UTF8.GetString(privateKey));
            if (Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo())) != intent.Request.SpkiDigest)
            {
                throw new InvalidDataException("AttemptPublicKeyMismatch");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateKey);
        }

        LocalOnboardingAttempt? existing = await work.Attempts.FindAsync(intent.AttemptId, token);
        if (existing is not null)
        {
            if (existing.Request != intent.Request || existing.RequestDigest != intent.RequestDigest
                || existing.PrivateKeyReference != intent.PrivateKeyReference
                || existing.BrowserOrigin != intent.BrowserOrigin || existing.CsrDer != intent.CsrDer
                || !existing.OperationIds.OrderBy(item => item.Key).SequenceEqual(intent.OperationIds.OrderBy(item => item.Key)))
            {
                throw new InvalidDataException("AttemptPayloadChanged");
            }

            if (existing.ProtectedInvitation is not null)
            {
                byte[] invitation = secrets.ReadInvitation(existing.AttemptId, existing.ProtectedInvitation);
                CryptographicOperations.ZeroMemory(invitation);
            }

            secrets.Reconcile(existing.AttemptId, existing.PrivateKeyReference);
            return existing;
        }

        secrets.Reconcile(intent.AttemptId, intent.PrivateKeyReference);
        work.Attempts.Create(intent);
        await work.SaveChangesAsync(token);
        return intent;
    }

    public async Task ReservedAsync(Guid attemptId, long acceptedIndex, long expectedRevision, CancellationToken token)
    {
        LocalOnboardingAttempt original = await work.Attempts.FindAsync(attemptId, token)
            ?? throw new InvalidDataException("AttemptMissing");
        if (original.ReservationIndex == acceptedIndex && original.ProtectedInvitation is null)
        {
            return;
        }

        if (original.ReservationIndex.HasValue)
        {
            throw new InvalidDataException("ReservationResultChanged");
        }

        await work.Attempts.ReplaceAsync(original.Reserved(acceptedIndex), expectedRevision, token);
        await work.SaveChangesAsync(token);
    }
}
