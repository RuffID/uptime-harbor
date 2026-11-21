using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace UptimeHarbor.Web.Controllers.Models;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public record ConfirmVoterContinuationRequest(
    [Required, RegularExpression("[0-9A-F]{64}")] string RequestDigest,
    [Required, StringLength(1024)] string Password)
{
    public override string ToString()
    {
        return nameof(ConfirmVoterContinuationRequest);
    }
}
