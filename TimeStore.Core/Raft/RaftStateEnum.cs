namespace TimeStore.Core.Raft;

public enum RaftStateEnum : byte
{
    Follower, Candidate, Leader
}