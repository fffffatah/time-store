using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TimeStore.Core.Database;

namespace TimeStore.Core.Raft;

/// <summary>
/// Implementation of the Raft consensus algorithm for distributed consensus.
/// </summary>
public class RaftService : IRaftService
{
    private readonly string _myId;
    private readonly List<string> _peers;
    private readonly SqlitePersistenceService _persistence;
    private readonly HttpClient _httpClient;
    private readonly ILogger<RaftService> _logger;
    private readonly CancellationTokenSource _cancellationTokenSource = new CancellationTokenSource();

    private RaftStateEnum _state = RaftStateEnum.Follower;
    private long _currentTerm = 0;
    private string? _votedFor = null;
    private Timer? _electionTimer;
    private Timer? _heartbeatTimer;
    private long _commitIndex = 0;
    private long _lastApplied = 0;
    private string? _leaderId = null;

    // Leader-specific state
    private readonly Dictionary<string, long> _nextIndex = new();
    private readonly Dictionary<string, long> _matchIndex = new();

    public RaftService(string myId, IEnumerable<string> allPeers, SqlitePersistenceService persistence, HttpClient httpClient, ILogger<RaftService> logger)
    {
        _myId = myId;
        _persistence = persistence;
        _httpClient = httpClient;
        _logger = logger;

        _peers = allPeers.Where(p => p != myId).ToList();

        _logger.LogInformation("Initialized Raft service for node {NodeId} with peers: {Peers}", 
            _myId, string.Join(", ", _peers));
    }
    
    public RaftStateEnum State => _state;
    public string MyId => _myId;
    public string? LeaderId => _leaderId;

    /// <summary>
    /// Starts the Raft service.
    /// </summary>
    public async Task StartAsync()
    {
        _logger.LogInformation("Starting Raft service for node {NodeId}", _myId);

        // Load persistent state from DB on startup
        var persistedState = _persistence.GetRaftState();
        _currentTerm = persistedState.CurrentTerm;
        _votedFor = persistedState.VotedFor;
        
        // Initialize timers
        _electionTimer = new Timer(async _ => await StartElection(), null, GetNewElectionTimeout(), Timeout.Infinite);
        _heartbeatTimer = new Timer(async _ => await SendHeartbeat(), null, Timeout.Infinite, Timeout.Infinite);
        
        _logger.LogInformation("Raft service started for node {NodeId} in term {Term}, state: {State}", 
            _myId, _currentTerm, _state);
    }

    /// <summary>
    /// Stops the Raft service.
    /// </summary>
    public async Task StopAsync()
    {
        _logger.LogInformation("Stopping Raft service for node {NodeId}", _myId);
        
        // Dispose timers
        _electionTimer?.Dispose();
        _heartbeatTimer?.Dispose();
        
        // Cancel any pending operations
        _cancellationTokenSource.Cancel();
        
        _logger.LogInformation("Raft service stopped for node {NodeId}", _myId);
    }

    // =============================================================================
    // Public API for Raft RPCs
    // =============================================================================
    
    /// <summary>
    /// Handles a RequestVote RPC call from a candidate.
    /// </summary>
    public async Task<RequestVoteResponse> HandleRequestVoteAsync(RequestVoteRequest request)
    {
        _logger.LogInformation("Received vote request from {CandidateId} for term {Term}", request.CandidateId, request.Term);
        
        // Rule 1: If request term < currentTerm, reply false
        if (request.Term < _currentTerm)
        {
            return new RequestVoteResponse(_currentTerm, false);
        }

        // Rule 2: If request term > currentTerm, become follower and update term
        if (request.Term > _currentTerm)
        {
            await BecomeFollower(request.Term);
        }

        // Rule 3: If votedFor is null or candidateId, and candidate's log is at least as up-to-date as receiver's, grant vote
        var lastLog = _persistence.GetLastLogEntry();
        bool isLogUpToDate = (request.LastLogTerm > lastLog.Term) ||
                             (request.LastLogTerm == lastLog.Term && request.LastLogIndex >= _persistence.GetLogEntries().Count);

        if ((_votedFor == null || _votedFor == request.CandidateId) && isLogUpToDate)
        {
            _votedFor = request.CandidateId;
            await _persistence.UpdateRaftState(_currentTerm, _votedFor);
            _electionTimer?.Change(GetNewElectionTimeout(), Timeout.Infinite);
            _logger.LogInformation("Voting for {CandidateId}", request.CandidateId);
            return new RequestVoteResponse(_currentTerm, true);
        }
        
        return new RequestVoteResponse(_currentTerm, false);
    }

