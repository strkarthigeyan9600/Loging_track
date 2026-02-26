using LogSystem.Agent.Monitors;
using LogSystem.Agent.Services;
using LogSystem.Shared.Configuration;
using LogSystem.Shared.Models;
using Microsoft.Extensions.Options;

namespace LogSystem.Agent;

/// <summary>
/// Main Worker Service — orchestrates all monitoring modules.
/// Runs as a Windows Service (or console app during development).
/// </summary>
public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly IOptions<AgentConfiguration> _config;
    private readonly LocalEventQueue _queue;
    private readonly LogUploaderService _uploader;

    private FileMonitorService? _fileMonitor;
    private AppMonitorService? _appMonitor;
    private ShellCopyMonitor? _shellCopyMonitor;
    private NetworkMonitorService? _networkMonitor;
    private CorrelationEngine? _correlationEngine;

    public Worker(
        ILogger<Worker> logger,
        IOptions<AgentConfiguration> config,
        LocalEventQueue queue,
        LogUploaderService uploader)
    {
        _logger = logger;
        _config = config;
        _queue = queue;
        _uploader = uploader;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("=== LogSystem Agent starting on {Machine} ({User}) ===",
            Environment.MachineName, Environment.UserName);
        _logger.LogInformation("Device ID: {DeviceId}", _config.Value.DeviceId);

        try
        {
            InitializeModules();
            StartAllModules();

            _logger.LogInformation("All modules initialized. Agent is running.");

            // Main loop — periodic flush and health logging
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await _queue.FlushToDiskAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error during periodic flush");
                }

                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Agent shutdown requested.");
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Fatal error in agent worker");
            throw;
        }
        finally
        {
            ShutdownModules();
            _logger.LogInformation("=== LogSystem Agent stopped ===");
        }
    }

    private void InitializeModules()
    {
        // Initialize Correlation Engine
        _correlationEngine = new CorrelationEngine(
            _logger as ILogger<CorrelationEngine>
                ?? Microsoft.Extensions.Logging.LoggerFactoryExtensions.CreateLogger<CorrelationEngine>(
                    new LoggerFactory()),
            _config,
            onAlert: alert =>
            {
                _queue.EnqueueAlert(alert);
                _logger.LogWarning("ALERT [{Severity}] {Type}: {Desc}",
                    alert.Severity, alert.AlertType, alert.Description);
            },
            onFlaggedNetwork: evt => _queue.EnqueueNetworkEvent(evt),
            onFlaggedFile: evt => _queue.EnqueueFileEvent(evt));

        // Initialize File Monitor
        _fileMonitor = new FileMonitorService(
            _logger as ILogger<FileMonitorService>
                ?? Microsoft.Extensions.Logging.LoggerFactoryExtensions.CreateLogger<FileMonitorService>(
                    new LoggerFactory()),
            _config,
            onFileEvent: evt =>
            {
                _queue.EnqueueFileEvent(evt);
                _correlationEngine.RegisterFileRead(evt);
            });

        // Initialize Shell Copy Monitor (ETW)
        _shellCopyMonitor = new ShellCopyMonitor(
            _logger as ILogger<ShellCopyMonitor>
                ?? Microsoft.Extensions.Logging.LoggerFactoryExtensions.CreateLogger<ShellCopyMonitor>(
                    new LoggerFactory()),
             _config,
            onFileEvent: evt => _queue.EnqueueFileEvent(evt));

        // Initialize App Monitor
        _appMonitor = new AppMonitorService(
            _logger as ILogger<AppMonitorService>
                ?? Microsoft.Extensions.Logging.LoggerFactoryExtensions.CreateLogger<AppMonitorService>(
                    new LoggerFactory()),
            _config,
            onAppEvent: evt => _queue.EnqueueAppEvent(evt));

        // Initialize Network Monitor
        _networkMonitor = new NetworkMonitorService(
            _logger as ILogger<NetworkMonitorService>
                ?? Microsoft.Extensions.Logging.LoggerFactoryExtensions.CreateLogger<NetworkMonitorService>(
                    new LoggerFactory()),
            _config,
            onNetworkEvent: evt =>
            {
                _queue.EnqueueNetworkEvent(evt);
                _correlationEngine.ProcessNetworkEvent(evt);
            });
    }

    private void StartAllModules()
    {
        _fileMonitor?.Start();
        _shellCopyMonitor?.Start();
        _appMonitor?.Start();
        _networkMonitor?.Start();
        _uploader.Start();

        _logger.LogInformation("Modules started: File={File}, ShellMon=True, App={App}, Network={Net}, Correlation={Corr}",
            _config.Value.FileMonitor.Enabled,
            _config.Value.AppMonitor.Enabled,
            _config.Value.NetworkMonitor.Enabled,
            _config.Value.Correlation.Enabled);
    }

    private void ShutdownModules()
    {
        _logger.LogInformation("Shutting down modules...");
        _fileMonitor?.Dispose();
        _shellCopyMonitor?.Dispose();
        _appMonitor?.Dispose();
        _networkMonitor?.Dispose();
        _correlationEngine?.Dispose();
        _uploader.Dispose();
        _queue.Dispose();
    }
}
