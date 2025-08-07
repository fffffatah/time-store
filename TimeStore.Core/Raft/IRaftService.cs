using TimeStore.Core.Database.Entities;

namespace TimeStore.Core.Raft;

/// <summary>
/// Interface for the Raft consensus service implementation.
/// </summary>
public interface IRaftService
{
    /// <summary>
    /// Gets the current state of the node (Follower, Candidate, or Leader).
    /// </summary>
    RaftStateEnum State { get; }
    
    /// <summary>
    /// Gets the ID of this node.
    /// </summary>
    string MyId { get; }
    
    /// <summary>
    /// Gets the ID of the current leader node, if known.
    /// </summary>
    string? LeaderId { get; }
    
    /// <summary>
    /// Handles a RequestVote call from a candidate.
    /// </summary>
    Task<RequestVoteResponse> HandleRequestVoteAsync(RequestVoteRequest request);
    
    /// <summary>
    /// Handles an AppendEntries call from the leader.
    /// </summary>
    Task<AppendEntriesResponse> HandleAppendEntriesAsync(AppendEntriesRequest request);
    
    /// <summary>
    /// Adds new time-series data to the distributed database.
    /// Clients should call this method on any node, which will forward to the leader if needed.
    /// </summary>
    /// <param name="data">The time-series data to add</param>
    /// <returns>A tuple with success status and optionally the leader ID if redirected</returns>
    Task<(bool success, string? leaderId)> AddTimeSeriesDataAsync(Data data);
    
    /// <summary>
    /// Reads all time-series data from the node's local database.
    /// </summary>
    /// <returns>A list of all time-series data points</returns>
    List<Data> GetTimeSeriesData();
    
    /// <summary>
    /// Starts the Raft service.
    /// </summary>
    Task StartAsync();
    
    /// <summary>
    /// Stops the Raft service.
    /// </summary>
    Task StopAsync();
}