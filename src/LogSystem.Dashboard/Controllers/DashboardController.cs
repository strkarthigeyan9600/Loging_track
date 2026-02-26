using Google.Cloud.Firestore;
using LogSystem.Dashboard.Data;
using Microsoft.AspNetCore.Mvc;

namespace LogSystem.Dashboard.Controllers;

/// <summary>
/// Dashboard query endpoints — provides data for the admin UI.
/// Reads directly from InMemoryStore — no Firestore dependency for reads.
/// Data is always available regardless of Firestore quota.
/// </summary>
[ApiController]
[Route("api/dashboard")]
public class DashboardController : ControllerBase
{
    private readonly InMemoryStore _store;

    public DashboardController(InMemoryStore store)
    {
        _store = store;
    }

    // ─── Devices ───

    [HttpGet("devices")]
    public IActionResult GetDevices()
    {
        return Ok(_store.GetDevices());
    }

    // ─── Alerts ───

    [HttpGet("alerts")]
    public IActionResult GetAlerts(
        [FromQuery] string? deviceId = null,
        [FromQuery] string? severity = null,
        [FromQuery] int hours = 24,
        [FromQuery] int limit = 100)
    {
        var cutoff = Timestamp.FromDateTime(DateTime.UtcNow.AddHours(-hours));
        return Ok(_store.GetAlerts(cutoff, deviceId, severity, limit));
    }

    // ─── File Events ───

    [HttpGet("file-events")]
    public IActionResult GetFileEvents(
        [FromQuery] string? deviceId = null,
        [FromQuery] string? flag = null,
        [FromQuery] int hours = 24,
        [FromQuery] int limit = 200)
    {
        var cutoff = Timestamp.FromDateTime(DateTime.UtcNow.AddHours(-hours));
        return Ok(_store.GetFileEvents(cutoff, deviceId, flag, limit));
    }

    // ─── File Transfers (USB / Network / Cloud) ───

    [HttpGet("transfers")]
    public IActionResult GetTransfers(
        [FromQuery] string? deviceId = null,
        [FromQuery] string? source = null,
        [FromQuery] int hours = 24,
        [FromQuery] int limit = 200)
    {
        var cutoff = Timestamp.FromDateTime(DateTime.UtcNow.AddHours(-hours));
        return Ok(_store.GetTransferEvents(cutoff, deviceId, source, limit));
    }

    // ─── Network Events ───

    [HttpGet("network-events")]
    public IActionResult GetNetworkEvents(
        [FromQuery] string? deviceId = null,
        [FromQuery] string? flag = null,
        [FromQuery] int hours = 24,
        [FromQuery] int limit = 200)
    {
        var cutoff = Timestamp.FromDateTime(DateTime.UtcNow.AddHours(-hours));
        return Ok(_store.GetNetworkEvents(cutoff, deviceId, flag, limit));
    }

    // ─── App Usage ───

    [HttpGet("app-usage")]
    public IActionResult GetAppUsage(
        [FromQuery] string? deviceId = null,
        [FromQuery] int hours = 24,
        [FromQuery] int limit = 200)
    {
        var cutoff = Timestamp.FromDateTime(DateTime.UtcNow.AddHours(-hours));
        return Ok(_store.GetAppUsageEvents(cutoff, deviceId, limit));
    }

    // ─── Summary / Statistics ───

    [HttpGet("summary")]
    public IActionResult GetSummary([FromQuery] int hours = 24)
    {
        var cutoff = Timestamp.FromDateTime(DateTime.UtcNow.AddHours(-hours));

        var totalDevices = _store.CountDevices();
        var activeDevices = _store.CountActiveDevices(cutoff);
        var totalAlerts = _store.CountAlerts(cutoff);
        var criticalAlerts = _store.CountAlerts(cutoff, "Critical");
        var highAlerts = _store.CountAlerts(cutoff, "High");
        var fileEvents = _store.CountFileEvents(cutoff);
        var flaggedFiles = _store.CountFileEvents(cutoff, flagFilter: "ProbableUpload");
        var transferEvents = _store.CountTransferEvents(cutoff);
        var networkEvents = _store.CountNetworkEvents(cutoff);

        var allNetEvents = _store.GetNetworkEventsForAggregation(cutoff);

        var topProcesses = allNetEvents
            .GroupBy(n => n.ProcessName)
            .Select(g => new
            {
                ProcessName = g.Key,
                TotalBytesSent = g.Sum(x => x.BytesSent),
                TotalBytesReceived = g.Sum(x => x.BytesReceived),
                EventCount = g.Count()
            })
            .OrderByDescending(x => x.TotalBytesSent)
            .Take(10)
            .ToList();

        var allAppEvents = _store.GetAppUsageForAggregation(cutoff);

        var topApps = allAppEvents
            .GroupBy(a => a.ApplicationName)
            .Select(g => new
            {
                ApplicationName = g.Key,
                TotalDurationMinutes = g.Sum(x => x.DurationSeconds) / 60.0,
                SessionCount = g.Count()
            })
            .OrderByDescending(x => x.TotalDurationMinutes)
            .Take(10)
            .ToList();

        return Ok(new
        {
            period = $"Last {hours} hours",
            totalDevices,
            activeDevices,
            totalAlerts,
            criticalAlerts,
            highAlerts,
            fileEvents,
            flaggedFiles,
            transferEvents,
            networkEvents,
            topProcesses,
            topApps
        });
    }

    // ─── Top Talkers ───

    [HttpGet("top-talkers")]
    public IActionResult GetTopTalkers([FromQuery] int hours = 24, [FromQuery] int limit = 10)
    {
        var cutoff = Timestamp.FromDateTime(DateTime.UtcNow.AddHours(-hours));
        var allNetEvents = _store.GetNetworkEventsForAggregation(cutoff);

        var topTalkers = allNetEvents
            .GroupBy(n => new { n.DeviceId, n.User })
            .Select(g => new
            {
                g.Key.DeviceId,
                g.Key.User,
                TotalBytesSent = g.Sum(x => x.BytesSent),
                TotalBytesReceived = g.Sum(x => x.BytesReceived),
                UniqueDestinations = g.Select(x => x.DestinationIp).Distinct().Count()
            })
            .OrderByDescending(x => x.TotalBytesSent)
            .Take(limit)
            .ToList();

        return Ok(topTalkers);
    }
}
