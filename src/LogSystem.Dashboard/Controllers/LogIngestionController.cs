using Google.Cloud.Firestore;
using LogSystem.Dashboard.Data;
using LogSystem.Shared.Models;
using Microsoft.AspNetCore.Mvc;

namespace LogSystem.Dashboard.Controllers;

/// <summary>
/// Receives log batches from agents.
/// Validates API key. Stores events in-memory FIRST (always succeeds),
/// then attempts Firestore persistence in the background (best-effort).
/// </summary>
[ApiController]
[Route("api/logs")]
public class LogIngestionController : ControllerBase
{
    private readonly InMemoryStore _store;
    private readonly FirestoreService _firestore;
    private readonly ILogger<LogIngestionController> _logger;
    private readonly IConfiguration _configuration;

    public LogIngestionController(
        InMemoryStore store,
        FirestoreService firestore,
        ILogger<LogIngestionController> logger,
        IConfiguration configuration)
    {
        _store = store;
        _firestore = firestore;
        _logger = logger;
        _configuration = configuration;
    }

    [HttpPost("ingest")]
    public Task<IActionResult> Ingest([FromBody] LogBatch batch)
    {
        // Validate API key
        var apiKey = Request.Headers["X-Api-Key"].FirstOrDefault();
        var expectedKey = _configuration["Dashboard:ApiKey"];
        if (string.IsNullOrEmpty(apiKey) || apiKey != expectedKey)
        {
            _logger.LogWarning("Unauthorized ingest attempt from {Ip}", HttpContext.Connection.RemoteIpAddress);
            return Task.FromResult<IActionResult>(Unauthorized(new { error = "Invalid API key" }));
        }

        if (batch == null)
            return Task.FromResult<IActionResult>(BadRequest(new { error = "Empty batch" }));

        // ── Build entity lists ──
        DeviceEntity? deviceEntity = null;
        List<FileEventEntity> fileEntities = new();
        List<NetworkEventEntity> netEntities = new();
        List<AppUsageEventEntity> appEntities = new();
        List<AlertEventEntity> alertEntities = new();

        if (batch.DeviceInfo != null)
        {
            deviceEntity = new DeviceEntity
            {
                DeviceId = batch.DeviceInfo.DeviceId,
                Hostname = batch.DeviceInfo.Hostname,
                User = batch.DeviceInfo.User,
                LastSeen = Timestamp.FromDateTime(batch.DeviceInfo.LastSeen.ToUniversalTime()),
                OsVersion = batch.DeviceInfo.OsVersion,
                AgentVersion = batch.DeviceInfo.AgentVersion
            };
        }

        if (batch.FileEvents.Count > 0)
        {
            fileEntities = batch.FileEvents.Select(fe => new FileEventEntity
            {
                Id = fe.Id,
                DeviceId = fe.DeviceId,
                User = fe.User,
                FileName = fe.FileName,
                FullPath = fe.FullPath,
                FileSize = fe.FileSize,
                Sha256 = fe.Sha256,
                ActionType = fe.ActionType.ToString(),
                Timestamp = Timestamp.FromDateTime(fe.Timestamp.ToUniversalTime()),
                ProcessName = fe.ProcessName,
                Flag = fe.Flag,
                Source = fe.Source
            }).ToList();
        }

        if (batch.NetworkEvents.Count > 0)
        {
            netEntities = batch.NetworkEvents.Select(ne => new NetworkEventEntity
            {
                Id = ne.Id,
                DeviceId = ne.DeviceId,
                User = ne.User,
                ProcessName = ne.ProcessName,
                ProcessId = ne.ProcessId,
                BytesSent = ne.BytesSent,
                BytesReceived = ne.BytesReceived,
                DestinationIp = ne.DestinationIp,
                DestinationPort = ne.DestinationPort,
                DurationSeconds = ne.Duration.TotalSeconds,
                Timestamp = Timestamp.FromDateTime(ne.Timestamp.ToUniversalTime()),
                Flag = ne.Flag
            }).ToList();
        }

        if (batch.AppUsageEvents.Count > 0)
        {
            appEntities = batch.AppUsageEvents.Select(ae => new AppUsageEventEntity
            {
                Id = ae.Id,
                DeviceId = ae.DeviceId,
                User = ae.User,
                ApplicationName = ae.ApplicationName,
                WindowTitle = ae.WindowTitle,
                StartTime = Timestamp.FromDateTime(ae.StartTime.ToUniversalTime()),
                DurationSeconds = ae.Duration.TotalSeconds,
                ProcessId = ae.ProcessId
            }).ToList();
        }

        if (batch.Alerts.Count > 0)
        {
            alertEntities = batch.Alerts.Select(alert => new AlertEventEntity
            {
                Id = alert.Id,
                DeviceId = alert.DeviceId,
                User = alert.User,
                Severity = alert.Severity.ToString(),
                AlertType = alert.AlertType,
                Description = alert.Description,
                RelatedFileName = alert.RelatedFileName,
                RelatedProcessName = alert.RelatedProcessName,
                BytesInvolved = alert.BytesInvolved,
                Timestamp = Timestamp.FromDateTime(alert.Timestamp.ToUniversalTime())
            }).ToList();
        }

        // ── Step 1: Write to in-memory store (ALWAYS succeeds) ──
        if (deviceEntity != null) _store.UpsertDevice(deviceEntity);
        if (fileEntities.Count > 0) _store.AddFileEvents(fileEntities);
        if (netEntities.Count > 0) _store.AddNetworkEvents(netEntities);
        if (appEntities.Count > 0) _store.AddAppUsageEvents(appEntities);
        if (alertEntities.Count > 0) _store.AddAlertEvents(alertEntities);

        var total = batch.FileEvents.Count + batch.NetworkEvents.Count +
                    batch.AppUsageEvents.Count + batch.Alerts.Count;

        _logger.LogInformation("Ingested {Count} events from device {DeviceId} (in-memory OK)", total, batch.DeviceId);

        // ── Step 2: Persist to Firestore in background (best-effort) ──
        _ = Task.Run(async () =>
        {
            try
            {
                if (deviceEntity != null)
                    await _firestore.UpsertDeviceAsync(deviceEntity);

                foreach (var chunk in Chunk(fileEntities, 450))
                    await _firestore.AddFileEventsBatchAsync(chunk);

                foreach (var chunk in Chunk(netEntities, 450))
                    await _firestore.AddNetworkEventsBatchAsync(chunk);

                foreach (var chunk in Chunk(appEntities, 450))
                    await _firestore.AddAppUsageEventsBatchAsync(chunk);

                foreach (var chunk in Chunk(alertEntities, 450))
                    await _firestore.AddAlertEventsBatchAsync(chunk);

                _logger.LogDebug("Firestore sync OK for batch from {DeviceId}", batch.DeviceId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Firestore sync failed for batch from {DeviceId} (data preserved in-memory)", batch.DeviceId);
            }
        });

        // Return success immediately — data is in memory
        return Task.FromResult<IActionResult>(Ok(new { received = total }));
    }

    private static IEnumerable<IEnumerable<T>> Chunk<T>(IEnumerable<T> source, int size)
    {
        var list = source.ToList();
        for (int i = 0; i < list.Count; i += size)
            yield return list.Skip(i).Take(size);
    }
}
