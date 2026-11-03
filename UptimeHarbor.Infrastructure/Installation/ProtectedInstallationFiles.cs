using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;

namespace UptimeHarbor.Infrastructure.Installation;

public static class ProtectedInstallationFiles
{
    public static string DirectoryPath(string path)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            throw new InvalidDataException("InstallationAbsolutePathRequired");
        }

        string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        DirectoryInfo? current = new(full);
        while (current is not null)
        {
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("InstallationLinkedPathForbidden");
            }

            current = current.Parent;
        }

        return full;
    }

    public static void EnsureDirectory(string path)
    {
        DirectoryPath(path);
        if (Directory.Exists(path))
        {
            VerifyPermissions(path, true);
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            DirectoryInfo directory = Directory.CreateDirectory(path);
            DirectorySecurity security = new();
            security.SetAccessRuleProtection(true, false);
            SecurityIdentifier owner = WindowsIdentity.GetCurrent().User
                ?? throw new InvalidOperationException("InstallationOwnerUnavailable");
            security.AddAccessRule(new(owner, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            directory.SetAccessControl(security);
        }
        else
        {
            Directory.CreateDirectory(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            VerifyPermissions(path, true);
        }
    }

    public static void VerifyPermissions(string path, bool directory = false)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("InstallationLinkedPathForbidden");
        }

        if (OperatingSystem.IsWindows())
        {
            FileSystemSecurity acl;
            if (directory)
            {
                acl = new DirectoryInfo(path).GetAccessControl();
            }
            else
            {
                acl = new FileInfo(path).GetAccessControl();
            }

            SecurityIdentifier owner = WindowsIdentity.GetCurrent().User!;
            SecurityIdentifier administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
            foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType == AccessControlType.Allow
                    && !rule.IdentityReference.Equals(owner) && !rule.IdentityReference.Equals(administrators))
                {
                    throw new InvalidDataException("InstallationPermissionsInvalid");
                }
            }
        }
        else
        {
            UnixFileMode expected = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            if (directory)
            {
                expected |= UnixFileMode.UserExecute;
            }

            if (File.GetUnixFileMode(path) != expected)
            {
                throw new InvalidDataException("InstallationPermissionsInvalid");
            }
        }
    }

    public static void WriteNew(string path, ReadOnlySpan<byte> bytes)
    {
        string directory = Path.GetDirectoryName(path) ?? throw new InvalidDataException("InstallationPathInvalid");
        VerifyPermissions(directory, true);
        string temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        FileStreamOptions options = new()
        {
            Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
            Options = FileOptions.WriteThrough
        };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        try
        {
            using (FileStream file = new(temporary, options))
            {
                file.Write(bytes);
                file.Flush(true);
            }

            VerifyPermissions(temporary);
            File.Move(temporary, path, false);
            SyncDirectory(directory);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public static void SyncDirectory(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        int descriptor = Open(path, 0x10000);
        if (descriptor < 0)
        {
            throw new IOException("InstallationDirectorySyncFailed");
        }

        try
        {
            if (Fsync(descriptor) != 0)
            {
                throw new IOException("InstallationDirectorySyncFailed");
            }
        }
        finally
        {
            Close(descriptor);
        }
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open(string path, int flags);
    [DllImport("libc", EntryPoint = "fsync", SetLastError = true)]
    private static extern int Fsync(int descriptor);
    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int descriptor);
}
