using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace UptimeHarbor.Web.Controllers.Models;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public record SetupPrepareRequest([Required, StringLength(16)] string Mode, long ExpectedRevision,
    long ExpectedGeneration, [StringLength(256)] string? CommonLogin,
    [StringLength(1024)] string? CommonPassword)
{
    public override string ToString()
    {
        return "SetupPrepareRequest";
    }
}