    /// <summary>
    /// Handles an AppendEntries RPC from the leader.
    /// Also serves as a heartbeat.
    /// </summary>
    public async Task<AppendEntriesResponse> HandleAppendEntriesAsync(AppendEntriesRequest request)
    {
        _logger.LogInformation("Received AppendEntries from {LeaderId} for term {Term} with {EntryCount} entries", 
            request.LeaderId, request.Term, request.Entries.Count);
        
        // Rule 1: If request term < currentTerm, reply false
        if (request.Term < _currentTerm)
        {
            return new AppendEntriesResponse(_currentTerm, false);
        }
        
        // Rule 2: If request term >= currentTerm, become follower and reset election timer
        await BecomeFollower(request.Term);
        _leaderId = request.LeaderId;
        _electionTimer?.Change(GetNewElectionTimeout(), Timeout.Infinite);

        // Rule 3: Check for log consistency
        var localLog = _persistence.GetLogEntries();
        if (request.PrevLogIndex > 0 && 
            (request.PrevLogIndex > localLog.Count || 
             localLog[(int)request.PrevLogIndex - 1].Term != request.PrevLogTerm))
        {
            _logger.LogWarning("Log inconsistency at index {Index}", request.PrevLogIndex);
            return new AppendEntriesResponse(_currentTerm, false);
        }
        
        // Rule 4: Append new entries and truncate inconsistencies
        if (request.Entries.Any())
        {
            long indexToStart = request.PrevLogIndex;
            if (indexToStart < localLog.Count)
            {
                // Truncate inconsistent entries
                _persistence.DeleteLogsFromIndex(indexToStart + 1);
            }
            
            // Append new entries
            foreach (var entry in request.Entries)
            {
                _persistence.AppendLogEntry(entry);
            }
            
            _logger.LogInformation("Appended {Count} entries to log", request.Entries.Count);
        }
        
        // Rule 5: Update commit index
        if (request.LeaderCommit > _commitIndex)
        {
            _commitIndex = Math.Min(request.LeaderCommit, _persistence.GetLogEntries().Count);
            await ApplyLogsToStateMachine();
        }

        return new AppendEntriesResponse(_currentTerm, true);
    }
    
    /// <summary>
    /// A client-facing method to add new time-series data.
    /// As a follower, it will redirect to the leader.
    /// As a leader, it will append to its log and replicate.
    /// </summary>
    public async Task<(bool success, string? leaderId)> AddTimeSeriesDataAsync(Data data)
    {
        if (_state != RaftStateEnum.Leader)
        {
            _logger.LogInformation("Redirecting client request to leader: {LeaderId}", _leaderId);
            return (false, _leaderId);
        }

        _logger.LogInformation("Leader received new data. Appending to log.");
        
        var command = JsonSerializer.Serialize(data);
        var logEntry = new RaftLogEntry(_currentTerm, command);
        
        // Append entry to local log
        _persistence.AppendLogEntry(logEntry);
        
        // Replicate the log entry to followers
        await ReplicateLogs();
        
        // Wait for a majority of replications before committing
        var replicationCount = 1; // Count ourselves
        foreach (var peer in _peers)
        {
            if (_matchIndex[peer] >= _persistence.GetLogEntries().Count)
            {
                replicationCount++;
            }
        }
        
        // If we have a majority, commit the entry
        if (replicationCount > RaftConstants.ClusterSize / 2)
        {
            _commitIndex = _persistence.GetLogEntries().Count;
            await ApplyLogsToStateMachine();
            return (true, null);
        }
        
        _logger.LogWarning("Failed to replicate entry to a majority of nodes");
        return (false, null);
    }
    
