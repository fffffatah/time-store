namespace TimeStore.Core.Raft;

/// <summary>
/// Contains constants used in the Raft consensus algorithm implementation.
/// </summary>
public static class RaftConstants
{
    /// <summary>
    /// The size of the Raft cluster, which is the number of nodes required to form a quorum.
    /// </summary>
    public const int ClusterSize = 3;

    /// <summary>
    /// The minimum and maximum election timeout in milliseconds.
    /// </summary>
    public const int MinElectionTimeout = 300;

    /// <summary>
    /// The maximum election timeout in milliseconds. This is the upper limit for how long a node will wait before starting a new election.
    /// </summary>
    public const int MaxElectionTimeout = 600;

    /// <summary>
    /// The interval at which heartbeats are sent from the leader to followers, in milliseconds.
    /// </summary>
    public const int HeartbeatInterval = 100;

    /// <summary>
    /// The maximum number of missed heartbeats before a follower considers the leader to be down and starts a new election.
    /// </summary>
    public const int MaxMissedHeartbeats = 3;

    /// <summary>
    /// The interval at which the leader checks the health of its followers, in milliseconds.
    /// </summary>
    public const int LeaderHealthCheckInterval = 150;
}