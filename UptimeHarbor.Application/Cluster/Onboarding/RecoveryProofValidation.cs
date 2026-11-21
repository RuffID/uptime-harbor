using UptimeHarbor.Application.Cluster.Models;

namespace UptimeHarbor.Application.Cluster.Onboarding;

/// <summary>Структурная граница typed proof. Подпись/диск/accepted registry проверяет соответствующий admission source.</summary>
public static class RecoveryProofValidation
{
    public static bool CanRestore(RecoveryAdmission admission, bool legacyOffline = false)
    {
        switch (admission.Proof)
        {
            case null:
                return legacyOffline && admission.FencingConfirmed;
            case SignedOfflineRecoveryProof offline:
                return admission.FencingConfirmed && offline.IsolationEvidenceDigest.Length == 64
                    && offline.IsolationEvidenceDigest.All(char.IsAsciiHexDigit);
            default:
                return false;
        }
    }

    public static bool CanBootstrap(RecoveryAdmission admission, bool legacyOffline = false)
    {
        return CanRestore(admission, legacyOffline) && !admission.OldBackup;
    }
}