    /// <summary>
    /// Reads all time-series data from the node's local database.
    /// </summary>
    public List<Data> GetTimeSeriesData()
    {
        return _persistence.GetAllTimeSeriesData();
    }
    
    // =============================================================================
    // Raft State Transitions and Background Tasks
    // =============================================================================

    /// <summary>
    /// Called when the election timer fires. A follower becomes a candidate.
    /// </summary>
    private async Task StartElection()
    {
        if (_state == RaftStateEnum.Leader) return;

        _logger.LogInformation("{NodeId} election timer timed out. Starting new election.", _myId);
        
        // Increment term and become a candidate
        _currentTerm++;
        _state = RaftStateEnum.Candidate;
        _votedFor = _myId; // Vote for self
        await _persistence.UpdateRaftState(_currentTerm, _votedFor);
        
        // Reset election timer
        _electionTimer?.Change(GetNewElectionTimeout(), Timeout.Infinite);

        // Send RequestVote RPCs to all other servers
        var lastLog = _persistence.GetLastLogEntry();
        var request = new RequestVoteRequest(
            _currentTerm,
            _myId,
            _persistence.GetLogEntries().Count,
            lastLog.Term
        );
        
        var votesReceived = 1; // Vote for self
        
        foreach (var peer in _peers)
        {
            try
            {
                var response = await _httpClient.PostAsJsonAsync($"{peer}/raft/vote", request, _cancellationTokenSource.Token);
                if (response.IsSuccessStatusCode)
                {
                    var voteResponse = await response.Content.ReadFromJsonAsync<RequestVoteResponse>();
                    if (voteResponse?.VoteGranted == true && voteResponse.Term == _currentTerm)
                    {
                        votesReceived++;
                    }
                    else if (voteResponse?.Term > _currentTerm)
                    {
                        // Higher term found, revert to follower
                        await BecomeFollower(voteResponse.Term);
                        return;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Could not reach peer {Peer}: {Message}", peer, ex.Message);
            }
        }
        
        // If a majority of votes are received, become the leader
        if (votesReceived > RaftConstants.ClusterSize / 2)
        {
            await BecomeLeader();
        }
        else
        {
            // Election failed, wait for another timeout
            _logger.LogInformation("Election failed for term {Term}. Restarting timer.", _currentTerm);
        }
    }
    
    /// <summary>
    /// Transitions the node to a Follower state.
    /// </summary>
    private async Task BecomeFollower(long term)
    {
        if (_state != RaftStateEnum.Follower || term > _currentTerm)
        {
            _logger.LogInformation("Transitioning to Follower for term {Term}", term);
            _state = RaftStateEnum.Follower;
            _currentTerm = term;
            _votedFor = null;
            await _persistence.UpdateRaftState(term, null);
            _heartbeatTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            _electionTimer?.Change(GetNewElectionTimeout(), Timeout.Infinite);
        }
    }
    
    /// <summary>
    /// Transitions the node to a Leader state.
    /// </summary>
    private async Task BecomeLeader()
    {
        _logger.LogInformation("Node {NodeId} becoming Leader for term {Term}!", _myId, _currentTerm);
        _state = RaftStateEnum.Leader;
        _leaderId = _myId;
        _electionTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        
        // Re-initialize nextIndex and matchIndex for all followers
        var lastLogIndex = _persistence.GetLogEntries().Count;
        foreach (var peer in _peers)
        {
            _nextIndex[peer] = lastLogIndex + 1;
            _matchIndex[peer] = 0;
        }

        // Start sending heartbeats immediately
        await SendHeartbeat();
        _heartbeatTimer?.Change(RaftConstants.HeartbeatInterval, RaftConstants.HeartbeatInterval);
    }
    
    /// <summary>
    /// The leader's heartbeat and log replication mechanism.
    /// </summary>
    private async Task SendHeartbeat()
    {
        if (_state != RaftStateEnum.Leader) return;

        foreach (var peer in _peers)
        {
            try
            {
                var nextIndex = _nextIndex.GetValueOrDefault(peer, 1);
                var prevLogIndex = nextIndex - 1;
                var localLog = _persistence.GetLogEntries();
                var prevLogTerm = prevLogIndex > 0 && prevLogIndex <= localLog.Count 
                    ? localLog[(int)prevLogIndex - 1].Term 
                    : 0;
                
                var entries = new List<RaftLogEntry>();
                
                // If we have entries to send, include them
                if (nextIndex <= localLog.Count)
                {
                    entries = localLog.Skip((int)nextIndex - 1).ToList();
                }
                
                var request = new AppendEntriesRequest(
                    _currentTerm,
                    _myId,
                    prevLogIndex,
                    prevLogTerm,
                    entries,
                    _commitIndex
                );
                
                var response = await _httpClient.PostAsJsonAsync($"{peer}/raft/append", request, _cancellationTokenSource.Token);
                
                if (response.IsSuccessStatusCode)
                {
                    var appendResponse = await response.Content.ReadFromJsonAsync<AppendEntriesResponse>();
                    if (appendResponse != null)
                    {
                        if (appendResponse.Term > _currentTerm)
                        {
                            // Higher term found, revert to follower
                            await BecomeFollower(appendResponse.Term);
                            return;
                        }
                        
                        if (appendResponse.Success)
                        {
                            // Update nextIndex and matchIndex for successful followers
                            if (entries.Any())
                            {
                                _nextIndex[peer] = nextIndex + entries.Count;
                                _matchIndex[peer] = _nextIndex[peer] - 1;
                                
                                // Check if we can update commitIndex
                                await UpdateCommitIndex();
                            }
                        }
                        else
                        {
                            // Log inconsistency, decrement nextIndex and retry
                            _nextIndex[peer] = Math.Max(1, nextIndex - 1);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Heartbeat to peer {Peer} failed: {Message}", peer, ex.Message);
            }
        }
    }

    /// <summary>
    /// Updates the commit index based on matchIndex values.
    /// </summary>
    private async Task UpdateCommitIndex()
    {
        if (_state != RaftStateEnum.Leader) return;
        
        // Sort matchIndex values to find the median
        var matchIndices = _matchIndex.Values.OrderBy(i => i).ToList();
        var majorityIndex = matchIndices[matchIndices.Count / 2];
        
        // Only update if the entry is from the current term
        var logEntries = _persistence.GetLogEntries();
        if (majorityIndex > _commitIndex && 
            majorityIndex <= logEntries.Count && 
            logEntries[(int)majorityIndex - 1].Term == _currentTerm)
        {
            _commitIndex = majorityIndex;
            await ApplyLogsToStateMachine();
        }
    }

    /// <summary>
    /// Replicates log entries to followers.
    /// </summary>
    private async Task ReplicateLogs()
    {
        if (_state != RaftStateEnum.Leader) return;
        
        // The SendHeartbeat method already handles log replication
        await SendHeartbeat();
    }
    
    /// <summary>
    /// Applies committed log entries to the state machine (time-series data table).
    /// </summary>
    private async Task ApplyLogsToStateMachine()
    {
        // Apply all entries from _lastApplied to _commitIndex
        while (_lastApplied < _commitIndex)
        {
            _lastApplied++;
            var logEntries = _persistence.GetLogEntries();
            if (_lastApplied <= logEntries.Count)
            {
                var logEntry = logEntries[(int)_lastApplied - 1];
                
                try
                {
                    var data = JsonSerializer.Deserialize<Data>(logEntry.Command);
                    if (data != null)
                    {
                        _persistence.AppendTimeSeriesData(data);
                        _persistence.MarkLogApplied(_lastApplied);
                        _logger.LogInformation("Applied log entry {Index} to state machine", _lastApplied);
                    }
                }
                catch (Exception ex)
                {
                     _logger.LogError("Failed to deserialize and apply command: {Message}", ex.Message);
                }
            }
        }
    }

    /// <summary>
    /// Generates a new random election timeout.
    /// </summary>
    private static int GetNewElectionTimeout()
    {
        var random = new Random();
        return random.Next(RaftConstants.MinElectionTimeout, RaftConstants.MaxElectionTimeout);
    }
}