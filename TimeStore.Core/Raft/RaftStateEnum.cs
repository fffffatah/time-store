namespace TimeStore.Core.Raft;

/// <summary>
/// Represents the possible states of a Raft node in the consensus algorithm.
/// </summary>
public enum RaftStateEnum : byte
{
    Follower, Candidate, Leader
}