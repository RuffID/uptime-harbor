using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using UptimeHarbor.Application.Security;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;
using UptimeHarbor.Infrastructure.Installation;
using UptimeHarbor.Web.Security;

namespace UptimeHarbor.Web.Cluster.EnrollmentRouting;

/// <summary>Актор подтверждения берётся из проверенного общего cookie ticket.</summary>
internal sealed class VoterContinuationAdministrator(IHttpContextAccessor context, InstallationState installation)
    : IVoterContinuationAdministrator
{
    public Guid? CommonActorId
    {
        get
        {
            AuthenticationTicket? ticket = context.HttpContext?.Features
                .Get<IAuthenticateResultFeature>()?.AuthenticateResult?.Ticket;
            if (!installation.CommonOpen || ticket?.AuthenticationScheme != IdentityConstants.ApplicationScheme
                || ticket.Principal.Identity is not { IsAuthenticated: true } identity
                || identity.AuthenticationType == SetupAuthentication.SCHEME
                || !ticket.Principal.IsInRole(ApplicationRoles.ADMINISTRATOR))
            {
                return null;
            }

            string? subject = ticket.Principal.FindFirstValue(ClaimTypes.NameIdentifier);
            if (!Guid.TryParse(subject, out Guid actor) || actor == Guid.Empty)
            {
                return null;
            }

            return actor;
        }
    }
}
