# LogSystem — Endpoint Monitoring Agent & Dashboard

A lightweight Windows endpoint monitoring system for internal teams (20–25 machines).  
Built with **C# / .NET 8**, runs as a Windows Service, no kernel drivers required.

> **Admin (Dashboard):** `192.168.0.169`  
> **Monitored System (Agent):** `192.168.0.213`

---

## Table of Contents

1. [Overview](#overview)  
2. [How It Works — End-to-End Workflow](#how-it-works--end-to-end-workflow)  
3. [Architecture Diagram](#architecture-diagram)  
4. [Project Structure](#project-structure)  
5. [Component Details](#component-details)  
   - [LogSystem.Shared](#logsystemshared--shared-library)  
   - [LogSystem.Agent](#logsystemagent--monitoring-agent)  
   - [LogSystem.Dashboard](#logsystemdashboard--admin-server--web-ui)  
6. [Data Flow — Step by Step](#data-flow--step-by-step)  
7. [Detection Capabilities](#detection-capabilities)  
8. [Correlation Rules](#correlation-rules)  
9. [API Endpoints](#api-endpoints)  
10. [Configuration Reference](#configuration-reference)  
11. [Deployment — Two-Computer Setup](#deployment--two-computer-setup)  
12. [Security](#security)  
13. [Troubleshooting](#troubleshooting)  

---

## Overview

LogSystem is a **client-server monitoring system** with two main components:

| Component | Role | Runs On |
|---|---|---|
| **LogSystem.Agent** | Silently monitors file activity, app usage, network connections, and raises alerts | Each employee machine (monitored system) |
| **LogSystem.Dashboard** | Receives logs from agents, stores them, and serves a real-time web dashboard | Admin/server machine |

The Agent collects data → encrypts it locally → uploads it over HTTP to the Dashboard → the Dashboard stores it in an **in-memory store** (with optional Firebase Firestore backup) → the admin views everything in a **live web dashboard**.

---

## How It Works — End-to-End Workflow

```
 ┌─────────────────────────────────────────────────────────────────────┐
 │                    MONITORED SYSTEM (192.168.0.213)                 │
 │                                                                     │
 │  ┌─────────────┐  ┌─────────────┐  ┌──────────────┐                │
 │  │ FILE MONITOR │  │ APP MONITOR │  │  NET MONITOR  │               │
 │  │ (FSWatcher)  │  │(Win32 API)  │  │(TCP Table)    │               │
 │  └──────┬───────┘  └──────┬──────┘  └──────┬────────┘               │
 │         │                 │                 │                        │
 │         ▼                 ▼                 ▼                        │
 │  ┌──────────────────────────────────────────────┐                   │
 │  │         CORRELATION ENGINE (Rules)            │                   │
 │  │  • Large Transfer ≥ 25 MB → Alert             │                   │
 │  │  • Continuous Transfer > 30 MB/10 min → Alert │                   │
 │  │  • File Read + Upload > 5 MB/15s → Alert      │                   │
 │  └──────────────────────┬───────────────────────┘                   │
 │                         │                                            │
 │                         ▼                                            │
 │  ┌──────────────────────────────────────────────┐                   │
 │  │       LOCAL ENCRYPTED QUEUE (AES-256-GCM)     │                   │
 │  │       C:\ProgramData\LogSystem\queue          │                   │
 │  └──────────────────────┬───────────────────────┘                   │
 │                         │                                            │
 │                         ▼                                            │
 │  ┌──────────────────────────────────────────────┐                   │
 │  │       LOG UPLOADER SERVICE (HTTP + API Key)   │                   │
 │  │       POST → http://192.168.0.169:5176        │                   │
 │  │       Retry with exponential backoff          │                   │
 │  └──────────────────────┬───────────────────────┘                   │
 │                         │                                            │
 └─────────────────────────┼────────────────────────────────────────────┘
                           │  HTTP POST /api/logs/ingest
                           │  Headers: X-Api-Key, X-Device-Id
                           ▼
 ┌─────────────────────────────────────────────────────────────────────┐
 │                     ADMIN SYSTEM (192.168.0.169)                    │
 │                                                                     │
 │  ┌──────────────────────────────────────────────┐                   │
 │  │       LOG INGESTION CONTROLLER                │                   │
 │  │       • Validates API Key                     │                   │
 │  │       • Parses LogBatch JSON                  │                   │
 │  └──────────────────────┬───────────────────────┘                   │
 │                         │                                            │
 │              ┌──────────┴──────────┐                                │
 │              ▼                     ▼                                 │
 │  ┌─────────────────┐   ┌─────────────────────┐                     │
 │  │  IN-MEMORY STORE │   │ FIRESTORE (BACKUP)  │                     │
 │  │  (Primary — fast │   │ (Background sync,   │                     │
 │  │   always works)  │   │  best-effort)       │                     │
 │  └────────┬─────────┘   └─────────────────────┘                     │
 │           │                                                          │
 │           ▼                                                          │
 │  ┌──────────────────────────────────────────────┐                   │
 │  │       DASHBOARD CONTROLLER (REST API)         │                   │
 │  │       /api/dashboard/summary                  │                   │
 │  │       /api/dashboard/devices                  │                   │
 │  │       /api/dashboard/alerts                   │                   │
 │  │       /api/dashboard/file-events              │                   │
 │  │       /api/dashboard/network-events           │                   │
 │  │       /api/dashboard/app-usage                │                   │
 │  └──────────────────────┬───────────────────────┘                   │
 │                         │                                            │
 │                         ▼                                            │
 │  ┌──────────────────────────────────────────────┐                   │
 │  │       WEB DASHBOARD (index.html)              │                   │
 │  │       http://192.168.0.169:5176               │                   │
 │  │       • Real-time device list                 │                   │
 │  │       • File/Network/App usage tables         │                   │
 │  │       • Alert severity badges                 │                   │
 │  │       • Auto-refresh every 2 minutes          │                   │
 │  └──────────────────────────────────────────────┘                   │
 │                                                                     │
 └─────────────────────────────────────────────────────────────────────┘
```

---

## Architecture Diagram

```
┌────────────────────┐          HTTP POST           ┌────────────────────────┐
│  LogSystem.Agent   │ ────────────────────────────► │  LogSystem.Dashboard   │
│  (Windows Service) │   /api/logs/ingest            │  (ASP.NET Core API)    │
│                    │   X-Api-Key auth              │                        │
│  Monitors:         │                               │  Storage:              │
│  ├── FileMonitor   │          HTTP GET             │  ├── InMemoryStore     │
│  ├── AppMonitor    │ ◄──── (admin browser) ──────► │  ├── FirestoreService  │
│  ├── NetMonitor    │                               │  │                     │
│  └── Correlation   │                               │  Web UI:              │
│                    │                               │  └── wwwroot/index.html│
│  Queue:            │                               │                        │
│  └── AES-256-GCM   │                               │  Swagger:              │
│     encrypted files │                               │  └── /swagger          │
└────────────────────┘                               └────────────────────────┘
     192.168.0.213                                        192.168.0.169:5176
```

---

## Project Structure

```
LogSystem/
├── LogSystem.sln                    # Visual Studio solution file
├── firebase.json                    # Firebase project config
├── firestore.rules                  # Firestore security rules
├── firestore.indexes.json           # Firestore index definitions
│
├── scripts/
│   ├── Install-Agent.ps1            # Deploy agent as Windows Service
│   ├── Uninstall-Agent.ps1          # Remove agent service
│   └── Start-Dashboard.ps1          # Run dashboard locally
│
├── docs/
│   └── SETUP_GUIDE.md               # Step-by-step Firebase setup
│
└── src/
    ├── LogSystem.Shared/            # ── SHARED LIBRARY ──
    │   ├── LogSystem.Shared.csproj
    │   ├── Models/
    │   │   ├── FileEvent.cs         # File operation event model
    │   │   ├── NetworkEvent.cs      # TCP connection event model
    │   │   ├── AppUsageEvent.cs     # Application window tracking model
    │   │   ├── AlertEvent.cs        # Security alert model
    │   │   ├── DeviceInfo.cs        # Machine identity model
    │   │   └── LogBatch.cs          # Upload envelope (batches all events)
    │   └── Configuration/
    │       └── AgentConfiguration.cs # All agent config classes
    │
    ├── LogSystem.Agent/             # ── MONITORING AGENT ──
    │   ├── LogSystem.Agent.csproj   # Targets net8.0-windows (Win32 APIs)
    │   ├── Program.cs               # Host builder, DI, Windows Service setup
    │   ├── Worker.cs                # Main BackgroundService orchestrator
    │   ├── NativeMethods.cs         # P/Invoke for Win32 (GetForegroundWindow)
    │   ├── appsettings.json         # Agent configuration
    │   ├── Monitors/
    │   │   ├── FileMonitorService.cs     # FileSystemWatcher-based file tracking
    │   │   ├── AppMonitorService.cs      # Win32 API foreground window polling
    │   │   ├── NetworkMonitorService.cs  # TCP table (GetExtendedTcpTable) polling
    │   │   └── CorrelationEngine.cs      # Rule engine for suspicious patterns
    │   ├── Services/
    │   │   ├── LocalEventQueue.cs        # AES-256-GCM encrypted disk queue
    │   │   └── LogUploaderService.cs     # HTTP uploader with retry/backoff
    │   └── Properties/
    │       └── launchSettings.json
    │
    └── LogSystem.Dashboard/         # ── ADMIN SERVER & WEB UI ──
        ├── LogSystem.Dashboard.csproj
        ├── Program.cs                # ASP.NET Core startup, Firebase init, middleware
        ├── appsettings.json          # Dashboard config (Firebase, API key)
        ├── Controllers/
        │   ├── LogIngestionController.cs  # POST /api/logs/ingest (receives agent data)
        │   └── DashboardController.cs     # GET endpoints (serves dashboard queries)
        ├── Data/
        │   ├── InMemoryStore.cs           # Primary data store (ConcurrentDictionary)
        │   ├── FirestoreService.cs        # Firebase Firestore read/write service
        │   └── LogSystemDbContext.cs      # Firestore entity classes (document models)
        ├── Converters/
        │   └── FirestoreTimestampJsonConverter.cs  # JSON ↔ Firestore Timestamp
        ├── Filters/
        │   └── FirestoreExceptionFilter.cs # Graceful gRPC/Firestore error handling
        ├── wwwroot/
        │   └── index.html             # Single-page dashboard UI (vanilla JS)
        └── Properties/
            └── launchSettings.json
```

---

## Component Details

### LogSystem.Shared — Shared Library

Referenced by both Agent and Dashboard. Contains:

| Model | Purpose | Key Fields |
|---|---|---|
| `FileEvent` | Every file operation detected | `FileName`, `FullPath`, `FileSize`, `ActionType` (Read/Write/Copy/Move/Delete/Rename/Create), `ProcessName`, `Sha256`, `Flag` |
| `NetworkEvent` | Every outbound TCP connection | `ProcessName`, `BytesSent`, `BytesReceived`, `DestinationIp`, `DestinationPort`, `Duration`, `Flag` |
| `AppUsageEvent` | Foreground application sessions | `ApplicationName`, `WindowTitle`, `StartTime`, `Duration`, `ProcessId` |
| `AlertEvent` | Security alerts from correlation | `Severity` (Low/Medium/High/Critical), `AlertType` (LargeTransfer/ContinuousTransfer/ProbableUpload), `Description`, `BytesInvolved` |
| `DeviceInfo` | Machine identity | `DeviceId`, `Hostname`, `User`, `OsVersion`, `AgentVersion`, `LastSeen` |
| `LogBatch` | Upload envelope containing all of the above | `DeviceId`, `FileEvents[]`, `NetworkEvents[]`, `AppUsageEvents[]`, `Alerts[]`, `DeviceInfo` |
| `AgentConfiguration` | All config classes for agent behavior | FileMonitor, AppMonitor, NetworkMonitor, Correlation, Security configs |

---

### LogSystem.Agent — Monitoring Agent

Runs on each monitored computer as a **Windows Service** (or console app in dev mode).

#### Module 1: FileMonitorService
- Uses `FileSystemWatcher` to track **all file operations** under configured watch paths (default: `C:\Users`)
- Detects: Create, Modify, Delete, Rename
- Tracks: file name, full path, file size, which process accessed it
- Monitors: USB drives, network shares, cloud sync folders (OneDrive, Google Drive, Dropbox)
- Computes **SHA-256 hash** for files in sensitive directories
- Excludes noise: `.tmp`, `.log`, `.etl`, `.pf`, `.sys`

#### Module 2: AppMonitorService
- Polls every **3 seconds** using Win32 `GetForegroundWindow()` API
- Records which application is in the foreground and its **window title**
- Tracks session duration (how long each app was focused)
- Excludes system processes: idle, svchost, csrss, dwm, etc.

#### Module 3: NetworkMonitorService
- Polls the **Windows TCP connection table** (`GetExtendedTcpTable`) every 5 seconds
- Records: process name, bytes sent/received, destination IP:port, connection duration
- Identifies which process is making each connection
- Filters out private/internal subnet traffic (configurable)

#### Module 4: CorrelationEngine
- Cross-references events from all three monitors in real-time
- Applies **3 detection rules** (see [Correlation Rules](#correlation-rules))
- Generates `AlertEvent` objects with severity levels
- Links alerts to the related files and processes

#### LocalEventQueue
- Thread-safe in-memory buffer that flushes to disk
- Files encrypted with **AES-256-GCM** (key derived via PBKDF2 with 100,000 iterations)
- Tamper detection on queue files
- Location: `C:\ProgramData\LogSystem\queue`

#### LogUploaderService
- Runs on a timer (default: every **60 seconds**)
- Dequeues batches from the local queue
- Sends HTTP POST to `http://192.168.0.169:5176/api/logs/ingest`
- Includes headers: `X-Api-Key`, `X-Device-Id`
- **Retry with exponential backoff** (max 3 retries, max 5-minute delay)
- If the server is down, events remain safely queued locally

#### Worker.cs — Orchestrator
- Entry point for the `BackgroundService`
- Initializes all 4 modules + uploader
- Auto-generates `DeviceId` as `MACHINENAME-USERNAME`
- Manages graceful shutdown

---

### LogSystem.Dashboard — Admin Server & Web UI

Runs on the admin machine. Receives logs and serves the dashboard.

#### LogIngestionController (`POST /api/logs/ingest`)
1. Validates the `X-Api-Key` header against the configured secret
2. Parses the `LogBatch` JSON body
3. **Writes all data to InMemoryStore** (always succeeds — instant, no external dependency)
4. **Attempts Firestore sync in background** (best-effort — if quota is exhausted, data is still safe in memory)
5. Returns `200 OK { received: N }` immediately

#### InMemoryStore (Primary Data Store)
- Thread-safe `ConcurrentDictionary` for each collection (devices, file events, network events, app usage, alerts)
- All dashboard reads come from here — **zero external network calls**
- Data persists as long as the server is running
- No quota limits, no rate limiting, instant reads

#### FirestoreService (Backup Data Store)
- Reads/writes to Google Cloud Firestore
- Used as **background backup** only
- If Firestore quota is exhausted, the system continues operating normally via InMemoryStore

#### DashboardController (REST API)
- All `GET` endpoints read directly from InMemoryStore
- Returns JSON data for summary stats, device lists, alerts, file/network/app events
- No async Firestore calls — all responses are instant

#### Web Dashboard (`wwwroot/index.html`)
- Single-page app using vanilla HTML/CSS/JavaScript
- Dark theme with responsive layout
- **6 summary cards**: Active Devices, Total Alerts, Critical Alerts, File Events, Flagged Files, Network Events
- **5 tabs**: Alerts, File Events, Network, App Usage, Devices
- Device filter dropdown and time range selector (1h / 6h / 24h / 3d / 7d)
- Auto-refreshes every **2 minutes**
- Severity badges: Critical (red), High (orange), Medium (yellow), Low (green)
- Flag badges: ProbableUpload, LargeTransfer, ContinuousTransfer

---

## Data Flow — Step by Step

Here's exactly what happens when an employee opens a file on the monitored system:

1. **Employee opens `Report.xlsx`** on the monitored PC (192.168.0.213)

2. **FileMonitorService** detects the file access via `FileSystemWatcher`
   - Creates a `FileEvent` with: fileName=`Report.xlsx`, actionType=`Read`, processName=`EXCEL.EXE`, fileSize=`2.4 MB`

3. **CorrelationEngine** receives the file read event
   - Watches for a matching network upload within 15 seconds

4. **If the user uploads the file** (e.g., via browser), **NetworkMonitorService** detects outbound traffic from `chrome.exe`
   - CorrelationEngine cross-references: file read + network send > 5 MB within 15s
   - Generates: `AlertEvent { Severity=High, AlertType=ProbableUpload, RelatedFileName=Report.xlsx }`

5. **LocalEventQueue** buffers all events in memory, flushes to AES-256-GCM encrypted files on disk

6. **LogUploaderService** (every 60s) packages events into a `LogBatch` and sends:
   ```
   POST http://192.168.0.169:5176/api/logs/ingest
   Headers: X-Api-Key: bLgyHLk0FVzyvIwH/WW50qP7O0onuA7MYxVsCTbEHcQ=
   Body: { deviceId, deviceInfo, fileEvents: [...], networkEvents: [...], alerts: [...] }
   ```

7. **LogIngestionController** validates the API key → writes to **InMemoryStore** → returns `200 OK`

8. **Dashboard web page** (auto-refreshes every 2 min) calls `GET /api/dashboard/summary` etc.
   - DashboardController reads from InMemoryStore → returns JSON
   - Browser renders: new device in Devices tab, alert in Alerts tab, file event in File Events tab

9. **Admin sees** the alert: "ProbableUpload — Report.xlsx may have been uploaded by chrome.exe (2.4 MB)"

---

## Detection Capabilities

| Capability | Accuracy | How It Works |
|---|---|---|
| File operations (create/read/write/delete/rename) | **100%** — exact file names | `FileSystemWatcher` on configured paths |
| USB drive activity | **100%** | Watches removable drive letters |
| Cloud sync folder activity (OneDrive, Google Drive, Dropbox) | **100%** | Monitors auto-detected cloud sync directories |
| Application usage & window titles | **100%** | Win32 `GetForegroundWindow` polling every 3s |
| Network connections per process | **100%** | Windows TCP table (`GetExtendedTcpTable`) |
| Large data transfers ≥ 25 MB | **100%** | Byte counter per connection |
| Slow/continuous exfiltration (30 MB over 10 min) | **High probability** | Sliding window aggregation |
| Browser/app file uploads | **High probability** | Correlation: file read + network send timing |
| Encrypted traffic content | **Not attempted** | By design — monitors metadata only |

---

## Correlation Rules

### Rule 1 — Large Transfer (Critical)
```
IF any process sends ≥ 25 MB outbound in a single connection
THEN → ALERT: LargeTransfer (Critical severity)
```

### Rule 2 — Continuous Small Transfers (High)
```
IF total outbound bytes > 30 MB within a 10-minute sliding window
AND involves multiple connections from the same process
THEN → ALERT: ContinuousTransfer (High severity)
```

### Rule 3 — Probable File Upload (High)
```
IF a file read event occurs
AND the same process sends > 5 MB outbound within 15 seconds
THEN → ALERT: ProbableUpload (High severity)
     → Flag the file event as "ProbableUpload"
```

---

## API Endpoints

### Ingestion (Agent → Dashboard)

| Method | Path | Auth | Description |
|---|---|---|---|
| `POST` | `/api/logs/ingest` | `X-Api-Key` header | Receive a `LogBatch` from an agent |

### Dashboard Queries (Browser → Dashboard)

| Method | Path | Description |
|---|---|---|
| `GET` | `/api/dashboard/summary?hours=24` | Summary statistics (counts, top processes, top apps) |
| `GET` | `/api/dashboard/devices` | List all registered devices with last-seen time |
| `GET` | `/api/dashboard/alerts?hours=24&limit=100&deviceId=...&severity=High` | Alert events |
| `GET` | `/api/dashboard/file-events?hours=24&limit=200&deviceId=...&flag=ProbableUpload` | File activity |
| `GET` | `/api/dashboard/network-events?hours=24&limit=200&deviceId=...` | Network connections |
| `GET` | `/api/dashboard/app-usage?hours=24&limit=200&deviceId=...` | Application usage sessions |
| `GET` | `/api/dashboard/top-talkers?hours=24&limit=10` | Devices with highest network traffic |

### Swagger UI

Available in development mode at: `http://192.168.0.169:5176/swagger`

---

## Configuration Reference

### Agent Configuration (`src/LogSystem.Agent/appsettings.json`)

| Setting | Default | Description |
|---|---|---|
| `AgentConfiguration.DeviceId` | (auto: `MACHINE-USER`) | Unique device identifier |
| `AgentConfiguration.ApiEndpoint` | `http://192.168.0.169:5176` | Dashboard server URL |
| `AgentConfiguration.ApiKey` | `bLgyHLk0FVz...` | Shared secret for authentication |
| `AgentConfiguration.UploadIntervalSeconds` | `60` | Seconds between upload cycles |
| `AgentConfiguration.MaxBatchSize` | `500` | Max events per upload batch |
| **File Monitor** | | |
| `FileMonitor.Enabled` | `true` | Enable/disable file tracking |
| `FileMonitor.WatchPaths` | `["C:\\Users"]` | Directories to monitor |
| `FileMonitor.MonitorUsb` | `true` | Watch removable USB drives |
| `FileMonitor.MonitorNetworkShares` | `true` | Watch mapped network drives |
| `FileMonitor.ComputeSha256ForSensitive` | `true` | Hash files in sensitive dirs |
| `FileMonitor.ExcludedExtensions` | `.tmp, .log, .etl, .pf, .sys` | Ignore these file types |
| **App Monitor** | | |
| `AppMonitor.Enabled` | `true` | Enable/disable app tracking |
| `AppMonitor.PollingIntervalMs` | `3000` | Foreground window poll rate |
| `AppMonitor.ExcludedProcesses` | `idle, svchost, csrss...` | System processes to ignore |
| **Network Monitor** | | |
| `NetworkMonitor.Enabled` | `true` | Enable/disable network tracking |
| `NetworkMonitor.PollingIntervalMs` | `5000` | TCP table poll rate |
| `NetworkMonitor.PrivateSubnets` | `10., 172.16., 192.168., 127.` | Internal subnets to filter |
| **Correlation** | | |
| `Correlation.Enabled` | `true` | Enable/disable alert rules |
| `Correlation.LargeTransferThresholdBytes` | `26214400` (25 MB) | Single-connection alert threshold |
| `Correlation.ContinuousTransferThresholdBytes` | `31457280` (30 MB) | Window-based alert threshold |
| `Correlation.ContinuousTransferWindowMinutes` | `10` | Sliding window for continuous detection |
| `Correlation.ProbableUploadThresholdBytes` | `5242880` (5 MB) | File+network correlation threshold |
| `Correlation.ProbableUploadWindowSeconds` | `15` | Time window for upload correlation |
| **Security** | | |
| `Security.EncryptLocalQueue` | `true` | AES-256-GCM encryption for queue files |
| `Security.TamperDetection` | `true` | Verify queue file integrity |
| `Security.LocalQueuePath` | `C:\ProgramData\LogSystem\queue` | Queue file storage |
| `Security.LogRetentionDays` | `90` | How long to keep logs |

### Dashboard Configuration (`src/LogSystem.Dashboard/appsettings.json`)

| Setting | Description |
|---|---|
| `Firebase.ProjectId` | Firebase project ID (e.g., `logs-6d4bb`) |
| `Firebase.CredentialPath` | Path to service account JSON (e.g., `firebase-service-accoun.json`) |
| `Dashboard.ApiKey` | Must match the Agent's `ApiKey` |

---

## Deployment — Two-Computer Setup

### Prerequisites (Both Machines)

- Windows 10/11 or Windows Server 2019+
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) installed
- Both machines on the **same network** (same subnet)

### Admin Machine (192.168.0.169) — Run the Dashboard

```powershell
# Step 1: Open terminal, navigate to project
cd C:\Users\strka\Documents\Loging_track

# Step 2: Build the solution
dotnet build LogSystem.sln

# Step 3: Run the Dashboard
cd src\LogSystem.Dashboard
dotnet run --launch-profile http
```

Dashboard will start on `http://0.0.0.0:5176` — accessible from any machine on the network.

Open in browser: `http://192.168.0.169:5176`

> **Firewall**: If the tracked machine can't connect, allow port 5176:
> ```powershell
> netsh advfirewall firewall add rule name="LogSystem Dashboard" dir=in action=allow protocol=TCP localport=5176
> ```

### Tracked Machine (192.168.0.213) — Run the Agent

1. **Copy the entire project folder** to the tracked machine (USB drive, network share, or git clone)

2. **Verify** `src\LogSystem.Agent\appsettings.json` has:
   ```json
   "ApiEndpoint": "http://192.168.0.169:5176",
   "ApiKey": "bLgyHLk0FVzyvIwH/WW50qP7O0onuA7MYxVsCTbEHcQ="
   ```

3. **Create required directories**:
   ```powershell
   New-Item -ItemType Directory -Path "C:\ProgramData\LogSystem\queue" -Force
   New-Item -ItemType Directory -Path "C:\ProgramData\LogSystem\logs" -Force
   ```

4. **Run the Agent**:
   ```powershell
   cd C:\path\to\project\src\LogSystem.Agent
   dotnet run
   ```

5. The agent will start monitoring and uploading events. Within 60 seconds, the device will appear in the admin dashboard.

### Install as Windows Service (Production)

```powershell
# Run as Administrator on the tracked machine
.\scripts\Install-Agent.ps1 -ApiEndpoint "http://192.168.0.169:5176" -ApiKey "bLgyHLk0FVzyvIwH/WW50qP7O0onuA7MYxVsCTbEHcQ="
```

To remove:
```powershell
.\scripts\Uninstall-Agent.ps1
```

---

## Security

| Layer | Implementation |
|---|---|
| **Transport** | HTTP with API key authentication (upgrade to HTTPS with TLS 1.2+ for production) |
| **Authentication** | Shared API key in `X-Api-Key` header on every request |
| **Local encryption** | AES-256-GCM for queue files on disk |
| **Key derivation** | PBKDF2 with 100,000 iterations (SHA-256) |
| **Tamper detection** | GCM authentication tag verifies queue file integrity |
| **Agent identity** | `X-Device-Id` header + auto-generated DeviceId |

---

## Troubleshooting

| Problem | Cause | Fix |
|---|---|---|
| Agent can't connect to Dashboard | Firewall blocking port 5176 | Run `netsh advfirewall firewall add rule name="Dashboard" dir=in action=allow protocol=TCP localport=5176` on admin machine |
| Dashboard shows no devices | Agent hasn't uploaded yet | Wait 60 seconds for first upload cycle, check agent console for errors |
| Agent shows "Upload failed" | API key mismatch | Ensure `ApiKey` in agent's `appsettings.json` matches Dashboard's `Dashboard:ApiKey` |
| Dashboard shows "Firestore quota exceeded" | Google free-tier limit (50K reads/day) | Non-fatal — InMemoryStore handles all reads/writes locally. Firestore is backup only |
| Port 5176 already in use | Previous Dashboard process still running | Run: `Get-NetTCPConnection -LocalPort 5176 \| ForEach-Object { Stop-Process -Id $_.OwningProcess -Force }` |
| Agent shows "Connection refused" | Dashboard not running or wrong IP | Verify Dashboard is listening: `curl http://192.168.0.169:5176/api/dashboard/devices` |

---

## Legal Requirements

Before deploying to any endpoint:

1. Written monitoring policy approved by management
2. Employee acknowledgment form signed
3. Deploy only on company-owned devices
4. Define log retention period (default: 90 days)
5. Restrict dashboard access to authorized administrators

---

## License

Internal use only. Not for distribution.