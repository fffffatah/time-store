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
    private readonly CancellationTokenSource _cancellationTokenSource = new ();

    private RaftStateEnum _state = RaftStateEnum.Follower;
    private long _currentTerm = 0;
    private string? _votedFor = null;
    private Timer? _electionTimer;
    private Timer? _heartbeatTimer;
    private Timer? _leaderHealthCheckTimer; // New timer for leader health check
    private long _commitIndex = 0;
    private long _lastApplied = 0;
    private string? _leaderId = null;
    private DateTime _lastHeartbeatReceived = DateTime.UtcNow;
    private int _leaderMissedHeartbeats = 0; // Track missed heartbeats from leader

    // Leader-specific state
    private readonly Dictionary<string, long> _nextIndex = new();
    private readonly Dictionary<string, long> _matchIndex = new();
    // Track unreachable peers
    private readonly Dictionary<string, int> _peerFailureCount = new();

    public RaftService(string myId, IEnumerable<string> allPeers, SqlitePersistenceService persistence, HttpClient httpClient, ILogger<RaftService> logger)
    {
        _myId = myId;
        _persistence = persistence;
        _httpClient = httpClient;
        _logger = logger;

        _peers = allPeers.Where(p => p != myId).ToList();

        // Initialize peer failure counts
        foreach (var peer in _peers)
        {
            _peerFailureCount[peer] = 0;
        }

        _logger.LogInformation("Initialized Raft service for node {NodeId} with peers: {Peers}", 
            _myId, string.Join(", ", _peers));
    }
    
    public RaftStateEnum State => _state;
    public string MyId => _myId;
    public string? LeaderId => _leaderId;

    /// <summary>
    /// Starts the Raft service.
    /// </summary>
    public Task StartAsync()
    {
        _logger.LogInformation("Starting Raft service for node {NodeId}", _myId);

        // Load persistent state from DB on startup
        var persistedState = _persistence.GetRaftState();
        _currentTerm = persistedState.CurrentTerm;
        _votedFor = persistedState.VotedFor;
        
        // Initialize timers with correct callbacks
        _electionTimer = new Timer(async void (_) => await StartElection(), null, GetNewElectionTimeout(), Timeout.Infinite);
        _heartbeatTimer = new Timer(async void (_) => await SendHeartbeat(), null, Timeout.Infinite, Timeout.Infinite);
        
        // Initialize leader health check timer with the correct interval
        _leaderHealthCheckTimer = new Timer(
            async void (_) => await CheckLeaderHealth(), 
            null, 
            RaftConstants.LeaderHealthCheckInterval, 
            RaftConstants.LeaderHealthCheckInterval);
        
        _logger.LogInformation("Raft service started for node {NodeId} in term {Term}, state: {State}", 
            _myId, _currentTerm, _state);
        
        return Task.CompletedTask;
    }

    /// <summary>
    /// Stops the Raft service.
    /// </summary>
    public Task StopAsync()
    {
        _logger.LogInformation("Stopping Raft service for node {NodeId}", _myId);
        
        // Dispose timers
        _electionTimer?.Dispose();
        _heartbeatTimer?.Dispose();
        _leaderHealthCheckTimer?.Dispose();
        
        // Cancel any pending operations
        _cancellationTokenSource.Cancel();
        
        _logger.LogInformation("Raft service stopped for node {NodeId}", _myId);
        
        return Task.CompletedTask;
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
            _logger.LogInformation("Rejecting vote: candidate term {CandidateTerm} < current term {CurrentTerm}", 
                request.Term, _currentTerm);
            return new RequestVoteResponse(_currentTerm, false);
        }

        // Rule 2: If request term > currentTerm, become follower and update term
        if (request.Term > _currentTerm)
        {
            _logger.LogInformation("Candidate term {CandidateTerm} > current term {CurrentTerm}, becoming follower", 
                request.Term, _currentTerm);
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
            ResetElectionTimer();
            _logger.LogInformation("Voting for {CandidateId}", request.CandidateId);
            return new RequestVoteResponse(_currentTerm, true);
        }
        
        _logger.LogInformation("Rejecting vote: already voted for {VotedFor} or log not up-to-date", _votedFor);
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
            _logger.LogInformation("Rejecting AppendEntries: leader term {LeaderTerm} < current term {CurrentTerm}", 
                request.Term, _currentTerm);
            return new AppendEntriesResponse(_currentTerm, false);
        }
        
        // Rule 2: If request term >= currentTerm, become follower and reset election timer
        if (request.Term >= _currentTerm)
        {
            if (_state != RaftStateEnum.Follower || request.Term > _currentTerm)
            {
                await BecomeFollower(request.Term);
            }
            _leaderId = request.LeaderId;
            _lastHeartbeatReceived = DateTime.UtcNow;
            _leaderMissedHeartbeats = 0; // Reset missed heartbeats counter
        }
        
        // Reset election timer explicitly with a new timeout
        ResetElectionTimer();

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
            if (_matchIndex.ContainsKey(peer) && _matchIndex[peer] >= _persistence.GetLogEntries().Count)
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
    /// Checks if the leader is still alive.
    /// </summary>
    private async Task CheckLeaderHealth()
    {
        // Only followers need to check leader health
        if (_state != RaftStateEnum.Follower || string.IsNullOrEmpty(_leaderId))
            return;
            
        // Calculate time since last heartbeat
        var timeSinceLastHeartbeat = DateTime.UtcNow - _lastHeartbeatReceived;
        
        // If we haven't heard from the leader in more than 2x the heartbeat interval,
        // increment the missed heartbeats counter
        if (timeSinceLastHeartbeat.TotalMilliseconds > RaftConstants.HeartbeatInterval * 2)
        {
            _leaderMissedHeartbeats++;
            _logger.LogDebug("Haven't heard from leader {LeaderId} in {Time}ms. Missed heartbeats: {Count}", 
                _leaderId, timeSinceLastHeartbeat.TotalMilliseconds, _leaderMissedHeartbeats);
                
            // If we've missed too many heartbeats, assume the leader is down and start an election
            if (_leaderMissedHeartbeats >= RaftConstants.MaxMissedHeartbeats)
            {
                _logger.LogWarning("Leader {LeaderId} appears to be down after missing {Count} heartbeats. Starting election.", 
                    _leaderId, _leaderMissedHeartbeats);
                    
                // Try to check if the leader is really down by sending a ping
                try 
                {
                    using var cts = new CancellationTokenSource(500); // Short timeout (500ms)
                    var response = await _httpClient.GetAsync($"{_leaderId}/raft/status", cts.Token);
                    if (!response.IsSuccessStatusCode)
                    {
                        // Leader is confirmed down, clear the leader ID and start new election
                        _logger.LogWarning("Confirmed leader {LeaderId} is down with status code {StatusCode}", _leaderId, response.StatusCode);
                        await ForceStartNewElection();
                    }
                }
                catch (Exception ex)
                {
                    // Network error or timeout confirms leader is unreachable
                    _logger.LogWarning("Confirmed leader {LeaderId} is unreachable: {Message}", _leaderId, ex.Message);
                    await ForceStartNewElection();
                }
            }
        }
        else
        {
            // Reset the counter if we're receiving heartbeats
            _leaderMissedHeartbeats = 0;
        }
    }
    
    /// <summary>
    /// Forces a new election when the leader is detected as down
    /// </summary>
    private async Task ForceStartNewElection()
    {
        // Store the old leader ID before clearing it for logging
        var oldLeaderId = _leaderId;
        
        // Explicitly clear the leader ID since we believe it's down
        _leaderId = null;

        // Reset voting state to allow voting in new election
        _votedFor = null;
        
        // Increment term to ensure we start a fresh election
        _currentTerm++;

        // Update persistent state with new term and reset vote
        await _persistence.UpdateRaftState(_currentTerm, null);
        
        // Cancel any existing election timer and start a new election immediately
        _electionTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        
        _logger.LogWarning("Forcing new election after leader {LeaderId} failure, term incremented to {Term}", 
            oldLeaderId, _currentTerm);
        
        // Set to candidate state and vote for self
        _state = RaftStateEnum.Candidate;
        _votedFor = _myId;
        await _persistence.UpdateRaftState(_currentTerm, _votedFor);
        
        // Start the election process immediately
        await StartElection();
    }
    
    /// <summary>
    /// Called when the election timer fires. A follower becomes a candidate.
    /// </summary>
    private async Task StartElection()
    {
        if (_state == RaftStateEnum.Leader)
        {
            return;
        }

        _logger.LogInformation("{NodeId} election timer timed out. Starting new election.", _myId);
        
        // Increment term and become a candidate
        _currentTerm++;
        _state = RaftStateEnum.Candidate;
        _votedFor = _myId; // Vote for self

        await _persistence.UpdateRaftState(_currentTerm, _votedFor);
        
        // Reset election timer with a new timeout
        ResetElectionTimer();

        // Send RequestVote RPCs to all other servers
        var lastLog = _persistence.GetLastLogEntry();
        var request = new RequestVoteRequest(
            _currentTerm,
            _myId,
            _persistence.GetLogEntries().Count,
            lastLog.Term
        );
        
        var votesReceived = 1; // Vote for self
        var voteTasks = new List<Task<RequestVoteResponse?>>();
        
        foreach (var peer in _peers)
        {
            voteTasks.Add(RequestVoteFromPeer(peer, request));
        }
        
        // Wait for vote requests with a timeout
        try 
        {
            // Use a shorter timeout to avoid waiting too long for dead nodes
            var timeout = Task.Delay(1500); 
            
            // Process votes as they come in, not waiting for all to complete
            while (voteTasks.Any() && !timeout.IsCompleted)
            {
                var completedTask = await Task.WhenAny(voteTasks.Concat(new[] { timeout }));
                
                if (completedTask == timeout)
                {
                    _logger.LogWarning("Vote collection timeout reached");
                    break;
                }
                
                var taskIndex = voteTasks.IndexOf(completedTask as Task<RequestVoteResponse?>);
                if (taskIndex >= 0)
                {
                    var task = voteTasks[taskIndex];
                    voteTasks.RemoveAt(taskIndex);
                    
                    if (task.IsCompletedSuccessfully && task.Result != null)
                    {
                        var voteResponse = task.Result;
                        
                        if (voteResponse.VoteGranted && voteResponse.Term == _currentTerm)
                        {
                            votesReceived++;
                            _logger.LogInformation("Received vote, total votes: {Votes}", votesReceived);
                            
                            // Check if we have majority after each vote
                            int totalAliveNodes = 1 + voteTasks.Count + votesReceived; // me + pending votes + received votes
                            int requiredVotes = (RaftConstants.ClusterSize / 2) + 1;
                            
                            // If we have majority of votes among reachable nodes, become leader
                            if (votesReceived >= requiredVotes)
                            {
                                _logger.LogInformation("Received majority of votes ({Votes}/{Required}), becoming leader", 
                                    votesReceived, requiredVotes);
                                await BecomeLeader();
                                return;
                            }
                        }
                        else if (voteResponse.Term > _currentTerm)
                        {
                            // Higher term found, revert to follower
                            _logger.LogInformation("Discovered higher term, reverting to follower");
                            await BecomeFollower(voteResponse.Term);
                            return;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError("Error during vote collection: {Message}", ex.Message);
        }
        
        // Check if we're still a candidate (could have changed during voting)
        if (_state == RaftStateEnum.Candidate)
        {
            // Calculate reachable nodes (based on who responded to vote requests)
            int reachableNodes = 1; // Include ourselves
            foreach (var task in voteTasks)
            {
                if (task.IsCompleted && !task.IsFaulted && task.Result != null)
                {
                    reachableNodes++;
                }
            }
            
            // Recalculate if we have a majority of REACHABLE nodes
            int majorityCount = (reachableNodes / 2) + 1;
            
            if (votesReceived >= majorityCount)
            {
                _logger.LogInformation("Received majority of votes from reachable nodes ({Votes}/{ReachableNodes}), becoming leader", 
                    votesReceived, reachableNodes);
                await BecomeLeader();
            }
            else
            {
                // Election failed, log information and try again after a shorter timeout
                _logger.LogInformation("Election failed for term {Term}. Received {Votes} out of {Needed} needed votes from {Reachable} reachable nodes.", 
                    _currentTerm, votesReceived, majorityCount, reachableNodes);
                
                // Start a new election sooner if this one failed
                var shorterTimeout = GetNewElectionTimeout() / 2;
                _electionTimer?.Change(shorterTimeout, Timeout.Infinite);
            }
        }
    }
    
    private async Task<RequestVoteResponse?> RequestVoteFromPeer(string peer, RequestVoteRequest request)
    {
        try
        {
            _logger.LogInformation("Sending vote request to peer {Peer}", peer);

            // Set a shorter timeout for vote requests to detect dead nodes faster
            using var cts = new CancellationTokenSource(1000); // 1 second timeout
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, _cancellationTokenSource.Token);
            var response = await _httpClient.PostAsJsonAsync($"{peer}/raft/vote", request, linkedCts.Token);

            if (response.IsSuccessStatusCode)
            {
                var voteResponse = await response.Content.ReadFromJsonAsync<RequestVoteResponse>(cancellationToken: linkedCts.Token);
                _logger.LogInformation("Received vote response from {Peer}: granted={Granted}", peer, voteResponse?.VoteGranted);

                return voteResponse;
            }

            _logger.LogWarning("Vote request to {Peer} failed with status {Status}", peer, response.StatusCode);
        }
        catch (Exception ex)
        {
            // Track peer failures - this is important for leader failure detection
            if (_peerFailureCount.ContainsKey(peer))
            {
                //_peerFailureCount[peer]++;
                IncrementPeerFailureCount(peer);

                /*// If this is the current leader and it's unreachable, clear it sooner
                if (peer == _leaderId && _peerFailureCount[peer] >= 2)
                {
                    _logger.LogWarning("Leader {LeaderId} appears to be down, clearing leader reference", _leaderId);
                    _leaderId = null;
                }*/
            }

            _logger.LogWarning("Could not reach peer {Peer} for vote: {Message}", peer, ex.Message);
        }

        return null;
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

            // Reset election timer with a new timeout
            ResetElectionTimer();
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
            _peerFailureCount[peer] = 0; // Reset failure counts
        }

        // Start sending heartbeats immediately
        await SendHeartbeat();
        _heartbeatTimer?.Change(0, RaftConstants.HeartbeatInterval);
    }

    /// <summary>
    /// The leader's heartbeat and log replication mechanism.
    /// </summary>
    private async Task SendHeartbeat()
    {
        if (_state != RaftStateEnum.Leader) return;

        _logger.LogDebug("Sending heartbeats to {PeerCount} peers", _peers.Count);

        var heartbeatTasks = new List<Task>();

        foreach (var peer in _peers)
        {
            heartbeatTasks.Add(SendHeartbeatToPeer(peer));
        }

        try
        {
            // Wait for all heartbeats with timeout
            var timeout = Task.Delay(2000);
            await Task.WhenAny(Task.WhenAll(heartbeatTasks), timeout);
        }
        catch (Exception ex)
        {
            _logger.LogError("Error during heartbeat sending: {Message}", ex.Message);
        }

        // Ensure we're still sending heartbeats at regular intervals
        if (_state == RaftStateEnum.Leader)
        {
            _heartbeatTimer?.Change(RaftConstants.HeartbeatInterval, RaftConstants.HeartbeatInterval);
        }
    }

    private async Task SendHeartbeatToPeer(string peer)
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

            _logger.LogDebug("Sending heartbeat to {Peer}", peer);

            // Use a shorter timeout for heartbeats to detect failures faster
            using var cts = new CancellationTokenSource(1000); // 1 second timeout
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, _cancellationTokenSource.Token);

            var response = await _httpClient.PostAsJsonAsync($"{peer}/raft/append", request, linkedCts.Token);

            if (response.IsSuccessStatusCode)
            {
                // Reset the failure count on success
                _peerFailureCount[peer] = 0;

                var appendResponse = await response.Content.ReadFromJsonAsync<AppendEntriesResponse>();
                if (appendResponse != null)
                {
                    if (appendResponse.Term > _currentTerm)
                    {
                        // Higher term found, revert to follower
                        _logger.LogInformation("Reverting to follower due to higher term from {Peer}", peer);
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
            else
            {
                // Increment failure count and check if this node is consistently unreachable
                IncrementPeerFailureCount(peer);
                _logger.LogWarning("Heartbeat to {Peer} failed with status {Status}", peer, response.StatusCode);
            }
        }
        catch (Exception ex)
        {
            // Track peer failures
            IncrementPeerFailureCount(peer);
            _logger.LogWarning("Heartbeat to peer {Peer} failed: {Message}", peer, ex.Message);
        }
    }

    /// <summary>
    /// Tracks peer failures to detect if a node is down
    /// </summary>
    private void IncrementPeerFailureCount(string peer)
    {
        if (_peerFailureCount.ContainsKey(peer))
        {
            _peerFailureCount[peer]++;

            if (_peerFailureCount[peer] >= 5)
            {
                _logger.LogWarning("Peer {Peer} has failed {Count} consecutive times, may be down",
                    peer, _peerFailureCount[peer]);
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
        var matchIndices = new List<long>(_matchIndex.Values);
        matchIndices.Add(_persistence.GetLogEntries().Count); // Include leader's own log
        matchIndices.Sort();

        // Get the index that a majority of servers have replicated
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
    /// Resets the election timer with a new random timeout.
    /// </summary>
    private void ResetElectionTimer()
    {
        var timeout = GetNewElectionTimeout();
        _logger.LogInformation("Resetting election timer with timeout: {Timeout}ms", timeout);
        _electionTimer?.Change(timeout, Timeout.Infinite);
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