using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Application.Cluster.Onboarding;
using UptimeHarbor.Application.Cluster.Onboarding.Renewal;

namespace UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;

/// <summary>Закрепляет только DER, ранее сохранённые выдачей продолжения.</summary>
public class VoterContinuationBind(VoterContinuationAuthority authority, IEnrollmentSigningUnitOfWork work,
    ILogger<VoterContinuationBind> logger)
{
    public async Task<CommandResult> BindAsync(VoterCredentialProofRequest call, string registrationDigest,
        string certificateDigest, CancellationToken token)
    {
        try
        {
            return await authority.BindAsync(call, registrationDigest, certificateDigest, work, token);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or IOException or InvalidDataException or ArgumentException
            or FormatException or CryptographicException or InvalidOperationException)
        {
            logger.LogWarning("Credential continuation Bind denied or unconfirmed for {RequestId} {IssueId}.",
                call.Request.OperationId, call.Request.Renewal.Issuance.IssueId);
            throw;
        }
    }
}
