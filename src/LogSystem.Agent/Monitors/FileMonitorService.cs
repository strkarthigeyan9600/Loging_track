using System.Security.Cryptography;
using LogSystem.Shared.Configuration;
using LogSystem.Shared.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LogSystem.Agent.Monitors;

/// <summary>
/// Module 1 — File Activity Monitor
/// Uses FileSystemWatcher to track file operations on local drives, USB, network shares, and cloud sync folders.
/// Filters out system noise (AppData, caches, internal DB files) so only meaningful user actions are reported.
/// </summary>
public sealed class FileMonitorService : IDisposable
{
    private readonly ILogger<FileMonitorService> _logger;
    private readonly AgentConfiguration _config;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Action<FileEvent> _onFileEvent;
    private readonly string _currentUser;
    private readonly string _machineId;
    private Timer? _usbPollTimer;
    private readonly HashSet<string> _knownDrives = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _watchedPaths = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// Drives already present when the agent started — assumed to be internal/system drives.
    /// Any drive letter that appears AFTER startup is treated as an external device (USB/Type-C/etc.).
    /// </summary>
    private readonly HashSet<string> _baselineDrives = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Built-in noisy path fragments that are ALWAYS excluded regardless of config.
    /// These generate thousands of events per minute and are never user-meaningful.
    /// </summary>
    private static readonly string[] _builtInExcludedPaths =
    [
        @"\AppData\Local\Temp",
        @"\AppData\Local\Microsoft",
        @"\AppData\Local\Packages",
        @"\AppData\Local\Google\Chrome\User Data",
        @"\AppData\Local\BraveSoftware\Brave-Browser\User Data",
        @"\AppData\Local\Mozilla\Firefox\Profiles",
        @"\AppData\Local\Microsoft\Edge\User Data",
        @"\AppData\Roaming\Code",        // VS Code internal state
        @"\AppData\Roaming\Microsoft",
        @"\AppData\Local\Programs",
        @"\AppData\Local\CrashDumps",
        @"\AppData\Local\D3DSCache",
        @"\AppData\Local\cache",
        @"\AppData\LocalLow",
        @"\.git\",
        @"\.vs\",
        @"\node_modules\",
        @"\obj\Debug",
        @"\obj\Release",
        @"\bin\Debug",
        @"\bin\Release",
        @"\__pycache__\",
        @"\$Recycle.Bin\",
        @"\System Volume Information\",
        @"\ProgramData\LogSystem\",       // Our own queue files
    ];

