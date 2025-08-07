using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TimeStore.Core.Database;
using TimeStore.Core.Database.Entities;

namespace TimeStore.Core.Raft;

/// <summary>
/// Implementation of the Raft consensus algorithm for distributed consensus.
/// </summary>
public class RaftService : IRaftService
{
    private readonly List<string> _peers;
    private readonly SqlitePersistenceService _persistence;
    private readonly HttpClient _httpClient;
    private readonly ILogger<RaftService> _logger;
    private readonly CancellationTokenSource _cancellationTokenSource = new();

    private long _currentTerm;
    private string? _votedFor;
    private long _commitIndex;
    private long _lastApplied;
    
    private Timer? _electionTimer;
    private Timer? _heartbeatTimer;
    private Timer? _leaderHealthCheckTimer;
    
    private DateTime _lastHeartbeatReceived = DateTime.UtcNow;
    private int _leaderMissedHeartbeats;
    
    private readonly Dictionary<string, long> _nextIndex = new();
    private readonly Dictionary<string, long> _matchIndex = new();
    private readonly Dictionary<string, int> _peerFailureCount = new();

    /// <summary>
    /// Gets the current state of the Raft node (Follower, Candidate, or Leader).
    /// </summary>
    public RaftStateEnum State { get; private set; } = RaftStateEnum.Follower;

    /// <summary>
    /// Gets the unique identifier for this Raft node.
    /// </summary>
    public string MyId { get; }

    /// <summary>
    /// Gets the ID of the current leader node, if known.
    /// </summary>
    public string? LeaderId { get; private set; }
    
    public RaftService(
        string myId, 
        IEnumerable<string> allPeers, 
        SqlitePersistenceService persistence, 
        HttpClient httpClient, 
        ILogger<RaftService> logger)
    {
        MyId = myId;
        _persistence = persistence;
        _httpClient = httpClient;
        _logger = logger;

        _peers = allPeers.Where(p => p != myId).ToList();
        
        InitializePeerTracking();

        _logger.LogInformation("Initialized Raft service for node {NodeId} with peers: {Peers}", 
            MyId, string.Join(", ", _peers));
    }
    
    private void InitializePeerTracking()
    {
        foreach (var peer in _peers)
        {
            _peerFailureCount[peer] = 0;
        }
    }

    /// <inheritdoc />
    public Task StartAsync()
    {
        _logger.LogInformation("Starting Raft service for node {NodeId}", MyId);

        LoadPersistedState();
        InitializeTimers();
        
        _logger.LogInformation("Raft service started for node {NodeId} in term {Term}, state: {State}", 
            MyId, _currentTerm, State);
        
        return Task.CompletedTask;
    }

    private void LoadPersistedState()
    {
        var persistedState = _persistence.GetRaftState();
        _currentTerm = persistedState.CurrentTerm;
        _votedFor = persistedState.VotedFor;
    }

    private void InitializeTimers()
    {
        _electionTimer = new Timer(
            async void (_) => await StartElection(), 
            null, 
            GetNewElectionTimeout(), 
            Timeout.Infinite);
            
        _heartbeatTimer = new Timer(
            async void (_) => await SendHeartbeat(), 
            null, 
            Timeout.Infinite, 
            Timeout.Infinite);
        
        _leaderHealthCheckTimer = new Timer(
            async void (_) => await CheckLeaderHealth(), 
            null, 
            RaftConstants.LeaderHealthCheckInterval, 
            RaftConstants.LeaderHealthCheckInterval);
    }

    /// <inheritdoc />
    public Task StopAsync()
    {
        _logger.LogInformation("Stopping Raft service for node {NodeId}", MyId);
        
        CleanupResources();
        
        _logger.LogInformation("Raft service stopped for node {NodeId}", MyId);
        
        return Task.CompletedTask;
    }
    
    private void CleanupResources()
    {
        _electionTimer?.Dispose();
        _heartbeatTimer?.Dispose();
        _leaderHealthCheckTimer?.Dispose();
        _cancellationTokenSource.Cancel();
    }

    /// <inheritdoc />
    public async Task<RequestVoteResponse> HandleRequestVoteAsync(RequestVoteRequest request)
    {
        _logger.LogInformation("Received vote request from {CandidateId} for term {Term}", 
            request.CandidateId, request.Term);
        
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
        bool isLogUpToDate = IsLogUpToDate(request.LastLogTerm, request.LastLogIndex, lastLog);

        if ((_votedFor == null || _votedFor == request.CandidateId) && isLogUpToDate)
        {
            return await GrantVote(request.CandidateId);
        }
        
        _logger.LogInformation("Rejecting vote: already voted for {VotedFor} or log not up-to-date", _votedFor);
        
        return new RequestVoteResponse(_currentTerm, false);
    }
    
