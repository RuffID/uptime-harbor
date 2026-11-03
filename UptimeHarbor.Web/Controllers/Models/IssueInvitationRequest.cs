using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace UptimeHarbor.Web.Controllers.Models;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public record IssueInvitationRequest(Guid OperationId, Guid? ReplacesInvitationId, [Required, StringLength(1024)] string Password)
{
    public override string ToString()
    {
        return $"IssueInvitationRequest {OperationId}";
    }
}
