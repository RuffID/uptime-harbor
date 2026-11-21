using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Installation;

namespace UptimeHarbor.Infrastructure.Installation.Renewal;

/// <summary>Immutable AEAD файлы прежнего локального secret storage, без SQL и внешних эффектов.</summary>
public class LocalVoterCredentialFiles(IOptions<InstallationOptions> options, LocalSecretStore secrets)
{
    internal const string MANIFEST_STAGE = "install-manifest";
    internal const string REFERENCE_STAGE = "install-reference";
    internal const string CONFIRMATION_STAGE = "install-confirmation";
    internal const string RETIRE_STAGE = "retire-intent";
    internal const string CONTINUATION_REQUEST_RESULT = "continuation-request-result";
    internal const string CONTINUATION_BIND_INTENT = "continuation-bind-intent";
    internal const string CONTINUATION_BIND_RESULT = "continuation-bind-result";

    public bool HasSlot(string reference)
    {
        return Directory.Exists(DirectoryPath(reference));
    }

    internal bool HasInstallationRecords(string reference)
    {
        string directory = DirectoryPath(reference);
        return File.Exists(Path.Combine(directory, MANIFEST_STAGE))
            || File.Exists(Path.Combine(directory, REFERENCE_STAGE))
            || File.Exists(Path.Combine(directory, CONFIRMATION_STAGE));
    }

    internal HashSet<string> ReadRetryStages(string reference)
    {
        return Directory.EnumerateFileSystemEntries(DirectoryPath(reference), LocalVoterCredentialRetry.STAGE_PREFIX + "*")
            .Select(Path.GetFileName).OfType<string>().ToHashSet(StringComparer.Ordinal);
    }

    public byte[]? Read(string reference, string stage)
    {
        string directory = DirectoryPath(reference);
        if (!Directory.Exists(directory))
        {
            return null;
        }

        ProtectedInstallationFiles.VerifyPermissions(directory, true);
        foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
        {
            if (!IsStage(Path.GetFileName(entry)))
            {
                throw new InvalidDataException("VoterCredentialOrphanNeedsAction");
            }
        }

        string path = Path.Combine(directory, Stage(stage));
        if (!File.Exists(path))
        {
            return null;
        }

        ProtectedInstallationFiles.VerifyPermissions(path);
        if (new FileInfo(path).Length is < 40 or > 90000)
        {
            throw new InvalidDataException("VoterCredentialLocalFileLimit");
        }

        byte[] plaintext = secrets.ReadVoterRenewal(Slot(reference), File.ReadAllText(path, Encoding.ASCII));
        byte[] prefix = Encoding.ASCII.GetBytes("UptimeHarbor.LocalVoterCredential.v1/" + stage + "/");
        try
        {
            if (!plaintext.AsSpan().StartsWith(prefix))
            {
                throw new InvalidDataException("VoterCredentialLocalStageChanged");
            }

            // После неопределённого rename/fsync повтор также должен подтвердить каталоги.
            SyncDirectories(directory);
            return plaintext.AsSpan(prefix.Length).ToArray();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public virtual void WriteOnce(string reference, string stage, ReadOnlySpan<byte> bytes)
    {
        RuntimeGenerationContext.Verify();
        byte[]? existing = Read(reference, stage);
        if (existing is not null)
        {
            try
            {
                if (!existing.AsSpan().SequenceEqual(bytes))
                {
                    throw new InvalidDataException("VoterCredentialLocalPinChanged");
                }

                return;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(existing);
            }
        }

        string directory = DirectoryPath(reference);
        ProtectedInstallationFiles.EnsureDirectory(Path.GetDirectoryName(directory)!);
        ProtectedInstallationFiles.EnsureDirectory(directory);
        SyncDirectories(directory);
        byte[] prefix = Encoding.ASCII.GetBytes("UptimeHarbor.LocalVoterCredential.v1/" + Stage(stage) + "/");
        byte[] plaintext = new byte[prefix.Length + bytes.Length];
        prefix.CopyTo(plaintext, 0);
        bytes.CopyTo(plaintext.AsSpan(prefix.Length));
        try
        {
            string envelope = secrets.ProtectVoterRenewal(Slot(reference), plaintext);
            RuntimeGenerationContext.Verify();
            ProtectedInstallationFiles.WriteNew(Path.Combine(directory, stage), Encoding.ASCII.GetBytes(envelope));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private string DirectoryPath(string reference)
    {
        if (reference.Length != 64 || reference.Any(character => !char.IsAsciiHexDigit(character))
            || reference != reference.ToUpperInvariant())
        {
            throw new InvalidDataException("VoterCredentialLocalReferenceInvalid");
        }

        string root = ProtectedInstallationFiles.DirectoryPath(options.Value.SecretDirectory);
        ProtectedInstallationFiles.VerifyPermissions(root, true);
        string directory = ProtectedInstallationFiles.DirectoryPath(Path.Combine(root, "voter-renewal", reference));
        string parent = Path.GetDirectoryName(directory)!;
        if (Directory.Exists(parent))
        {
            ProtectedInstallationFiles.VerifyPermissions(parent, true);
        }

        return directory;
    }

    private static Guid Slot(string reference)
    {
        return new Guid(Convert.FromHexString(reference).AsSpan(0, 16));
    }

    private static void SyncDirectories(string directory)
    {
        string parent = Path.GetDirectoryName(directory)!;
        ProtectedInstallationFiles.SyncDirectory(Path.GetDirectoryName(parent)!);
        ProtectedInstallationFiles.SyncDirectory(parent);
        ProtectedInstallationFiles.SyncDirectory(directory);
    }

    private static string Stage(string stage)
    {
        if (!IsStage(stage))
        {
            throw new InvalidDataException("VoterCredentialLocalStageInvalid");
        }

        return stage;
    }

    private static bool IsStage(string stage)
    {
        return stage is "intent" or "authorization" or "artifact" or "bind-intent" or "bind-result"
            or CONTINUATION_REQUEST_RESULT or CONTINUATION_BIND_INTENT or CONTINUATION_BIND_RESULT
            or MANIFEST_STAGE or REFERENCE_STAGE or CONFIRMATION_STAGE or RETIRE_STAGE
            || IsContinuationRecoveryStage(stage)
            || stage.StartsWith(LocalVoterCredentialRetry.STAGE_PREFIX, StringComparison.Ordinal)
            && Guid.TryParseExact(stage.AsSpan(LocalVoterCredentialRetry.STAGE_PREFIX.Length), "N", out Guid operation)
            && stage == LocalVoterCredentialRetry.STAGE_PREFIX + operation.ToString("N");
    }

    internal static string ContinuationRecoveryStage(Guid boot, string kind)
    {
        return $"continuation-recovery-{boot:N}-{kind}";
    }

    private static bool IsContinuationRecoveryStage(string stage)
    {
        const string prefix = "continuation-recovery-";
        int delimiter = prefix.Length + 32;
        return stage.Length > delimiter + 1 && stage.StartsWith(prefix, StringComparison.Ordinal)
            && Guid.TryParseExact(stage.AsSpan(prefix.Length, 32), "N", out Guid boot)
            && stage[(delimiter + 1)..] is "intent" or "request" or "authorize" or "register"
            && stage == ContinuationRecoveryStage(boot, stage[(delimiter + 1)..]);
    }
}
