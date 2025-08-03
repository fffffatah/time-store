namespace TimeStore.Core.Raft;

/// <summary>
/// Represents a request for vote sent by candidates to other nodes.
/// </summary>
/// <param name="Term">Candidate's term</param>
/// <param name="CandidateId">Candidate requesting vote</param>
/// <param name="LastLogIndex">Index of candidate's last log entry</param>
/// <param name="LastLogTerm">Term of candidate's last log entry</param>
public record RequestVoteRequest(
    long Term,
    string CandidateId,
    long LastLogIndex,
    long LastLogTerm
);

/// <summary>
/// Represents a response to a vote request.
/// </summary>
/// <param name="Term">Current term, for candidate to update itself</param>
/// <param name="VoteGranted">True if candidate received vote</param>
public record RequestVoteResponse(
    long Term,
    bool VoteGranted
);

/// <summary>
/// Represents a log entry in the Raft consensus.
/// </summary>
/// <param name="Term">Term when entry was received by leader</param>
/// <param name="Command">Command for state machine</param>
public record RaftLogEntry(
    long Term,
    string Command
);

/// <summary>
/// Represents an AppendEntries request sent by the leader to replicate log entries.
/// Also used as a heartbeat when entries is empty.
/// </summary>
/// <param name="Term">Leader's term</param>
/// <param name="LeaderId">Leader ID so followers can redirect clients</param>
/// <param name="PrevLogIndex">Index of log entry immediately preceding new ones</param>
/// <param name="PrevLogTerm">Term of prevLogIndex entry</param>
/// <param name="Entries">Log entries to store (empty for heartbeat)</param>
/// <param name="LeaderCommit">Leader's commitIndex</param>
public record AppendEntriesRequest(
    long Term,
    string LeaderId,
    long PrevLogIndex,
    long PrevLogTerm,
    List<RaftLogEntry> Entries,
    long LeaderCommit
);

/// <summary>
/// Represents a response to an AppendEntries request.
/// </summary>
/// <param name="Term">Current term, for leader to update itself</param>
/// <param name="Success">True if follower contained entry matching prevLogIndex and prevLogTerm</param>
public record AppendEntriesResponse(
    long Term,
    bool Success
);
