using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using UptimeHarbor.Infrastructure.Cluster.Onboarding.Renewal;
using UptimeHarbor.Web.Cluster.EnrollmentRouting;

namespace UptimeHarbor.Web.Controllers;

[ApiController]
[AllowAnonymous]
public sealed class VoterContinuationController : ControllerBase
{
    [HttpPost(VoterContinuationEndpoint.ROOT + "/nonce")]
    [HttpPost(VoterContinuationEndpoint.ROOT + "/request")]
    [HttpPost(VoterContinuationEndpoint.ROOT + "/status")]
    [HttpPost(VoterContinuationEndpoint.ROOT + "/register")]
    [HttpPost(VoterContinuationEndpoint.ROOT + "/issue")]
    [HttpPost(VoterContinuationEndpoint.ROOT + "/bind")]
    [HttpPost(VoterContinuationEndpoint.ROOT + "/" + VoterContinuationAuthority.SLICE)]
    public Task ExchangeAsync()
    {
        return VoterContinuationEndpoint.HandleAsync(HttpContext);
    }
}
