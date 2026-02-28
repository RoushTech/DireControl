# SPEC.md — DireWatch (working title)

## Overview
A web-based APRS station monitoring and messaging interface that connects to a local
Direwolf software TNC. Operators can monitor nearby stations on a live map, track
movement, decode telemetry and weather data, exchange APRS messages, replay historical
sessions, and receive proximity and watch-list alerts — all from a browser.

## Target User
Amateur radio operators running Direwolf on a local machine who want a modern,
browser-based UI instead of (or alongside) traditional APRS clients like Xastir
or APRS.fi.

---

## Tech Stack
- **Frontend:** Vue 3 (Composition API), Vite
- **Backend:** C# / ASP.NET Core (minimal API or controller-based)
- **Database:** SQLite via Entity Framework Core
- **TNC Connection:** Direwolf via KISS over TCP (default port 8001) or AGW
- **Realtime:** SignalR for pushing updates from backend to frontend
- **Maps:** Leaflet.js (supports swappable tile providers)

---

## Core Entities

### Station
Represents a unique APRS callsign (with SSID).

| Field | Type | Notes |
|---|---|---|
| Callsign | string | Primary key, e.g. W1AW-9 |
| LastSeen | datetime | UTC |
| LastLat | float? | Last known latitude |
| LastLon | float? | Last known longitude |
| LastHeading | int? | Degrees |
| LastSpeed | float? | knots |
| LastAltitude | float? | feet or meters |
| Symbol | string | APRS symbol table + code |
| Status | string | Last status/comment text |
| IsWeatherStation | bool | |
| StationType | enum | Fixed, Mobile, Weather, Digipeater, IGate, Unknown |
| FirstSeen | datetime | UTC |
| QrzLookupData | JSON? | Cached QRZ/HamDB result |
| IsOnWatchList | bool | |
| GridSquare | string? | Maidenhead grid square derived from position |

### Packet
Every raw packet received, linked to a Station.

| Field | Type | Notes |
|---|---|---|
| Id | int | PK |
| StationCallsign | string | FK → Station |
| ReceivedAt | datetime | UTC |
| RawPacket | string | Full raw APRS string |
| ParsedType | enum | Position, Message, Weather, Telemetry, Object, Item, Status, Unknown |
| Latitude | float? | |
| Longitude | float? | |
| Path | string | Raw digipeater path string, e.g. WIDE1-1,WIDE2-1 |
| ResolvedPath | JSON | Array of callsigns with coords where known |
| HopCount | int | Number of digipeater hops |
| Comment | string | |
| WeatherData | JSON? | Decoded weather fields if applicable |
| TelemetryData | JSON? | |
| MessageData | JSON? | Addressee, message text, message ID |
| SignalData | JSON? | Decode quality, frequency offset if available from Direwolf |
| GridSquare | string? | Maidenhead grid square derived from position |

### Message (Inbox)
APRS messages addressed to our callsign.

| Field | Type | Notes |
|---|---|---|
| Id | int | PK |
| FromCallsign | string | |
| ToCallsign | string | |
| Body | string | |
| MessageId | string | APRS message number |
| ReceivedAt | datetime | UTC |
| IsRead | bool | |
| AckSent | bool | Whether we've sent an ACK |
| ReplySent | bool | |

### Alert
Triggered alerting events (proximity, watch list, geofence, etc.).

| Field | Type | Notes |
|---|---|---|
| Id | int | PK |
| AlertType | enum | Proximity, WatchList, Geofence, NewMessage |
| Callsign | string | Station that triggered it |
| TriggeredAt | datetime | UTC |
| Detail | JSON | Alert-specific payload |
| IsAcknowledged | bool | |

### Geofence
Defined geographic areas used for alerting.

| Field | Type | Notes |
|---|---|---|
| Id | int | PK |
| Name | string | |
| CenterLat | float | |
| CenterLon | float | |
| RadiusMeters | float | |
| IsActive | bool | |
| AlertOnEnter | bool | |
| AlertOnExit | bool | |

### StationStatistic
Aggregated stats per station, updated periodically.

| Field | Type | Notes |
|---|---|---|
| Callsign | string | FK → Station |
| PacketsToday | int | |
| AveragePacketsPerHour | float | |
| LongestGapMinutes | int | |
| LastComputedAt | datetime | |

---

## Features

### 1. Live Map
- Leaflet.js-based map with swappable tile providers selectable from a dropdown:
  - OpenStreetMap
  - OpenTopoMap
  - Esri Satellite
  - Carto Dark / Light
  - Configurable: allow user to add a custom XYZ tile URL
