using System.ComponentModel.DataAnnotations;

namespace UptimeHarbor.Web.Controllers.Models;

public class CreateUserRequest
{
    [Required]
    public string Login { get; init; } = string.Empty;

    [Required]
    public string InitialPassword { get; init; } = string.Empty;
}
