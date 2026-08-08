using DireControl.Api.Controllers.Models;
using DireControl.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace DireControl.Api.Controllers;

[ApiController]
[Route("api/v0/status")]
public class StatusController(
    KissConnectionHolder connectionHolder,
    IAprsIsStatusService aprsIsStatus,
    SoundModemService modemService,
    DigipeaterService digipeaterService,
    KissTcpServerService kissServer,
    AprsIsTxQueue aprsIsTxQueue,
    AprsIsReconnectTrigger reconnectTrigger) : ControllerBase
{
    [HttpGet]
    public ActionResult<StatusDto> GetStatus()
    {
        var s = aprsIsStatus;
        return Ok(new StatusDto
        {
            DirewolfConnected = connectionHolder.IsConnected,
            DigipeatedFrames = digipeaterService.DigipeatedFrames,
            KissServerClients = kissServer.ClientCount,
            RfToIsGatedLines = aprsIsTxQueue.SentCount,
            AprsIsState = s.State.ToString(),
            AprsIsServerName = s.ServerName,
            AprsIsFilter = s.ActiveFilter,
            AprsIsSessionPacketCount = s.SessionPacketCount,
            AprsIsFirstDisconnectedAt = s.FirstDisconnectedAt,
            AprsIsLastConnectAttemptAt = s.LastConnectAttemptAt,
            AprsIsFailedAttempts = s.FailedAttempts,
            AprsIsLastError = s.LastError,
            ModemState = modemService.AggregateState,
            ModemCarrierDetected = modemService.AnyCarrierDetected,
        });
    }

    /// <summary>
    /// Forces the APRS-IS background service to drop any current connection and
    /// attempt to reconnect immediately, interrupting the normal retry delay (and
    /// the indefinite wait that follows an auth failure).
    /// </summary>
    [HttpPost("aprs-is/reconnect")]
    public ActionResult ReconnectAprsIs()
    {
        reconnectTrigger.Trigger();
        return NoContent();
    }
}