- Each visible station rendered as a map marker using the correct APRS icon
- Clicking a station marker opens a popup/sidebar with full station detail
- Stations fade or are removed from map after configurable inactivity (default: 2 hours)
- **Range rings** — configurable concentric distance rings drawn around our station
  (e.g. 5mi, 10mi, 25mi) toggled on/off from map controls
- **Heatmap mode** — a toggleable density heatmap layer showing the geographic
  distribution of all received packet positions, useful for visualising receive coverage
- **Coverage map** — highlight all unique Maidenhead grid squares from which packets
  have been received, giving a clear picture of effective range
- **Dark mode** — full dark theme for the entire UI, especially useful for nighttime
  operating; persisted to user preferences
- **Multi-window support** — the map view and beacon stream can each be popped out
  into separate browser windows for multi-monitor setups

### 2. Station Icons
- Full APRS symbol set rendered from the standard APRS symbol bitmap sheets or an
  SVG equivalent set, bundled locally so the UI works without internet
- Support both the primary symbol table (`/`) and alternate/overlay table (`\`)
- Overlays (letter or number characters on icons) rendered correctly
- Station type differentiated visually (mobile icons animate or show direction arrow
  based on heading)

### 3. Packet Path Visualisation
- When a station is selected, draw the digipeater path as a polyline on the map:
  - From the originating station → each digipeater node (where we have coords) → our station
- Each hop rendered as a distinct line segment, colour-coded by hop number
- Tooltip on each segment showing the digipeater callsign and hop number
- **Heard-by breakdown** — panel showing which digipeaters forwarded each packet,
  how many hops it took, and a hop count distribution chart across all packets
  from that station

### 4. Movement Tracks
- For mobile stations that have sent multiple position packets with differing coordinates:
  - Draw a track line showing their path over time
  - Track colour fades from light (old) to bold (recent) to indicate direction of travel
  - Track history configurable (last N points or last X hours)
  - Toggle track visibility per station or globally
- Track points are clickable to view the underlying packet detail at that position

### 5. Estimated Position
- For mobile stations with a known last heading and speed:
  - Project a "ghost" position marker forward from last known position based on elapsed
    time since last beacon
  - Displayed as a faded/dashed version of the station's icon
  - A circle of uncertainty grows with time since last beacon to indicate confidence
  - Clearly labelled "Estimated — Xm ago"
  - Hidden once the station is considered stale

### 6. Weather Stations
- Stations flagged as weather stations get a dedicated weather panel
- Decoded fields displayed cleanly: temperature, humidity, wind speed/direction,
  barometric pressure, rainfall (1h, 24h, since midnight)
- Wind direction shown as a compass rose or directional arrow
- Mini sparkline graphs for temperature and wind speed over time, drawn from packet history
- Weather stations filterable and visually highlighted on the map
- Weather data exportable as CSV for a selected time range

### 7. Station Expiry
- Stations not heard from in more than 2 hours (configurable per station type) are
  considered stale
- Stale stations are removed from the live map view but remain in the database
- A "show old stations" toggle re-displays stale stations on the map
- Configurable thresholds per station type (e.g. keep weather stations visible longer
  than mobiles)

### 8. Beacon Stream
- A live scrolling feed of all incoming packets, newest at top
- Each entry shows: timestamp, callsign, packet type icon, and a human-readable summary
  (coordinates, message text, weather reading, etc.)
- Colour-coded rows by packet type (position, message, weather, telemetry, object/item)
- Filterable by callsign, packet type, or free-text search
- Clicking a row selects the station on the map and opens the packet detail view
- Pausable — freezes scroll without dropping packets in the background; indicator shows
  how many new packets have arrived while paused

### 9. Messaging — Outbound
- Compose panel to send APRS messages to any callsign via Direwolf KISS/AGW
- Message ID generated and tracked automatically
- ACK monitoring — message flagged as acknowledged when ACK packet is received
- Configurable retry logic if no ACK is received within a timeout period
- Message thread view groups sent messages and received replies by conversation

### 10. Messaging — All Messages (Promiscuous Mode)
- An "All Messages" tab shows every APRS message visible in the stream, regardless
  of addressee
- Messages to our callsign are highlighted distinctly
- Filterable by sender, addressee, or message content

### 11. Messaging — Inbox
- Messages addressed to our callsign stored permanently in Inbox
- Unread badge count on the Inbox tab
- Desktop browser notification on new message to our callsign (with browser permission)
- ACK automatically sent when a message to us is received
- Reply button pre-fills the compose panel with the correct addressee and message thread

### 12. Station Detail Panel
- Accessible by clicking a map marker or a row in the station list
- Displays:
  - Callsign, symbol, status text, first seen / last seen timestamps
  - Current coordinates with a link to open in an external map
  - Speed, heading, altitude (for mobile stations)
  - Full packet history table for that station, paginated
  - Weather panel (for weather stations)
  - Message history involving that station
  - Station statistics: packets today, average beacons/hour, longest gap between beacons
  - QRZ / HamDB lookup result (name, address, license class) — loaded on demand, cached
  - Heard-by digipeater summary

### 13. Station List / Search
- Sidebar listing all currently active stations
- Sortable by: callsign, last seen, distance from our station, packet count
- Filterable by station type (mobile, fixed, weather, digipeater, igate)
- Search by callsign prefix or free text
- Clicking a row flies the map to the station and opens the detail panel

### 14. RF & Signal Analysis
- If Direwolf exposes decode quality metrics or frequency offset data, log and display
  these per packet in the packet detail view
- Per-station signal trend graph showing decode quality over time
- Useful for antenna experimentation and understanding receive performance

### 15. Station Statistics Dashboard
- A dedicated Statistics page showing:
  - Total packets received today and this session
  - Unique stations heard today vs. this week vs. all time
  - Packets-per-hour graph for the last 24 hours
  - Busiest digipeaters heard (by forwarded packet count)
  - Busiest stations by beacon rate
  - Most recent new stations (first ever heard)
  - Grid squares heard — world map with heard squares highlighted

### 16. Alerting & Automation

#### Proximity Alerts
- Configurable rules that trigger a notification when any station (or a specific
  callsign) comes within a defined radius of our position or a saved point of interest
- Alert stored in the Alert log and surfaced as a browser notification

#### Watch List
- Mark specific callsigns as watched
- Receive an alert and visual highlight when a watched station comes online
  (first packet received after being absent for more than the expiry threshold)
- Watch list managed from the Station Detail Panel or a dedicated settings page

#### Geofence Alerts
- Define named geographic areas (circle with centre + radius) from a settings page
- Alerts triggered when a tracked mobile station enters or exits a geofence
- Useful for tracking vehicles on a known route or monitoring a specific area
- Alert log records entry/exit events with timestamps

#### Alert Log
- All triggered alerts listed in a dedicated Alert Log view
- Filterable by type and callsign
- Acknowledge/dismiss individual alerts
- Unacknowledged alerts surfaced as a badge count in the nav

### 17. Data, History & Export

#### Packet Export
- Export raw or decoded packets for a selected station and time range as CSV or JSON
- Export weather data independently as CSV
- Export the full station list as CSV

#### Replay Mode
- A timeline scrubber to replay historical packet data as if it were live
- The map and beacon stream animate through stored packets at configurable speeds
  (1x, 5x, 10x, 60x)
- Replay can be paused, rewound, and stepped forward packet by packet
- Useful for reviewing a net, an event, or a mobile station's route after the fact

#### Database Retention Policy
- Configurable auto-pruning of old packets to keep SQLite size manageable
- Default: delete packets older than 30 days, retaining the Station record and
  the most recent packet per station
- Retention threshold configurable independently for each packet type
  (e.g. keep weather data longer than position data)
- Manual "prune now" trigger available from settings with a preview of what will be removed

### 18. APRS Object & Item Support
- APRS Objects and Items (weather alerts, events, points of interest broadcast by
  other stations) decoded and rendered on the map distinctly from tracked stations
- Objects use their own icon and label
- Compose and transmit your own APRS Objects via Direwolf (name, position, symbol,
  comment, and expiry)
- Object management panel to list, edit, and delete your own transmitted objects

### 19. Callsign Lookup Integration
- Optional integration with QRZ.com or HamDB (no API key required for HamDB)
- Loaded on demand from the Station Detail Panel, not fetched automatically for
  all stations
- Result cached in the database to avoid repeated lookups
- Displays: operator name, city/state, license class, grid square on file

### 20. Keyboard Shortcuts
- `M` — open message compose panel
- `F` + callsign — follow/track a specific station (map centres on it as it moves)
- `B` — toggle beacon stream open/closed
- `R` — open replay mode
- `Esc` — close the active panel or modal
- `?` — show keyboard shortcut reference overlay
- Shortcuts documented in a help overlay and in settings

---

## Configuration (stored in appsettings or a settings UI table)

| Setting | Default | Notes |
|---|---|---|
| Our callsign + SSID | — | Required |
| Direwolf KISS TCP host | localhost | |
| Direwolf KISS TCP port | 8001 | |
| Station expiry timeout | 120 min | Per type overrides available |
| Default map provider | OpenStreetMap | |
| Track history length | 50 points | Or X hours |
| Auto-ACK messages | true | |
| Packet retention days | 30 | |
| QRZ / HamDB preference | HamDB | |
| Range ring distances | 5, 10, 25 mi | Comma-separated, toggleable |
| Browser notifications | Prompt on first alert | |
| Dark mode | System default | |
| Replay default speed | 5x | |