    private bool IsLogUpToDate(long candidateLastLogTerm, long candidateLastLogIndex, RaftLogEntry lastLog)
    {
        return (candidateLastLogTerm > lastLog.Term) ||
               (candidateLastLogTerm == lastLog.Term && candidateLastLogIndex >= _persistence.GetLogEntries().Count);
    }
    
    private async Task<RequestVoteResponse> GrantVote(string candidateId)
    {
        _votedFor = candidateId;
        await _persistence.UpdateRaftState(_currentTerm, _votedFor);
        ResetElectionTimer();
        _logger.LogInformation("Voting for {CandidateId}", candidateId);
        
        return new RequestVoteResponse(_currentTerm, true);
    }

    /// <inheritdoc />
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
        
        // Rule 2: If request term >= currentTerm, become follower and recognize leader
        if (request.Term >= _currentTerm)
        {
            await AcknowledgeLeader(request.Term, request.LeaderId);
        }
        
        // Rule 3: Check for log consistency
        if (!IsLogConsistent(request.PrevLogIndex, request.PrevLogTerm))
        {
            return new AppendEntriesResponse(_currentTerm, false);
        }
        
        // Rule 4: Append new entries and truncate inconsistencies
        if (request.Entries.Any())
        {
            await ProcessLogEntries(request.PrevLogIndex, request.Entries);
        }
        
        // Rule 5: Update commit index
        if (request.LeaderCommit > _commitIndex)
        {
            await UpdateCommitIndexFromLeader(request.LeaderCommit);
        }

