using Google.Cloud.Firestore;

namespace LogSystem.Dashboard.Data;

/// <summary>
/// Firestore document models.
/// All classes use [FirestoreData] / [FirestoreProperty] for automatic serialization.
/// Firestore collections:
///   devices, file_events, network_events, app_usage_events, alert_events
/// </summary>

[FirestoreData]
public class FileEventEntity
{
    [FirestoreDocumentId]
    public string Id { get; set; } = string.Empty;

    [FirestoreProperty("deviceId")]
    public string DeviceId { get; set; } = string.Empty;

    [FirestoreProperty("user")]
    public string User { get; set; } = string.Empty;

    [FirestoreProperty("fileName")]
    public string FileName { get; set; } = string.Empty;

    [FirestoreProperty("fullPath")]
    public string FullPath { get; set; } = string.Empty;

    [FirestoreProperty("fileSize")]
    public long FileSize { get; set; }

    [FirestoreProperty("sha256")]
    public string? Sha256 { get; set; }

    [FirestoreProperty("actionType")]
    public string ActionType { get; set; } = string.Empty;

    [FirestoreProperty("timestamp")]
    public Timestamp Timestamp { get; set; }

    [FirestoreProperty("processName")]
    public string ProcessName { get; set; } = string.Empty;

    [FirestoreProperty("flag")]
    public string Flag { get; set; } = "Normal";

    [FirestoreProperty("isTransfer")]
    public bool IsTransfer { get; set; } = false;

    [FirestoreProperty("direction")]
    public string Direction { get; set; } = "Unknown";

    /// <summary>
    /// Where the file event originated: USB | NetworkShare | CloudSync | UserFolder | Local
    /// </summary>
    [FirestoreProperty("source")]
    public string Source { get; set; } = "Local";
}

[FirestoreData]
public class NetworkEventEntity
{
    [FirestoreDocumentId]
    public string Id { get; set; } = string.Empty;

    [FirestoreProperty("deviceId")]
    public string DeviceId { get; set; } = string.Empty;

    [FirestoreProperty("user")]
    public string User { get; set; } = string.Empty;

    [FirestoreProperty("processName")]
    public string ProcessName { get; set; } = string.Empty;

    [FirestoreProperty("processId")]
    public int ProcessId { get; set; }

    [FirestoreProperty("bytesSent")]
    public long BytesSent { get; set; }

    [FirestoreProperty("bytesReceived")]
    public long BytesReceived { get; set; }

    [FirestoreProperty("destinationIp")]
    public string DestinationIp { get; set; } = string.Empty;

    [FirestoreProperty("destinationPort")]
    public int DestinationPort { get; set; }

    [FirestoreProperty("durationSeconds")]
    public double DurationSeconds { get; set; }

    [FirestoreProperty("timestamp")]
    public Timestamp Timestamp { get; set; }

    [FirestoreProperty("flag")]
    public string Flag { get; set; } = "Normal";
}

[FirestoreData]
public class AppUsageEventEntity
{
    [FirestoreDocumentId]
    public string Id { get; set; } = string.Empty;

    [FirestoreProperty("deviceId")]
    public string DeviceId { get; set; } = string.Empty;

    [FirestoreProperty("user")]
    public string User { get; set; } = string.Empty;

    [FirestoreProperty("applicationName")]
    public string ApplicationName { get; set; } = string.Empty;

    [FirestoreProperty("windowTitle")]
    public string WindowTitle { get; set; } = string.Empty;

    [FirestoreProperty("startTime")]
    public Timestamp StartTime { get; set; }

    [FirestoreProperty("durationSeconds")]
    public double DurationSeconds { get; set; }

    [FirestoreProperty("processId")]
    public int ProcessId { get; set; }
}

[FirestoreData]
public class AlertEventEntity
{
    [FirestoreDocumentId]
    public string Id { get; set; } = string.Empty;

    [FirestoreProperty("deviceId")]
    public string DeviceId { get; set; } = string.Empty;

    [FirestoreProperty("user")]
    public string User { get; set; } = string.Empty;

    [FirestoreProperty("severity")]
    public string Severity { get; set; } = string.Empty;

    [FirestoreProperty("alertType")]
    public string AlertType { get; set; } = string.Empty;

    [FirestoreProperty("description")]
    public string Description { get; set; } = string.Empty;

    [FirestoreProperty("relatedFileName")]
    public string? RelatedFileName { get; set; }

    [FirestoreProperty("relatedProcessName")]
    public string? RelatedProcessName { get; set; }

    [FirestoreProperty("bytesInvolved")]
    public long? BytesInvolved { get; set; }

    [FirestoreProperty("timestamp")]
    public Timestamp Timestamp { get; set; }
}

[FirestoreData]
public class DeviceEntity
{
    [FirestoreDocumentId]
    public string DeviceId { get; set; } = string.Empty;

    [FirestoreProperty("hostname")]
    public string Hostname { get; set; } = string.Empty;

    [FirestoreProperty("user")]
    public string User { get; set; } = string.Empty;

    [FirestoreProperty("lastSeen")]
    public Timestamp LastSeen { get; set; }

    [FirestoreProperty("osVersion")]
    public string OsVersion { get; set; } = string.Empty;

    [FirestoreProperty("agentVersion")]
    public string AgentVersion { get; set; } = string.Empty;
}
