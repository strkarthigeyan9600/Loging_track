using System;
using System.IO;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;
using LogSystem.Shared.Configuration;
using LogSystem.Shared.Models;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LogSystem.Agent.Monitors;

public class ShellCopyMonitor : IDisposable
{
    private readonly ILogger<ShellCopyMonitor> _logger;
    private readonly Action<FileEvent> _onFileEvent;
    private readonly string _currentUser;
    private readonly string _machineId;
    private readonly CancellationTokenSource _cts = new();
    private Task? _monitoringTask;
    private TraceEventSession? _session;
    private static readonly Guid ShellCoreProvider = new Guid("30336ed4-e327-447c-9de0-51c652c86108");

    public ShellCopyMonitor(
        ILogger<ShellCopyMonitor> logger,
        IOptions<AgentConfiguration> config,
        Action<FileEvent> onFileEvent)
    {
        _logger = logger;
        _onFileEvent = onFileEvent;
        _currentUser = Environment.UserName;
        _machineId = config.Value.DeviceId; // Use DeviceId from config
    }

    public void Start()
    {
        // ETW requires Administrator privileges
        if (!IsAdministrator())
        {
            _logger.LogWarning("ShellCopyMonitor requires Administrator privileges. ETW monitoring disabled.");
            return;
        }

        _monitoringTask = Task.Run(() => StartEtwSession(_cts.Token));
    }

    private void StartEtwSession(CancellationToken token)
    {
        try
        {
            _logger.LogInformation("Starting ETW Shell Copy Engine Monitor...");
            // Use a unique session name
            using (_session = new TraceEventSession("LogSystemAgent-ShellCopyMonitor"))
            {
                // Clean up on cancel
                token.Register(() => _session.Stop());

                _session.EnableProvider(ShellCoreProvider, TraceEventLevel.Verbose, 0x0);

                _session.Source.Dynamic.All += data =>
                {
                    try
                    {
                        if (data.EventName.Contains("FileCreate") || data.EventName.Contains("FileOperation_Info"))
                        {
                            AnalyzeShellEvent(data);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing ETW event");
                    }
                };

                _session.Source.Process(); // Blocking call
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start ETW Shell Monitor");
        }
    }

    private void AnalyzeShellEvent(TraceEvent data)
    {
        // Look specifically for CopyEngine operations
        // Payload inspection can be tricky as it varies by Windows version, but usually contains parsing names for Source/Dest
        
        string? destPath = null;
        string? srcPath = null;

        for (int i = 0; i < data.PayloadNames.Length; i++)
        {
            var name = data.PayloadNames[i];
            var val = data.PayloadValue(i)?.ToString();
            if (string.IsNullOrEmpty(val)) continue;

            if (name.Contains("Dest", StringComparison.OrdinalIgnoreCase) || name.Contains("Target", StringComparison.OrdinalIgnoreCase))
            {
                destPath = val;
            }
            if (name.Contains("Source", StringComparison.OrdinalIgnoreCase) || name.Contains("Src", StringComparison.OrdinalIgnoreCase))
            {
                srcPath = val;
            }
        }

        if (string.IsNullOrEmpty(destPath)) return;

        // Check if destination is suspicious (MTP, USB, Network)
        string destinationType = CheckDestinationType(destPath);
        if (destinationType == "Local") return; // Ignore local copies (handled by FileMonitorService)

        // If we found an external transfer!
        var fileName = Path.GetFileName(srcPath ?? destPath);
        
        var evt = new FileEvent
        {
            MachineId = _machineId,
            DeviceId = _machineId,
            User = _currentUser,
            FileName = fileName,
            FullPath = destPath, // The destination is the interesting part
            ActionType = FileActionType.Copy,
            Timestamp = DateTime.UtcNow,
            ProcessName = "explorer", // Shell operations usually via Explorer
            Flag = destinationType + "Transfer",
            Source = destinationType,
            IsTransfer = true,
            Direction = "Outgoing" // Shell copy TO somewhere
        };
        
        // Try to get file size if source exists locally
        if (!string.IsNullOrEmpty(srcPath) && File.Exists(srcPath))
        {
            try { evt.FileSize = new FileInfo(srcPath).Length; } catch { }
        }

        _logger.LogWarning("Shell Copy Detected: {File} -> {Dest} ({Type})", fileName, destPath, destinationType);
        _onFileEvent(evt);
    }

    private string CheckDestinationType(string path)
    {
        // 1. Check for MTP / Shell Namespace paths (often start with ::{GUID} or "This PC")
        // Or sometimes generic "\Device\HarddiskVolume..."
        if (path.StartsWith("::", StringComparison.Ordinal) || path.StartsWith(@"\\?\USB", StringComparison.OrdinalIgnoreCase))
            return "MTP/Device";

        if (!path.Contains(':') && path.StartsWith(@"\\"))
            return "NetworkShare"; // UNC path

        try
        {
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root)) return "MTP/Device"; // No root often means virtual shell folder

            var drive = new DriveInfo(root);
            if (drive.DriveType == DriveType.Removable) return "USB";
            if (drive.DriveType == DriveType.Network) return "NetworkShare";
            if (drive.DriveType == DriveType.CDRom) return "Optical";
        }
        catch 
        {
            // If Path.GetPathRoot fails, it's likely a shell path (MTP)
            return "MTP/Device";
        }

        return "Local";
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _session?.Stop();
        _session?.Dispose();
        _monitoringTask?.Wait(1000);
    }
}
