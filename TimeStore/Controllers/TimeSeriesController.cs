using Microsoft.AspNetCore.Mvc;
using TimeStore.Core.Database;
using TimeStore.Core.Raft;

namespace TimeStore.Controllers;

/// <summary>
/// API controller for time-series data operations.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class TimeSeriesController : ControllerBase
{
    private readonly IRaftService _raftService;
    private readonly ILogger<TimeSeriesController> _logger;

    public TimeSeriesController(IRaftService raftService, ILogger<TimeSeriesController> logger)
    {
        _raftService = raftService;
        _logger = logger;
    }

    /// <summary>
    /// Gets all time-series data points.
    /// </summary>
    [HttpGet]
    public ActionResult<IEnumerable<Data>> GetAll()
    {
        _logger.LogInformation("Getting all time-series data");
        var data = _raftService.GetTimeSeriesData();
        return Ok(data);
    }

    /// <summary>
    /// Adds a new time-series data point.
    /// If this node is not the leader, it will redirect to the leader.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<Data>> Add([FromBody] Data data)
    {
        _logger.LogInformation("Adding new time-series data point for device {DeviceId}", data.DeviceId);
        
        var result = await _raftService.AddTimeSeriesDataAsync(data);
        
        if (result.success)
        {
            return Ok(data);
        }
        else if (result.leaderId != null)
        {
            // Return a redirect to the leader
            return StatusCode(StatusCodes.Status307TemporaryRedirect, new { LeaderId = result.leaderId });
        }
        else
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "No leader available");
        }
    }

    /// <summary>
    /// Gets time-series data for a specific device.
    /// </summary>
    [HttpGet("device/{deviceId}")]
    public ActionResult<IEnumerable<Data>> GetByDevice(string deviceId)
    {
        _logger.LogInformation("Getting time-series data for device {DeviceId}", deviceId);
        var data = _raftService.GetTimeSeriesData()
            .Where(d => d.DeviceId == deviceId)
            .ToList();
        
        return Ok(data);
    }
}
