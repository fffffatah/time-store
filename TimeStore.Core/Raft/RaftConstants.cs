namespace TimeStore.Core.Raft;

public class RaftConstants
{
    // The total number of nodes in our hardcoded cluster.
    public const int ClusterSize = 3;
    // The election timeout range in milliseconds. Raft needs randomization here.
    public static readonly int MinElectionTimeout = 300;
    public static readonly int MaxElectionTimeout = 600;
    // The heartbeat interval for the leader in milliseconds.
    public static readonly int HeartbeatInterval = 100;
    // The number of missed heartbeats before considering a leader down
    public static readonly int MaxMissedHeartbeats = 3;
    // The leader health check interval in milliseconds
    public static readonly int LeaderHealthCheckInterval = 150;
}