    /// <summary>
    /// Built-in noisy extensions always excluded in addition to config.
    /// </summary>
    private static readonly HashSet<string> _builtInExcludedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".vscdb", ".vscdb-journal", ".ldb", ".lock", ".dat",
        ".sqlite-journal", ".sqlite-shm", ".sqlite-wal",
        ".crswap", ".partial", ".download",
        ".pma", ".blf", ".regtrans-ms",
        ".etl", ".pf", ".diagsession"
    };

    public FileMonitorService(
        ILogger<FileMonitorService> logger,
        IOptions<AgentConfiguration> config,
        Action<FileEvent> onFileEvent)
    {
        _logger = logger;
        _config = config.Value;
        _onFileEvent = onFileEvent;
        _currentUser = Environment.UserName;
        _machineId = _config.DeviceId;
    }

    public void Start()
    {
        if (!_config.FileMonitor.Enabled)
        {
            _logger.LogInformation("File monitor is disabled.");
            return;
        }

        _logger.LogInformation("Starting file monitor...");

        // ── Auto-detect meaningful user folders ──
        if (_config.FileMonitor.AutoWatchUserFolders)
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var userFolders = new[]
            {
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Path.Combine(userProfile, "Downloads"),
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
                Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            };

            foreach (var folder in userFolders)
            {
                if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder) && _watchedPaths.Add(folder))
                    AddWatcher(folder, "UserFolder");
            }
        }

        // Watch configured paths (expand environment variables)
        foreach (var rawPath in _config.FileMonitor.WatchPaths)
        {
            var path = Environment.ExpandEnvironmentVariables(rawPath);
            if (Directory.Exists(path) && _watchedPaths.Add(path))
                AddWatcher(path, "ConfiguredPath");
        }

        // Watch sensitive directories
        foreach (var rawPath in _config.FileMonitor.SensitiveDirectories)
        {
            var path = Environment.ExpandEnvironmentVariables(rawPath);
            if (Directory.Exists(path) && _watchedPaths.Add(path))
                AddWatcher(path, "SensitiveDir");
        }

        // Watch cloud sync folders
        var cloudPaths = DetectCloudSyncFolders();
        foreach (var path in cloudPaths)
        {
            if (_watchedPaths.Add(path))
                AddWatcher(path, "CloudSync");
        }
        foreach (var rawPath in _config.FileMonitor.CloudSyncPaths)
        {
            var path = Environment.ExpandEnvironmentVariables(rawPath);
            if (Directory.Exists(path) && _watchedPaths.Add(path))
                AddWatcher(path, "CloudSync");
        }

        // ── Baseline all currently-present drive letters ──
        // Type-C drives, external SSDs, phone storage etc. often appear as DriveType.Fixed.
        // By recording the drives that exist at startup, we can detect ANY new drive as external.
        foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady))
            _baselineDrives.Add(d.RootDirectory.FullName);

        _logger.LogInformation("Baseline drives: {Drives}", string.Join(", ", _baselineDrives));

        // Monitor USB / Type-C / external drives
        if (_config.FileMonitor.MonitorUsb)
        {
            ScanForExternalDrives();
            _usbPollTimer = new Timer(_ =>
            {
                ScanForExternalDrives();
                if (_config.FileMonitor.MonitorNetworkShares) RescanNetworkDrives();
            }, null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
        }

        // Monitor network shares
        if (_config.FileMonitor.MonitorNetworkShares)
        {
            ScanForNetworkDrives();
        }

        _logger.LogInformation("File monitor started with {Count} watchers: {Paths}",
            _watchers.Count, string.Join(", ", _watchedPaths));
    }

    /// <summary>
    /// Auto-detect common cloud sync folders (OneDrive, Google Drive, Dropbox, iCloud).
    /// </summary>
    private static List<string> DetectCloudSyncFolders()
    {
        var result = new List<string>();
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        string[] candidates =
        [
            Path.Combine(userProfile, "OneDrive"),
            Path.Combine(userProfile, "OneDrive - Personal"),
            Path.Combine(userProfile, "Google Drive"),
            Path.Combine(userProfile, "Dropbox"),
            Path.Combine(userProfile, "iCloudDrive"),
            Path.Combine(userProfile, "MEGA"),
            Path.Combine(userProfile, "Box"),
        ];

        foreach (var path in candidates)
        {
            if (Directory.Exists(path))
                result.Add(path);
        }

        return result;
    }

    private void AddWatcher(string path, string source)
    {
        try
        {
            var watcher = new FileSystemWatcher(path)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName
                             | NotifyFilters.DirectoryName
                             | NotifyFilters.LastWrite
                             | NotifyFilters.LastAccess   // ← Track file opens/reads
                             | NotifyFilters.Size
                             | NotifyFilters.CreationTime,
                InternalBufferSize = _config.FileMonitor.InternalBufferSize,
                EnableRaisingEvents = true
            };

            watcher.Created += (s, e) => HandleEvent(e.FullPath, FileActionType.Create, source);
            watcher.Changed += (s, e) => HandleEvent(e.FullPath, FileActionType.Write, source);
            watcher.Deleted += (s, e) => HandleEvent(e.FullPath, FileActionType.Delete, source);
            watcher.Renamed += (s, e) =>
            {
                HandleEvent(e.OldFullPath, FileActionType.Rename, source, e.FullPath);
            };
            watcher.Error += (s, e) =>
            {
                _logger.LogWarning(e.GetException(), "FileSystemWatcher error/buffer overflow on {Path}", path);
                // Re-enable the watcher after a buffer overflow
                try { watcher.EnableRaisingEvents = true; } catch { /* best effort */ }
            };

            _watchers.Add(watcher);
            _logger.LogDebug("Watching {Path} (source: {Source})", path, source);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create watcher for {Path}", path);
        }
    }

    /// <summary>
    /// Check if a file path should be excluded based on built-in + configured exclusions.
    /// </summary>
    private bool IsExcludedPath(string fullPath)
    {
        // Built-in path exclusions
        foreach (var excluded in _builtInExcludedPaths)
        {
            if (fullPath.Contains(excluded, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // Config-based path exclusions
        foreach (var excluded in _config.FileMonitor.ExcludedPaths)
        {
            if (fullPath.Contains(excluded, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Check if a file extension should be excluded.
    /// </summary>
    private bool IsExcludedExtension(string? ext)
    {
        if (string.IsNullOrEmpty(ext)) return false;

        // Built-in extension exclusions
        if (_builtInExcludedExtensions.Contains(ext)) return true;

        // Config-based extension exclusions
        return _config.FileMonitor.ExcludedExtensions
            .Any(e => e.Equals(ext, StringComparison.OrdinalIgnoreCase));
    }

    private void HandleEvent(string fullPath, FileActionType actionType, string source, string? newPath = null)
    {
        try
        {
            // ── Noise filtering (skip for USB/Network — always report those) ──
            bool isTransferSource = source is "USB" or "NetworkShare" or "CloudSync";

            if (!isTransferSource)
            {
                // Filter excluded paths (AppData, .git, node_modules, caches, etc.)
                if (IsExcludedPath(fullPath))
                    return;

                // Filter excluded extensions
                var ext = Path.GetExtension(fullPath)?.ToLowerInvariant();
                if (IsExcludedExtension(ext))
                    return;

                // Skip files that start with ~ (temp/lock files from Office, etc.)
                var fileName2 = Path.GetFileName(fullPath);
                if (fileName2.StartsWith('~') || fileName2.StartsWith('.'))
                    return;
            }

            var fileName = Path.GetFileName(fullPath);
            long fileSize = 0;
            string? sha256 = null;

            if (actionType != FileActionType.Delete && File.Exists(fullPath))
            {
                try
                {
                    var fi = new FileInfo(fullPath);
                    fileSize = fi.Length;

                    // Compute SHA256 for sensitive directories or transfers
                    if ((_config.FileMonitor.ComputeSha256ForSensitive &&
                        (source == "SensitiveDir" || isTransferSource)) &&
                        fileSize > 0 && fileSize < 100 * 1024 * 1024)
                    {
                        sha256 = ComputeSha256(fullPath);
                    }
                }
                catch (IOException)
                {
                    // File may be locked — that's okay
                }
            }

            // Determine the process that triggered this (best-effort)
            var processName = GetLikelyProcess();

            // ── Known browser process names ──
            var browserProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "chrome", "brave", "msedge", "firefox", "opera", "vivaldi", "chromium", "iexplore", "safari" };

            // ── Transfer / source detection ──
            var flag = "Normal";
            var effectiveAction = actionType;
            bool isTransfer = false;
            string direction = "Unknown";

            // 1) Activity on USB / Network / Cloud folders (The watcher is ON the external device)
            if (isTransferSource)
            {
                flag = source switch
                {
                    "USB" => "UsbTransfer",
                    "NetworkShare" => "NetworkTransfer",
                    "CloudSync" => "CloudSyncTransfer",
                    _ => "Normal"
                };

                if (actionType is FileActionType.Create or FileActionType.Write)
                {
                    // Created on USB = Copied TO USB (Outgoing)
                    effectiveAction = FileActionType.Copy;
                    isTransfer = true;
                    direction = "Outgoing";
                    _logger.LogWarning("TRANSFER DETECTED (Outgoing): {File} ({Size} bytes) TO {Source} by {Process}",
                        fileName, fileSize, source, processName);
                }
                else if (actionType is FileActionType.Delete)
                {
                    // Deleted from USB
                    direction = "DeleteExternal";
                }
            }

            // 2) Internet download: browser process creates/writes a file in Downloads or Desktop
            else if (browserProcesses.Contains(processName)
                     && (actionType is FileActionType.Create or FileActionType.Write)
                     && fileSize > 0)
            {
                flag = "InternetDownload";
                effectiveAction = FileActionType.Copy;
                isTransfer = true;
                direction = "Incoming";
                _logger.LogWarning("INTERNET DOWNLOAD: {File} ({Size} bytes) via {Browser}",
                    fileName, fileSize, processName);
            }

            // 3) Cross-correlation: file appeared locally while external drive connected
            else if (_knownDrives.Count > 0
                     && actionType == FileActionType.Create
                     && fileSize > 0)
            {
                flag = "ProbableUsbTransfer";
                effectiveAction = FileActionType.Copy;
                isTransfer = true;
                direction = "Incoming"; // USB connected, file created on C: -> Assume copied FROM USB
                _logger.LogWarning(
                    "PROBABLE USB TRANSFER (Incoming): {File} ({Size} bytes) by {Process} — drives: {Drives}",
                    fileName, fileSize, processName, string.Join(", ", _knownDrives));
            }

            // 4) Messaging / file-sharing apps: WhatsApp, Telegram, Slack, Teams, Discord, etc.
            else if (IsFileTransferApp(processName)
                     && (actionType is FileActionType.Create or FileActionType.Write)
                     && fileSize > 0)
            {
                flag = "AppTransfer";
                effectiveAction = FileActionType.Copy;
                isTransfer = true;
                direction = "Incoming"; // Usually receiving a file
                _logger.LogWarning("APP FILE TRANSFER: {File} ({Size} bytes) via {App}",
                    fileName, fileSize, processName);
            }

            var fileEvent = new FileEvent
            {
                DeviceId = _machineId,
                MachineId = _machineId,
                User = _currentUser,
                FileName = fileName,
                FullPath = newPath ?? fullPath,
                FileSize = fileSize,
                Sha256 = sha256,
                ActionType = effectiveAction,
                Timestamp = DateTime.UtcNow,
                ProcessName = processName,
                Flag = flag,
                Source = source,
                IsTransfer = isTransfer,
                Direction = direction
            };

            _onFileEvent(fileEvent);
            _logger.LogDebug("File event: {Action} {File} by {Process} ({Size} bytes) [source={Source}, flag={Flag}]",
                effectiveAction, fileName, processName, fileSize, source, flag);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling file event for {Path}", fullPath);
        }
    }

    /// <summary>
    /// Scans for ALL external drives — USB sticks (Removable), Type-C drives (often Fixed),
    /// external SSDs (Fixed), phone storage (Removable or Fixed), SD cards, etc.
    /// Any drive letter NOT in the startup baseline is treated as external.
    /// </summary>
    private void ScanForExternalDrives()
    {
        try
        {
            var currentDrives = DriveInfo.GetDrives()
                .Where(d => d.IsReady)
                .ToList();

            var currentPaths = currentDrives
                .Select(d => d.RootDirectory.FullName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            // ── Detect newly connected external drives ──
            foreach (var drive in currentDrives)
            {
                var root = drive.RootDirectory.FullName;

                // Skip drives that existed at startup (system/internal drives)
                // Exception: always pick up DriveType.Removable even if it was present at startup
                bool isNew = !_baselineDrives.Contains(root);
                bool isRemovable = drive.DriveType == DriveType.Removable;

                if ((isNew || isRemovable) && _knownDrives.Add(root))
                {
                    _logger.LogWarning(
                        "EXTERNAL DRIVE CONNECTED: {Drive} (Type={DriveType}, Label={Label}, Size={Size} GB)",
                        root, drive.DriveType, drive.VolumeLabel,
                        drive.TotalSize / (1024.0 * 1024 * 1024));

                    if (_watchedPaths.Add(root))
                        AddWatcher(root, "USB");
                }
            }

            // ── Detect removed drives ──
            var removed = _knownDrives.Where(d => !currentPaths.Contains(d)).ToList();
            foreach (var drive in removed)
            {
                _knownDrives.Remove(drive);
                _logger.LogWarning("EXTERNAL DRIVE REMOVED: {Drive}", drive);

                // Remove the watcher for this drive
                var deadWatcher = _watchers.FirstOrDefault(w =>
                    w.Path.Equals(drive, StringComparison.OrdinalIgnoreCase));
                if (deadWatcher != null)
                {
                    deadWatcher.EnableRaisingEvents = false;
                    deadWatcher.Dispose();
                    _watchers.Remove(deadWatcher);
                    _watchedPaths.Remove(drive);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error scanning external drives");
        }
    }

    private void ScanForNetworkDrives()
    {
        try
        {
            var networkDrives = DriveInfo.GetDrives()
                .Where(d => d.DriveType == DriveType.Network && d.IsReady);

            foreach (var drive in networkDrives)
            {
                var root = drive.RootDirectory.FullName;
                if (_watchedPaths.Add(root))
                {
                    _logger.LogWarning("NETWORK DRIVE DETECTED: {Drive}", root);
                    AddWatcher(root, "NetworkShare");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error scanning network drives");
        }
    }

    /// <summary>
    /// Re-scan for network drives periodically (they can be mapped after startup).
    /// Called from the same poll timer as external drives.
    /// </summary>
    private void RescanNetworkDrives()
    {
        try
        {
            var networkDrives = DriveInfo.GetDrives()
                .Where(d => d.DriveType == DriveType.Network && d.IsReady);

            foreach (var drive in networkDrives)
            {
                var root = drive.RootDirectory.FullName;
                if (_watchedPaths.Add(root))
                {
                    _logger.LogWarning("NEW NETWORK DRIVE MAPPED: {Drive}", root);
                    AddWatcher(root, "NetworkShare");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error re-scanning network drives");
        }
    }

    private static string ComputeSha256(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var hash = SHA256.HashData(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Best-effort: get the foreground process name.
    /// This is a heuristic — FileSystemWatcher doesn't natively report which process caused the event.
    /// </summary>
    /// <summary>
    /// Returns true if the given process name is a known file-sharing / messaging app.
    /// </summary>
    private static bool IsFileTransferApp(string processName)
    {
        var apps = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "whatsapp", "telegram", "slack", "teams", "discord",
            "skype", "zoom", "signal", "element", "thunderbird",
            "outlook", "filezilla", "winscp", "putty", "7zfm",
            "winrar", "torrent", "qbittorrent", "utorrent", "bittorrent",
            "sharex", "dropbox", "onedrive", "googledrivesync",
        };
        return apps.Contains(processName);
    }

    private static string GetLikelyProcess()
    {
        try
        {
            var hwnd = NativeMethods.GetForegroundWindow();
            NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid > 0)
            {
                var proc = System.Diagnostics.Process.GetProcessById((int)pid);
                return proc.ProcessName;
            }
        }
        catch { /* swallow */ }
        return "Unknown";
    }

    public void Dispose()
    {
        _usbPollTimer?.Dispose();
        foreach (var w in _watchers)
        {
            w.EnableRaisingEvents = false;
            w.Dispose();
        }
        _watchers.Clear();
    }
}
