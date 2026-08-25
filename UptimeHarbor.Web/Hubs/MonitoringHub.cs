using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace UptimeHarbor.Web.Hubs;

[Authorize]
public class MonitoringHub : Hub;
