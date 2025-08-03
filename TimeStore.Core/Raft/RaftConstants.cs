namespace TimeStore.Core.Raft;

public class RaftConstants
{
    // The total number of nodes in our hardcoded cluster.
    public const int ClusterSize = 3;
    // The election timeout range in milliseconds. Raft needs randomization here.
    public static readonly int MinElectionTimeout = 150;
    public static readonly int MaxElectionTimeout = 300;
    // The heartbeat interval for the leader in milliseconds.
    public static readonly int HeartbeatInterval = 50;
}