using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using UptimeHarbor.Application.Cluster;
using UptimeHarbor.Application.Cluster.Models;
using UptimeHarbor.Infrastructure.Cluster;

namespace UptimeHarbor.Web.Cluster;

[AllowAnonymous]
[Route("internal/cluster/v1/commands")]
public class ClusterRevisionController(RevisionDispatcher dispatcher, IOptions<ClusterOptions> options) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> SubmitAsync(CancellationToken cancellationToken)
    {
        if (HttpContext.Items[typeof(PeerEnvelope)] is not PeerEnvelope peer)
        {
            return StatusCode(StatusCodes.Status403Forbidden);
        }

        try
        {
            byte[] bytes = await RevisionBatchCodec.ReadAsync(Request.Body, options.Value.BatchMaxBytes, cancellationToken);
            IReadOnlyList<RevisionCommand> commands = RevisionBatchCodec.Decode(bytes, options.Value);
            if (commands.Any(command => command.OriginNodeId != peer.FromNodeId
                || command.Monitoring is not null && command.Monitoring.StorageGeneration != peer.StorageGeneration
                || command.Delivery is not null && command.Delivery.StorageGeneration != peer.StorageGeneration))
            {
                return StatusCode(StatusCodes.Status403Forbidden);
            }

            List<CommandResult> results = [];
            foreach (RevisionCommand command in commands)
            {
                results.Add(await dispatcher.AcceptAsync(command, peer.FromNodeId, cancellationToken));
            }

            return new JsonResult(results);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException or InvalidDataException or ArgumentException)
        {
            return StatusCode(StatusCodes.Status400BadRequest);
        }
        catch (Exception exception) when (exception is InvalidOperationException or UnauthorizedAccessException or OperationCanceledException)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable);
        }
    }
}
