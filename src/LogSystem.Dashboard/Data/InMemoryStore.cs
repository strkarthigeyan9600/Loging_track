using System.Collections.Concurrent;
using Google.Cloud.Firestore;

namespace LogSystem.Dashboard.Data;

/// <summary>
/// Thread-safe in-memory store for all log data.
/// Acts as primary data source — always available regardless of Firestore quota.
/// Firestore writes happen best-effort in the background.
/// </summary>
public sealed class InMemoryStore
{
    private readonly ConcurrentDictionary<string, DeviceEntity> _devices = new();
    private readonly ConcurrentDictionary<string, FileEventEntity> _fileEvents = new();
    private readonly ConcurrentDictionary<string, NetworkEventEntity> _networkEvents = new();
    private readonly ConcurrentDictionary<string, AppUsageEventEntity> _appUsageEvents = new();
    private readonly ConcurrentDictionary<string, AlertEventEntity> _alertEvents = new();

    // ──────── Devices ────────

    public void UpsertDevice(DeviceEntity device)
    {
        _devices[device.DeviceId] = device;
    }

    public List<DeviceEntity> GetDevices()
    {
        return _devices.Values
            .OrderByDescending(d => d.LastSeen.ToDateTime())
            .ToList();
    }

    public int CountDevices() => _devices.Count;

    public int CountActiveDevices(Timestamp cutoff)
    {
        return _devices.Values.Count(d => d.LastSeen >= cutoff);
    }

    // ──────── File Events ────────

    public void AddFileEvents(IEnumerable<FileEventEntity> events)
    {
        foreach (var e in events)
            _fileEvents[e.Id] = e;
    }

    public List<FileEventEntity> GetFileEvents(Timestamp cutoff, string? deviceId = null, string? flag = null, int limit = 200)
    {
        var results = _fileEvents.Values.Where(e => e.Timestamp >= cutoff);

        if (!string.IsNullOrEmpty(deviceId))
            results = results.Where(e => e.DeviceId == deviceId);
        if (!string.IsNullOrEmpty(flag))
            results = results.Where(e => e.Flag == flag);

        return results.OrderByDescending(e => e.Timestamp.ToDateTime()).Take(limit).ToList();
    }

    public int CountFileEvents(Timestamp cutoff, string? flagFilter = null)
    {
        var results = _fileEvents.Values.Where(e => e.Timestamp >= cutoff);
        if (!string.IsNullOrEmpty(flagFilter))
            results = results.Where(e => e.Flag == flagFilter);
        return results.Count();
    }

    // ──────── Network Events ────────

    public void AddNetworkEvents(IEnumerable<NetworkEventEntity> events)
    {
        foreach (var e in events)
            _networkEvents[e.Id] = e;
    }

    public List<NetworkEventEntity> GetNetworkEvents(Timestamp cutoff, string? deviceId = null, string? flag = null, int limit = 200)
    {
        var results = _networkEvents.Values.Where(e => e.Timestamp >= cutoff);

        if (!string.IsNullOrEmpty(deviceId))
            results = results.Where(e => e.DeviceId == deviceId);
        if (!string.IsNullOrEmpty(flag))
            results = results.Where(e => e.Flag == flag);

        return results.OrderByDescending(e => e.Timestamp.ToDateTime()).Take(limit).ToList();
    }

    public int CountNetworkEvents(Timestamp cutoff)
    {
        return _networkEvents.Values.Count(e => e.Timestamp >= cutoff);
    }

    public List<NetworkEventEntity> GetNetworkEventsForAggregation(Timestamp cutoff)
    {
        return _networkEvents.Values.Where(e => e.Timestamp >= cutoff).ToList();
    }

    // ──────── App Usage Events ────────

    public void AddAppUsageEvents(IEnumerable<AppUsageEventEntity> events)
    {
        foreach (var e in events)
            _appUsageEvents[e.Id] = e;
    }

    public List<AppUsageEventEntity> GetAppUsageEvents(Timestamp cutoff, string? deviceId = null, int limit = 200)
    {
        var results = _appUsageEvents.Values.Where(e => e.StartTime >= cutoff);

        if (!string.IsNullOrEmpty(deviceId))
            results = results.Where(e => e.DeviceId == deviceId);

        return results.OrderByDescending(e => e.StartTime.ToDateTime()).Take(limit).ToList();
    }

    public List<AppUsageEventEntity> GetAppUsageForAggregation(Timestamp cutoff)
    {
        return _appUsageEvents.Values.Where(e => e.StartTime >= cutoff).ToList();
    }

    // ──────── Alert Events ────────

    public void AddAlertEvents(IEnumerable<AlertEventEntity> events)
    {
        foreach (var e in events)
            _alertEvents[e.Id] = e;
    }

    public List<AlertEventEntity> GetAlerts(Timestamp cutoff, string? deviceId = null, string? severity = null, int limit = 100)
    {
        var results = _alertEvents.Values.Where(e => e.Timestamp >= cutoff);

        if (!string.IsNullOrEmpty(deviceId))
            results = results.Where(e => e.DeviceId == deviceId);
        if (!string.IsNullOrEmpty(severity))
            results = results.Where(e => e.Severity == severity);

        return results.OrderByDescending(e => e.Timestamp.ToDateTime()).Take(limit).ToList();
    }

    public int CountAlerts(Timestamp cutoff, string? severity = null)
    {
        var results = _alertEvents.Values.Where(e => e.Timestamp >= cutoff);
        if (!string.IsNullOrEmpty(severity))
            results = results.Where(e => e.Severity == severity);
        return results.Count();
    }
}
