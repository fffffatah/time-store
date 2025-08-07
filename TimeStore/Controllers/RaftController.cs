using Microsoft.AspNetCore.Mvc;
using TimeStore.Core.Raft;
using TimeStore.Dtos;

namespace TimeStore.Controllers;

/// <summary>
/// API controller for Raft consensus protocol communication between nodes.
/// </summary>
[ApiController]
[Route("raft")]
public class RaftController(IRaftService raftService, ILogger<RaftController> logger) : ControllerBase
{
    /// <summary>
    /// Handles vote requests from candidate nodes.
    /// </summary>
    [HttpPost("vote")]
    public async Task<ActionResult<RequestVoteResponse>> Vote([FromBody] RequestVoteRequest request)
    {
        var response = await raftService.HandleRequestVoteAsync(request);
        
        return Ok(response);
    }

    /// <summary>
    /// Handles append entries requests from the leader node.
    /// This is the main replication mechanism in Raft.
    /// </summary>
    [HttpPost("append")]
    public async Task<ActionResult<AppendEntriesResponse>> AppendEntries([FromBody] AppendEntriesRequest request)
    {
        var response = await raftService.HandleAppendEntriesAsync(request);
        
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
            NodeId = raftService.MyId,
            State = raftService.State.ToString(),
            LeaderId = raftService.LeaderId
        });
    }
}
