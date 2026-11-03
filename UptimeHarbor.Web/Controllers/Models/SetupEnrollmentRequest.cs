using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace UptimeHarbor.Web.Controllers.Models;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public record SetupEnrollmentRequest([Required, StringLength(2048)] string BrowserOrigin,
    [StringLength(36)] string? Invitation)
{
    public override string ToString()
    {
        return "SetupEnrollmentRequest";
    }
}
