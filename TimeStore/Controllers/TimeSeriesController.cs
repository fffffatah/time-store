using Microsoft.AspNetCore.Mvc;
using TimeStore.Core.Database.Entities;
using TimeStore.Core.Raft;

namespace TimeStore.Controllers;

/// <summary>
/// API controller for time-series data operations.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class TimeSeriesController(IRaftService raftService)
    : ControllerBase
{
    /// <summary>
    /// Gets all time-series data points.
    /// </summary>
    [HttpGet]
    public ActionResult<IEnumerable<Data>> GetAll()
    {
        var data = raftService.GetTimeSeriesData();
        
        return Ok(data);
    }

    /// <summary>
    /// Adds a new time-series data point.
    /// If this node is not the leader, it will redirect to the leader.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<Data>> Add([FromBody] Data data)
    {
        var result = await raftService.AddTimeSeriesDataAsync(data);

        if (result is not { success: true, leaderId: not null })
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "No leader available");
        }

        return Ok(data);

    }

    /// <summary>
    /// Gets time-series data for a specific device.
    /// </summary>
    [HttpGet("device/{deviceId}")]
    public ActionResult<IEnumerable<Data>> GetByDevice(string deviceId)
    {
        var data = raftService.GetTimeSeriesData()
            .Where(d => d.DeviceId == deviceId)
            .ToList();
        
        return Ok(data);
    }
}
