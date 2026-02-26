namespace LogSystem.Shared.Configuration;

public class AgentConfiguration
{
    public string DeviceId { get; set; } = string.Empty;
    public string ApiEndpoint { get; set; } = "https://localhost:5001/api/logs";
    public string ApiKey { get; set; } = string.Empty;
    public int UploadIntervalSeconds { get; set; } = 60;
    public int MaxBatchSize { get; set; } = 500;
    public FileMonitorConfig FileMonitor { get; set; } = new();
    public AppMonitorConfig AppMonitor { get; set; } = new();
    public NetworkMonitorConfig NetworkMonitor { get; set; } = new();
    public CorrelationConfig Correlation { get; set; } = new();
    public SecurityConfig Security { get; set; } = new();
}

public class FileMonitorConfig
{
    public bool Enabled { get; set; } = true;
    public List<string> WatchPaths { get; set; } = [];
    public List<string> SensitiveDirectories { get; set; } = [];
    public List<string> CloudSyncPaths { get; set; } = [];
    public bool ComputeSha256ForSensitive { get; set; } = true;
    public bool MonitorUsb { get; set; } = true;
    public bool MonitorNetworkShares { get; set; } = true;
    public List<string> ExcludedExtensions { get; set; } = [".tmp", ".log", ".etl"];
    /// <summary>
    /// Path substrings to exclude (case-insensitive). Any file whose full path
    /// contains one of these substrings will be silently skipped.
    /// </summary>
    public List<string> ExcludedPaths { get; set; } = [];
    /// <summary>
    /// When true, also watch user-profile folders (Desktop, Documents, Downloads,
    /// Pictures, Videos, Music) automatically — even if they are not listed in WatchPaths.
    /// </summary>
    public bool AutoWatchUserFolders { get; set; } = true;
    public int InternalBufferSize { get; set; } = 262144; // 256 KB default
}

public class AppMonitorConfig
{
    public bool Enabled { get; set; } = true;
    public int PollingIntervalMs { get; set; } = 3000; // 3 seconds
    public List<string> ExcludedProcesses { get; set; } = ["idle", "svchost", "csrss", "dwm"];
}

public class NetworkMonitorConfig
{
    public bool Enabled { get; set; } = true;
    public int PollingIntervalMs { get; set; } = 5000; // 5 seconds
    public List<string> ExcludedProcesses { get; set; } = ["System", "svchost"];
    public List<string> PrivateSubnets { get; set; } = ["10.", "172.16.", "192.168.", "127."];
}

public class CorrelationConfig
{
    public bool Enabled { get; set; } = true;
    public long LargeTransferThresholdBytes { get; set; } = 25 * 1024 * 1024; // 25 MB
    public long ContinuousTransferThresholdBytes { get; set; } = 30 * 1024 * 1024; // 30 MB
    public int ContinuousTransferWindowMinutes { get; set; } = 10;
    public long ProbableUploadThresholdBytes { get; set; } = 5 * 1024 * 1024; // 5 MB
    public int ProbableUploadWindowSeconds { get; set; } = 15;
}

public class SecurityConfig
{
    public bool EncryptLocalQueue { get; set; } = true;
    public bool TamperDetection { get; set; } = true;
    public string LocalQueuePath { get; set; } = @"C:\ProgramData\LogSystem\queue";
    public string LocalLogPath { get; set; } = @"C:\ProgramData\LogSystem\logs";
    public int LogRetentionDays { get; set; } = 90;
}
