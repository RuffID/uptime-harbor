using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Common;
using UptimeHarbor.Application.Installation;
using UptimeHarbor.Application.Users;
using UptimeHarbor.Domain.Cluster;
using UptimeHarbor.Infrastructure.Cluster;
using UptimeHarbor.Application.Cluster.Onboarding;

namespace UptimeHarbor.Infrastructure.Installation;

/// <summary>Только локальный E0. Ни native Bootstrap, ни сеть не вызываются до durable intent.</summary>
public class InstallationPreparation(ILocalSetupUnitOfWork work, InstallationState state,
    SetupAccountLogin login, SetupPreflight preflight, LocalSecretStore secrets,
    IAccountCredentialService credentials, IOptions<InstallationOptions> options,
    TimeProvider time, ILogger<InstallationPreparation> logger, ISetupBotIdentity botIdentity,
    IEnrollmentSigningRepository? signing = null)
{
    public async Task<ServiceResult<SetupStatus>> PrepareAsync(SetupSession session,
        SetupPreparationRequest request, CancellationToken token)
    {
        await state.Transition.WaitAsync(token);
        try
        {
            if (!await login.ValidateAsync(session, token))
            {
                return ServiceResult<SetupStatus>.Fail(ServiceErrorType.Unauthorized, "Требуется локальный вход.");
            }

            LocalInstallationProfile profile = await work.Profiles.ReadAsync(token)
                ?? throw new InvalidDataException("InstallationProfileMissing");
            IReadOnlyList<EnrollmentIssuerReference>? issuers = null;
            if (signing is not null)
            {
                issuers = await signing.ListIssuersAsync(token);
            }

            secrets.ReconcileAll(profile, issuers);
            if (profile.AttemptId.HasValue)
            {
                if (profile.Mode != request.Mode || !MatchesPreparedRequest(profile, request))
                {
                    return ServiceResult<SetupStatus>.Fail(ServiceErrorType.Conflict, "Уже сохранена другая попытка настройки.");
                }

                secrets.Reconcile(profile.AttemptId.Value, profile.KeyReference);
                return ServiceResult<SetupStatus>.Ok(Status(profile));
            }

            if (profile.ProfileRevision != request.ExpectedRevision || profile.RuntimeGeneration != request.ExpectedGeneration
                || profile.Phase != InstallationPhase.SetupReady || request.Mode is not ("Bootstrap" or "Join"))
            {
                return ServiceResult<SetupStatus>.Fail(ServiceErrorType.Conflict, "Состояние изменилось. Обновите сведения.");
            }

            ServiceResult check = await preflight.CheckAsync(profile, token);
            if (!check.Success)
            {
                logger.LogWarning("{EventName} {InstallIdentity} {Reason}", "SetupPreflightRejected", profile.InstallIdentity, check.Error!.Message);
                return ServiceResult<SetupStatus>.Fail(check.Error!);
            }

            Guid attempt = Guid.NewGuid();
            Guid node = Guid.NewGuid();
            Guid storage = Guid.NewGuid();
            Guid cluster = Guid.NewGuid();
            ImmutableDictionary<string, Guid> operations = new Dictionary<string, Guid>
            {
                ["prepare"] = Guid.NewGuid(), ["reserve"] = Guid.NewGuid(), ["bootstrap"] = Guid.NewGuid(),
                ["admit"] = Guid.NewGuid(), ["membership"] = Guid.NewGuid(), ["activation"] = Guid.NewGuid(), ["complete"] = Guid.NewGuid()
            }.ToImmutableDictionary();
            string? accountReference = null;
            string? accountDigest = null;
            long botId = 0;
            if (request.Mode == "Bootstrap")
            {
                ServiceResult<long> bot = await botIdentity.ReadAsync(token);
                if (!bot.Success)
                {
                    return ServiceResult<SetupStatus>.Fail(bot.Error!);
                }

                botId = bot.Data;

                if (string.IsNullOrWhiteSpace(request.CommonLogin) || string.IsNullOrWhiteSpace(request.CommonPassword))
                {
                    return ServiceResult<SetupStatus>.Fail(ServiceErrorType.Validation, "Укажите нового общего администратора.");
                }

                Guid userId = Guid.NewGuid();
                ServiceResult<ProtectedCredential> prepared = await credentials.PrepareAsync(userId,
                    request.CommonLogin, request.CommonPassword, token);
                if (!prepared.Success)
                {
                    return ServiceResult<SetupStatus>.Fail(prepared.Error!);
                }

                ProtectedCredential credential = prepared.Data!;
                RevisionCommand account = new()
                {
                    ClusterId = cluster, ClusterGeneration = 1, OperationId = operations["bootstrap"],
                    OriginNodeId = node, ActorUserId = userId, ActorRevision = 1, EntityId = userId,
                    EntityKind = DeletedEntityKind.Account, Action = "BootstrapAccount", BaseRevision = 0,
                    CreatedAtUtc = time.GetUtcNow(), Value = new ConfigurationValue
                    {
                        Name = request.CommonLogin.Normalize(NormalizationForm.FormKC).Trim(), RoleId = Guid.NewGuid(),
                        PasswordHash = credential.PasswordHash, PasswordHashFormat = credential.FormatVersion,
                        SecurityStamp = credential.SecurityStamp, ConcurrencyStamp = credential.ConcurrencyStamp
                    }
                };
                byte[] bytes = RevisionCodec.Encode(account);
                accountDigest = RevisionCodec.Digest(account);
                try
                {
                    accountReference = secrets.Write(attempt, "bootstrap", bytes);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(bytes);
                }
            }
            else if (request.CommonLogin is not null || request.CommonPassword is not null)
            {
                return ServiceResult<SetupStatus>.Fail(ServiceErrorType.Validation, "Присоединение не создаёт общего администратора и не задаёт бота.");
            }

            using ECDsa key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            byte[] pem = Encoding.UTF8.GetBytes(key.ExportPkcs8PrivateKeyPem());
            string keyReference;
            try
            {
                keyReference = secrets.Write(attempt, "peer", pem);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(pem);
            }

            string spkiDigest = Convert.ToHexString(SHA256.HashData(key.ExportSubjectPublicKeyInfo()));
            string canonical = $"LocalPrepare/v1/{profile.InstallIdentity:N}/{attempt:N}/{request.Mode}/{node:N}/{storage:N}/{options.Value.PeerEndpoint}/{spkiDigest}/{accountDigest}/{botId}";
            string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
            LocalInstallationProfile next = profile with
            {
                Mode = request.Mode, Phase = InstallationPhase.Preparing, ProfileRevision = profile.ProfileRevision + 1,
                AttemptId = attempt, NodeId = node, StorageGeneration = storage, PeerAddress = options.Value.PeerEndpoint,
                KeyReference = keyReference, RequestDigest = digest, OperationIds = operations,
                BootstrapAccountReference = accountReference, BotId = botId, BootstrapAccountDigest = accountDigest
            };
            if (request.Mode == "Bootstrap")
            {
                next = next with { ClusterId = cluster, ClusterGeneration = 1 };
            }

            await work.ExecuteInTransactionAsync(async cancellation =>
            {
                await work.Profiles.ReplaceAsync(next, profile.ProfileRevision, cancellation);
                work.Profiles.AddStep(new(operations["prepare"], attempt, "LocalPreparation", digest,
                    "AuthorityRequired", next.ProfileRevision, next.RuntimeGeneration, time.GetUtcNow()));
            }, token);
            state.Publish(next);
            logger.LogInformation("{EventName} {InstallIdentity} {ActorId} {AttemptId} {Phase} {Revision} {Generation}",
                "RuntimeTransitionStarted", next.InstallIdentity, session.AccountId, attempt, next.Phase, next.ProfileRevision, next.RuntimeGeneration);
            return ServiceResult<SetupStatus>.Ok(Status(next));
        }
        finally
        {
            state.Transition.Release();
        }
    }

    public static SetupStatus Status(LocalInstallationProfile profile, bool commonAccessOpen = false)
    {
        return new(profile.Phase.ToString(), profile.Mode, profile.ProfileRevision,
            profile.RuntimeGeneration, profile.AttemptId, profile.ErrorCode, commonAccessOpen);
    }

    private bool MatchesPreparedRequest(LocalInstallationProfile profile, SetupPreparationRequest request)
    {
        if (profile.Mode == "Join")
        {
            return request.CommonLogin is null && request.CommonPassword is null;
        }

        if (profile.BootstrapAccountReference is null || request.CommonPassword is null || request.CommonLogin is null)
        {
            return false;
        }

        byte[] encoded = secrets.Read(profile.AttemptId!.Value, "bootstrap", profile.BootstrapAccountReference);
        try
        {
            RevisionCommand account = RevisionCodec.Decode(encoded);
            ConfigurationValue value = account.Value!;
            if (value.Name != request.CommonLogin.Normalize(NormalizationForm.FormKC).Trim())
            {
                return false;
            }

            ProtectedCredential credential = new(value.PasswordHash!, value.PasswordHashFormat,
                value.SecurityStamp!, value.ConcurrencyStamp!);
            return credentials.Verify(account.EntityId, value.Name, credential, request.CommonPassword);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encoded);
        }
    }
}