        return new AppendEntriesResponse(_currentTerm, true);
    }
    
    private async Task AcknowledgeLeader(long term, string leaderId)
    {
        if (State != RaftStateEnum.Follower || term > _currentTerm)
        {
            await BecomeFollower(term);
        }
        
        LeaderId = leaderId;
        _lastHeartbeatReceived = DateTime.UtcNow;
        _leaderMissedHeartbeats = 0;
        
        ResetElectionTimer();
    }
    
    private bool IsLogConsistent(long prevLogIndex, long prevLogTerm)
    {
        var localLog = _persistence.GetLogEntries();
        
        if (prevLogIndex > 0 && (prevLogIndex > localLog.Count || 
                                localLog[(int)prevLogIndex - 1].Term != prevLogTerm))
        {
            _logger.LogWarning("Log inconsistency at index {Index}", prevLogIndex);
            
            return false;
        }
        
        return true;
    }
    
    private Task ProcessLogEntries(long prevLogIndex, List<RaftLogEntry> entries)
    {
        var localLog = _persistence.GetLogEntries();
        
        if (prevLogIndex < localLog.Count)
        {
            _persistence.DeleteLogsFromIndex(prevLogIndex + 1);
        }
        
        foreach (var entry in entries)
        {
            _persistence.AppendLogEntry(entry);
        }
        
        _logger.LogInformation("Appended {Count} entries to log", entries.Count);
        
        return Task.CompletedTask;
    }
    
    private async Task UpdateCommitIndexFromLeader(long leaderCommit)
    {
        var localLogCount = _persistence.GetLogEntries().Count;
        _commitIndex = Math.Min(leaderCommit, localLogCount);
        await ApplyLogsToStateMachine();
    }
    
    /// <summary>
    /// A client-facing method to add new time-series data.
    /// As a follower, it will redirect to the leader.
    /// As a leader, it will append to its log and replicate.
    /// </summary>
    public async Task<(bool success, string? leaderId)> AddTimeSeriesDataAsync(Data data)
    {
        if (State != RaftStateEnum.Leader)
        {
            _logger.LogInformation("Redirecting client request to leader: {LeaderId}", LeaderId);
            return (false, LeaderId);
        }

        _logger.LogInformation("Leader received new data. Appending to log.");
        
        var command = JsonSerializer.Serialize(data);
        var logEntry = new RaftLogEntry(_currentTerm, command);
        
        _persistence.AppendLogEntry(logEntry);
        
        await ReplicateLogs();
        
        var replicationCount = 1;
        
        foreach (var peer in _peers)
        {
            if (_matchIndex.ContainsKey(peer) && _matchIndex[peer] >= _persistence.GetLogEntries().Count)
            {
                replicationCount++;
            }
        }
        
        if (replicationCount > RaftConstants.ClusterSize / 2)
        {
            _commitIndex = _persistence.GetLogEntries().Count;
            await ApplyLogsToStateMachine();
            
            return (true, null);
        }
        
        _logger.LogWarning("Failed to replicate entry to a majority of nodes");
        
        return (false, null);
    }
    
    /// <inheritdoc />
    public List<Data> GetTimeSeriesData()
    {
        return _persistence.GetAllTimeSeriesData();
    }

    /// <summary>
    /// Checks if the leader is still alive.
    /// </summary>
    private async Task CheckLeaderHealth()
    {
        if (State != RaftStateEnum.Follower || string.IsNullOrEmpty(LeaderId))
            return;
        
        var timeSinceLastHeartbeat = DateTime.UtcNow - _lastHeartbeatReceived;
        
        if (timeSinceLastHeartbeat.TotalMilliseconds > RaftConstants.HeartbeatInterval * 2)
        {
            _leaderMissedHeartbeats++;
            _logger.LogDebug("Haven't heard from leader {LeaderId} in {Time}ms. Missed heartbeats: {Count}", 
                LeaderId, timeSinceLastHeartbeat.TotalMilliseconds, _leaderMissedHeartbeats);
            
            if (_leaderMissedHeartbeats >= RaftConstants.MaxMissedHeartbeats)
            {
                _logger.LogWarning("Leader {LeaderId} appears to be down after missing {Count} heartbeats. Starting election.", 
                    LeaderId, _leaderMissedHeartbeats);
                
                try 
                {
                    using var cts = new CancellationTokenSource(500);
                    var response = await _httpClient.GetAsync($"{LeaderId}/raft/status", cts.Token);
                    if (!response.IsSuccessStatusCode)
                    {
                        _logger.LogWarning("Confirmed leader {LeaderId} is down with status code {StatusCode}", LeaderId, response.StatusCode);
                        await ForceStartNewElection();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Confirmed leader {LeaderId} is unreachable: {Message}", LeaderId, ex.Message);
                    await ForceStartNewElection();
                }
            }
        }
        else
        {
            _leaderMissedHeartbeats = 0;
        }
    }
    
    /// <summary>
    /// Forces a new election when the leader is detected as down
    /// </summary>
    private async Task ForceStartNewElection()
    {
        var oldLeaderId = LeaderId;
        LeaderId = null;
        _votedFor = null;
        _currentTerm++;
        
        await _persistence.UpdateRaftState(_currentTerm, null);
        
        _electionTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        
        _logger.LogWarning("Forcing new election after leader {LeaderId} failure, term incremented to {Term}", 
            oldLeaderId, _currentTerm);
        
        State = RaftStateEnum.Candidate;
        _votedFor = MyId;
        await _persistence.UpdateRaftState(_currentTerm, _votedFor);
        
        await StartElection();
    }
    
    /// <summary>
    /// Called when the election timer fires. A follower becomes a candidate.
    /// </summary>
    private async Task StartElection()
    {
        if (State == RaftStateEnum.Leader)
        {
            return;
        }

        _logger.LogInformation("{NodeId} election timer timed out. Starting new election.", MyId);
        
        _currentTerm++;
        State = RaftStateEnum.Candidate;
        _votedFor = MyId;

        await _persistence.UpdateRaftState(_currentTerm, _votedFor);
        
        ResetElectionTimer();
        
        var lastLog = _persistence.GetLastLogEntry();
        var request = new RequestVoteRequest(
            _currentTerm,
            MyId,
            _persistence.GetLogEntries().Count,
            lastLog.Term
        );
        
        var votesReceived = 1;
        var voteTasks = _peers.Select(peer => RequestVoteFromPeer(peer, request)).ToList();
        
        try 
        {
            var timeout = Task.Delay(1500); 
            
            while (voteTasks.Any() && !timeout.IsCompleted)
            {
                var completedTask = await Task.WhenAny(voteTasks.Concat([timeout]));
                
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

                            const int requiredVotes = (RaftConstants.ClusterSize / 2) + 1;
                            
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
        
        if (State == RaftStateEnum.Candidate)
        {
            int reachableNodes = 1;
            
            foreach (var task in voteTasks)
            {
                if (task.IsCompleted && !task.IsFaulted && task.Result != null)
                {
                    reachableNodes++;
                }
            }
            
            int majorityCount = (reachableNodes / 2) + 1;
            
            if (votesReceived >= majorityCount)
            {
                _logger.LogInformation("Received majority of votes from reachable nodes ({Votes}/{ReachableNodes}), becoming leader", 
                    votesReceived, reachableNodes);
                await BecomeLeader();
            }
            else
            {
                
                _logger.LogInformation("Election failed for term {Term}. Received {Votes} out of {Needed} needed votes from {Reachable} reachable nodes.", 
                    _currentTerm, votesReceived, majorityCount, reachableNodes);
                
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
            
            using var cts = new CancellationTokenSource(1000);
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
            if (_peerFailureCount.ContainsKey(peer))
            {
                IncrementPeerFailureCount(peer);
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
        if (State != RaftStateEnum.Follower || term > _currentTerm)
        {
            _logger.LogInformation("Transitioning to Follower for term {Term}", term);
            State = RaftStateEnum.Follower;
            _currentTerm = term;
            _votedFor = null;
            await _persistence.UpdateRaftState(term, null);
            _heartbeatTimer?.Change(Timeout.Infinite, Timeout.Infinite);
            
            ResetElectionTimer();
        }
    }

    /// <summary>
    /// Transitions the node to a Leader state.
    /// </summary>
    private async Task BecomeLeader()
    {
        _logger.LogInformation("Node {NodeId} becoming Leader for term {Term}!", MyId, _currentTerm);
        State = RaftStateEnum.Leader;
        LeaderId = MyId;
        _electionTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        
        var lastLogIndex = _persistence.GetLogEntries().Count;
        
        foreach (var peer in _peers)
        {
            _nextIndex[peer] = lastLogIndex + 1;
            _matchIndex[peer] = 0;
            _peerFailureCount[peer] = 0;
        }
        
        await SendHeartbeat();
        _heartbeatTimer?.Change(0, RaftConstants.HeartbeatInterval);
    }

    /// <summary>
    /// The leader's heartbeat and log replication mechanism.
    /// </summary>
    private async Task SendHeartbeat()
    {
        if (State != RaftStateEnum.Leader) return;

        _logger.LogDebug("Sending heartbeats to {PeerCount} peers", _peers.Count);

        var heartbeatTasks = new List<Task>();

        foreach (var peer in _peers)
        {
            heartbeatTasks.Add(SendHeartbeatToPeer(peer));
        }

        try
        {
            var timeout = Task.Delay(2000);
            await Task.WhenAny(Task.WhenAll(heartbeatTasks), timeout);
        }
        catch (Exception ex)
        {
            _logger.LogError("Error during heartbeat sending: {Message}", ex.Message);
        }
        
        if (State == RaftStateEnum.Leader)
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
            
            if (nextIndex <= localLog.Count)
            {
                entries = localLog.Skip((int)nextIndex - 1).ToList();
            }

            var request = new AppendEntriesRequest(
                _currentTerm,
                MyId,
                prevLogIndex,
                prevLogTerm,
                entries,
                _commitIndex
            );

            _logger.LogDebug("Sending heartbeat to {Peer}", peer);
            
            using var cts = new CancellationTokenSource(1000);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, _cancellationTokenSource.Token);

            var response = await _httpClient.PostAsJsonAsync($"{peer}/raft/append", request, linkedCts.Token);

            if (response.IsSuccessStatusCode)
            {
                _peerFailureCount[peer] = 0;

                var appendResponse = await response.Content.ReadFromJsonAsync<AppendEntriesResponse>(cancellationToken: linkedCts.Token);
                
                if (appendResponse != null)
                {
                    if (appendResponse.Term > _currentTerm)
                    {
                        _logger.LogInformation("Reverting to follower due to higher term from {Peer}", peer);
                        await BecomeFollower(appendResponse.Term);
                        
                        return;
                    }

                    if (appendResponse.Success)
                    {
                        if (entries.Any())
                        {
                            _nextIndex[peer] = nextIndex + entries.Count;
                            _matchIndex[peer] = _nextIndex[peer] - 1;
                            
                            await UpdateCommitIndex();
                        }
                    }
                    else
                    {
                        _nextIndex[peer] = Math.Max(1, nextIndex - 1);
                    }
                }
            }
            else
            {
                IncrementPeerFailureCount(peer);
                _logger.LogWarning("Heartbeat to {Peer} failed with status {Status}", peer, response.StatusCode);
            }
        }
        catch (Exception ex)
        {
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
        if (State != RaftStateEnum.Leader) return;
        
        var matchIndices = new List<long>(_matchIndex.Values) { _persistence.GetLogEntries().Count };
        matchIndices.Sort();
        
        var majorityIndex = matchIndices[matchIndices.Count / 2];
        
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
        if (State != RaftStateEnum.Leader)
        {
            return;
        }
        
        await SendHeartbeat();
    }

    /// <summary>
    /// Applies committed log entries to the state machine (time-series data table).
    /// </summary>
    private Task ApplyLogsToStateMachine()
    {
        while (_lastApplied < _commitIndex)
        {
            _lastApplied++;
            var logEntries = _persistence.GetLogEntries();

            if (_lastApplied > logEntries.Count)
            {
                continue;
            }
            
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

        return Task.CompletedTask;
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