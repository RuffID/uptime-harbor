using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using DotNext.Net.Cluster;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Infrastructure.Cluster.Models;
using UptimeHarbor.Infrastructure.Identity;

namespace UptimeHarbor.Web.Cluster;

/// <summary>Явный офлайн-вход: файлы/подпись/проверка копии без создания builder, SQL, HTTP и hosted services.</summary>
public static class ClusterControlTool
{
    private static readonly JsonSerializerOptions JSON = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, WriteIndented = true, MaxDepth = 32
    };

    public static async Task<bool> TryExecuteAsync(string[] args)
    {
        if (args.Length == 0 || args[0] != "--cluster-control")
        {
            return false;
        }

        try
        {
            await ExecuteAsync(args);
        }
        catch
        {
            // Ни исключение, ни защищённые входные файлы не попадают в stdout/log.
            Console.Error.WriteLine("Cluster control failed; no permission to activate was issued.");
            Environment.ExitCode = 1;
        }

        return true;
    }

    private static async Task ExecuteAsync(string[] args)
    {
        if (args.Length == 4 && args[1] == "keygen")
        {
            using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            await WriteNewAsync(args[2], Encoding.ASCII.GetBytes(key.ExportPkcs8PrivateKeyPem()));
            await WriteNewAsync(args[3], Encoding.ASCII.GetBytes(key.ExportSubjectPublicKeyInfoPem()));
            Console.WriteLine(Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo())));
            return;
        }

        if (args.Length == 5 && args[1] == "sign")
        {
            byte[] payload = await File.ReadAllBytesAsync(args[2]);
            if (payload.Length > 262144)
            {
                throw new InvalidDataException("ControlLengthInvalid");
            }

            ClusterControlRequest request = JsonSerializer.Deserialize<ClusterControlRequest>(payload, JSON)
                ?? throw new InvalidDataException("ControlRequestMissing");
            if (request.Admission?.BootstrapAccount is not null)
            {
                throw new InvalidDataException("BootstrapMustBeProtected");
            }

            if (request.Isolation is not null)
            {
                request = request with { IsolationEvidenceDigest = RevisionCodec.Digest(JsonSerializer.SerializeToUtf8Bytes(request.Isolation)) };
                payload = JsonSerializer.SerializeToUtf8Bytes(request, JSON);
            }

            using ECDsa key = ECDsa.Create();
            key.ImportFromPem(await File.ReadAllTextAsync(args[3]));
            SignedClusterControl signed = new(Convert.ToBase64String(payload),
                Convert.ToBase64String(key.SignData(payload, HashAlgorithmName.SHA256)));
            await WriteNewAsync(args[4], JsonSerializer.SerializeToUtf8Bytes(signed, JSON));
            return;
        }

        if (args.Length == 5 && args[1] == "bootstrap")
        {
            RevisionCommand template = JsonSerializer.Deserialize<RevisionCommand>(await File.ReadAllBytesAsync(args[2]), JSON)
                ?? throw new InvalidDataException("BootstrapTemplateMissing");
            if (template.Action != "BootstrapAccount" || template.Value is null || template.Value.PasswordHash is not null)
            {
                throw new InvalidDataException("BootstrapTemplateInvalid");
            }

            string password = ReadPassword();
            ValidatePassword(password);
            PasswordHasher<ApplicationUser> hasher = new();
            ConfigurationValue value = template.Value with
            {
                PasswordHash = hasher.HashPassword(new(template.EntityId, template.Value.Name!), password),
                PasswordHashFormat = 1, SecurityStamp = Guid.NewGuid().ToString("N"), ConcurrencyStamp = Guid.NewGuid().ToString("N")
            };
            RevisionCommand command = template with { Value = value };
            byte[] bytes = RevisionCodec.Encode(command);
            IDataProtectionProvider provider = DataProtectionProvider.Create(new DirectoryInfo(args[3]),
                builder => builder.SetApplicationName($"UptimeHarbor.ClusterControl/{command.OriginNodeId:N}"));
            string protectedCommand = provider.CreateProtector("UptimeHarbor.Control.Bootstrap.v1").Protect(Encoding.UTF8.GetString(bytes));
            await WriteNewAsync(args[4], JsonSerializer.SerializeToUtf8Bytes(new { ProtectedBootstrapCommand = protectedCommand }, JSON));
            return;
        }

        if (args.Length == 3 && args[1] == "recover")
        {
            ClusterRecoveryToolRequest request = JsonSerializer.Deserialize<ClusterRecoveryToolRequest>(await File.ReadAllBytesAsync(args[2]), JSON)
                ?? throw new InvalidDataException("RecoveryRequestMissing");
            Console.WriteLine($"Selected committed copy: {request.Selection.PreviousClusterId}/{request.Selection.SourceIndex}. Missing changes may be lost.");
            Console.WriteLine(EmergencyRecoveryOrchestrator.LOSS_ACKNOWLEDGEMENT);
            EmergencyRecoveryOrchestrator recovery = new(new PasswordHasher<ApplicationUser>(), TimeProvider.System);
            if (Console.IsInputRedirected || Console.ReadLine() != EmergencyRecoveryOrchestrator.LOSS_ACKNOWLEDGEMENT)
            {
                await recovery.RecordDecisionAsync(request.Selection, "DeclinedOrRejected", CancellationToken.None);
                throw new InvalidDataException("ExplicitRecoveryConfirmationRequired");
            }

            PublishedRevisionSnapshot snapshot = await recovery.PrepareAsync(request.Selection, request.Target,
                request.Node, request.Credential, ReadPassword(), CancellationToken.None);
            ClusterDiskManifest manifest = new(request.Target.ClusterId, request.Target.Generation, request.Target.NodeId,
                request.Target.StorageGeneration, request.Target.PublicUri, request.Selection.RecoveryId)
            {
                RecoveryId = request.Selection.RecoveryId
            };
            await WriteNewAsync(Path.Combine(request.Target.StoragePath, "manifest.json"), JsonSerializer.SerializeToUtf8Bytes(manifest, JSON));
            Console.WriteLine($"Quarantined snapshot {snapshot.Manifest.SnapshotId}; SHA256 {snapshot.Digest}; no work enabled.");
            return;
        }

        if (args.Length == 3 && args[1] == "provision")
        {
            ClusterOptions options = JsonSerializer.Deserialize<ClusterOptions>(await File.ReadAllBytesAsync(args[2]), JSON)
                ?? throw new InvalidDataException("ProvisionOptionsMissing");
            if (options.Mode is not ("Bootstrap" or "Join") || options.ClusterId == Guid.Empty || options.Generation <= 0
                || options.NodeId == Guid.Empty || options.StorageGeneration == Guid.Empty || options.CredentialId == Guid.Empty
                || !Path.IsPathFullyQualified(options.StoragePath))
            {
                throw new InvalidDataException("ProvisionIdentityInvalid");
            }

            ClusterOperationValidation.ValidateAddress(options.PublicUri);
            if (Directory.Exists(options.StoragePath) && Directory.EnumerateFileSystemEntries(options.StoragePath).Any())
            {
                throw new InvalidDataException("ProvisionTargetMustBeEmpty");
            }

            Directory.CreateDirectory(options.StoragePath);
            Guid bootstrap = Guid.Empty;
            if (options.Mode == "Bootstrap")
            {
                bootstrap = Guid.NewGuid();
            }

            ClusterDiskManifest manifest = new(options.ClusterId, options.Generation, options.NodeId, options.StorageGeneration,
                options.PublicUri, bootstrap);
            await WriteNewAsync(Path.Combine(options.StoragePath, "manifest.json"), JsonSerializer.SerializeToUtf8Bytes(manifest, JSON));
            Console.WriteLine(ClusterMemberId.FromEndPoint(new UriEndPoint(new Uri(options.PublicUri))).ToString());
            return;
        }

        if (args.Length == 6 && args[1] == "checkpoint")
        {
            ClusterOptions options = JsonSerializer.Deserialize<ClusterOptions>(await File.ReadAllBytesAsync(args[2]), JSON)
                ?? throw new InvalidDataException("CheckpointOptionsMissing");
            PublishedRevisionSnapshot snapshot = await new RevisionSnapshotStore(Microsoft.Extensions.Options.Options.Create(options))
                .ReadAsync(args[3], args[4], CancellationToken.None);
            await WriteNewAsync(args[5], JsonSerializer.SerializeToUtf8Bytes(new
            {
                SnapshotId = snapshot.Manifest.SnapshotId, SnapshotDigest = snapshot.Digest,
                MinimumCommittedIndex = snapshot.Image.Index, MembershipVersion = snapshot.Image.Operations.MembershipVersion,
                snapshot.Image.Monitoring.Nodes, snapshot.Image.Operations
            }, JSON));
            return;
        }

        throw new ArgumentException("Expected keygen/sign/bootstrap/recover/provision/checkpoint with exact file arguments.");
    }

    private static async Task WriteNewAsync(string file, byte[] bytes)
    {
        if (!Path.IsPathFullyQualified(file))
        {
            throw new ArgumentException("AbsolutePathRequired");
        }

        await using FileStream output = new(file, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        await output.WriteAsync(bytes);
        await output.FlushAsync();
        output.Flush(true);
    }

    private static string ReadPassword()
    {
        if (Console.IsInputRedirected)
        {
            throw new InvalidOperationException("InteractivePasswordRequired");
        }

        Console.Write("Administrator password: ");
        StringBuilder password = new();
        ConsoleKeyInfo key;
        while ((key = Console.ReadKey(true)).Key != ConsoleKey.Enter)
        {
            if (key.Key == ConsoleKey.Backspace && password.Length > 0)
            {
                password.Length--;
            }
            else if (!char.IsControl(key.KeyChar) && password.Length < 1024)
            {
                password.Append(key.KeyChar);
            }
        }

        Console.WriteLine();
        return password.ToString();
    }

    private static void ValidatePassword(string password)
    {
        PasswordOptions rules = new();
        if (password.Length < rules.RequiredLength || !password.Any(char.IsUpper) || !password.Any(char.IsLower)
            || !password.Any(char.IsDigit) || password.All(char.IsLetterOrDigit))
        {
            throw new ArgumentException("PasswordPolicyFailed");
        }
    }
}
