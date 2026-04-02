# DireControl

A real-time APRS (Automatic Packet Reporting System) monitoring and control application that interfaces with [Direwolf](https://github.com/wb2osz/direwolf) via KISS TCP. Aggregates packets from local RF and APRS-IS, provides interactive mapping, messaging, alerting, and beacon management for ham radio operators.

## Features

- **Real-time packet capture** — Connects to Direwolf via KISS TCP, parses AX.25 frames, and classifies stations by type (Mobile, Fixed, Weather, Digipeater, IGate)
- **APRS-IS integration** — Dual-source packet aggregation from RF and internet with configurable filters and deduplication
- **Interactive map** — Leaflet-based map with station plotting, APRS symbols, track history, range rings, heatmap overlay, and Maidenhead grid coverage analysis
- **Messaging** — Send and receive APRS messages with automatic retry and acknowledgment tracking
- **Beacon management** — Multi-radio beacon configuration with digipeater confirmation tracking and heard/not-heard status
- **Alerting** — Watch list alerts, proximity rules, and geofences with real-time notifications
- **Weather overlays** — NEXRAD radar (IEM), RainViewer Pro, lightning (Tomorrow.io), and wind tiles (OpenWeatherMap)
- **Callsign lookup** — HamDB and QRZ integration with result caching
- **Statistics** — Coverage grid analysis, digipeater statistics, packet rates, and per-station metrics
- **Real-time updates** — SignalR pushes all events (packets, messages, alerts, beacon confirmations) to the browser instantly

## Tech Stack

| Layer    | Technology                              |
|----------|-----------------------------------------|
| Backend  | ASP.NET Core (.NET 10), Entity Framework Core, SQLite |
| Frontend | Vue 3, Vuetify 4, Vite, TypeScript, Pinia |
| Mapping  | Leaflet                                 |
| Realtime | SignalR                                 |
| APRS     | AprsSharp (KISS TNC + APRS-IS + parser) |

## Docker

The easiest way to run DireControl is with the published Docker image:

```bash
docker run -d \
  --name direcontrol \
  -p 5010:5010 \
  -v direcontrol-data:/app/data \
  roushtech/direcontrol:stable
```

The `stable` tag always points to the latest release. Pinned version tags (e.g. `0.2.0.173`) are also available — see [Docker Hub](https://hub.docker.com/r/roushtech/direcontrol/tags) for all tags.

## Building from Source

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Node.js](https://nodejs.org/) 20.19+ or 22.12+
- [Direwolf](https://github.com/wb2osz/direwolf) running with KISS TCP enabled

### Getting Started

### Backend

```bash
dotnet run --project DireControl.Api
# Listening on http://localhost:5010
```

### Frontend

```bash
cd DireControl.Vue
npm install
npm run dev
```

The Vite dev server proxies API and SignalR requests to the backend automatically.

### Configuration

Copy and edit the local settings override (git-ignored):

```bash
cp DireControl.Api/appsettings.json DireControl.Api/appsettings.local.json
```

Key settings:

| Setting | Description |
|---------|-------------|
| `ConnectionStrings:Default` | SQLite database path |
| `Direwolf:Host` / `Port` | Direwolf KISS TCP connection |
| `QRZ:Username` / `Password` | QRZ callsign lookup credentials (optional) |
| `DireControl:OurCallsign` | Your callsign with SSID |
| `DireControl:StationExpiryTimeoutMinutes` | How long before inactive stations expire |

Weather API keys and APRS-IS settings are configured through the in-app Settings page.

## Running Tests

```bash
dotnet test DireControl.Tests/DireControl.Tests.csproj
```

## License

[MIT](LICENSE) — Copyright (c) 2026 William Roush / RoushTech
