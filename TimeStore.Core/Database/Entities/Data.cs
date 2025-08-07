namespace TimeStore.Core.Database.Entities;

/// <summary>
/// Represents a data record in the TimeStore database.
/// </summary>
public class Data
{
    /// <summary>
    /// Unique identifier for the data record.
    /// </summary>
    public Guid Id { get; set; }

    /// <summary>
    /// Unique identifier for device.
    /// </summary>
    public string DeviceId { get; set; }

    /// <summary>
    /// Timestamp of when the data was recorded.
    /// </summary>
    public DateTime Timestamp { get; set; }

    /// <summary>
    /// Value of the data record, which can be a measurement or any other relevant information.
    /// </summary>
    public double Value { get; set; }
}