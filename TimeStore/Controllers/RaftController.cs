using Microsoft.AspNetCore.Mvc;
using TimeStore.Core.Database;
using TimeStore.Core.Raft;

namespace TimeStore.Controllers;

/// <summary>
/// API controller for Raft consensus protocol communication between nodes.
/// </summary>
[ApiController]
[Route("raft")]
public class RaftController : ControllerBase
{
    private readonly IRaftService _raftService;
    private readonly ILogger<RaftController> _logger;

    public RaftController(IRaftService raftService, ILogger<RaftController> logger)
    {
        _raftService = raftService;
        _logger = logger;
    }

    /// <summary>
    /// Handles vote requests from candidate nodes.
    /// </summary>
    [HttpPost("vote")]
    public async Task<ActionResult<RequestVoteResponse>> Vote([FromBody] RequestVoteRequest request)
    {
        _logger.LogInformation("Received vote request from candidate {CandidateId}", request.CandidateId);
        var response = await _raftService.HandleRequestVoteAsync(request);
        return Ok(response);
    }

    /// <summary>
    /// Handles append entries requests from the leader node.
    /// This is the main replication mechanism in Raft.
    /// </summary>
    [HttpPost("append")]
    public async Task<ActionResult<AppendEntriesResponse>> AppendEntries([FromBody] AppendEntriesRequest request)
    {
        _logger.LogInformation("Received append entries request from leader {LeaderId}", request.LeaderId);
        var response = await _raftService.HandleAppendEntriesAsync(request);
        return Ok(response);
    }

    /// <summary>
    /// Gets information about the current node's state.
    /// </summary>
    [HttpGet("status")]
    public ActionResult<RaftStatus> GetStatus()
    {
        return Ok(new RaftStatus
        {
            NodeId = _raftService.MyId,
            State = _raftService.State.ToString(),
            LeaderId = _raftService.LeaderId
        });
    }
}

/// <summary>
/// Represents the status of a Raft node.
/// </summary>
public class RaftStatus
{
    public string NodeId { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
    public string? LeaderId { get; set; }
}
