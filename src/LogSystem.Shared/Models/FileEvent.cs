namespace LogSystem.Shared.Models;

public class FileEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string DeviceId { get; set; } = string.Empty;
    public string MachineId { get; set; } = string.Empty;
    public string User { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public string? Sha256 { get; set; }
    public FileActionType ActionType { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string ProcessName { get; set; } = string.Empty;
    /// <summary>
    /// Normal | ProbableUpload | UsbTransfer | NetworkTransfer | CloudSyncTransfer
    /// </summary>
    public string Flag { get; set; } = "Normal";

    /// <summary>
    /// True if this event represents a file transfer (copy/move) between locations, rather than simple activity.
    /// </summary>
    public bool IsTransfer { get; set; } = false;

    /// <summary>
    /// Incoming (Download) or Outgoing (Upload)
    /// </summary>
    public string Direction { get; set; } = "Unknown";

    /// <summary>
    /// Where the watcher detected this event:
    /// UserFolder | USB | NetworkShare | CloudSync | SensitiveDir | ConfiguredPath
    /// </summary>
    public string Source { get; set; } = "Local";
}

public enum FileActionType
{
    Read,
    Write,
    Copy,
    Move,
    Delete,
    Rename,
    Create
}
