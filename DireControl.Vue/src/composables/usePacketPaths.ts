import type { ShallowRef } from 'vue'
import L from 'leaflet'
import { getStationPackets } from '@/api/stationsApi'
import type { StationDto } from '@/types/station'
import type { ResolvedPathEntry } from '@/types/packet'
import { useUnits } from '@/composables/useUnits'
import { useStationSelectionStore } from '@/stores/stationSelection'
import { escapeHtml } from '@/utils/escapeHtml'

const HOP_SEGMENT_COLORS = ['#4A90D9', '#7B68EE', '#DA70D6'] // blue, purple, orchid
const HOP_COLOR_FALLBACK = '#FF8C00'  // dark orange for hop 3+
const UNKNOWN_SEGMENT_COLOR = '#999999'  // grey for dashed unknown segments
const FINAL_HOP_COLOR = '#2ECC71'       // green for the last hop to our station

// Packet path visualisation state
// Each entry holds the map layer group, an optional fade timer, and whether
// the path is "persistent" (user-selected) or "auto" (fades after 8 s).
type PathEntry = { group: L.LayerGroup; fadeTimer: ReturnType<typeof setTimeout> | null; persistent: boolean; resolvedPath: ResolvedPathEntry[] }

// Identifies generic APRS path aliases that do not represent a specific station.
const GENERIC_ALIAS_RE = /^(WIDE|RELAY|TRACE|NCA|GATE|ECHO|IGATE)(\d(-\d)?)?$/i
function isGenericAlias(callsign: string): boolean {
  return GENERIC_ALIAS_RE.test(callsign)
}

function haversineKm(lat1: number, lon1: number, lat2: number, lon2: number): number {
  const R = 6371
  const dLat = (lat2 - lat1) * Math.PI / 180
  const dLon = (lon2 - lon1) * Math.PI / 180
  const a =
    Math.sin(dLat / 2) ** 2 +
    Math.cos(lat1 * Math.PI / 180) * Math.cos(lat2 * Math.PI / 180) * Math.sin(dLon / 2) ** 2
  return R * 2 * Math.atan2(Math.sqrt(a), Math.sqrt(1 - a))
}

function bearingDeg(lat1: number, lon1: number, lat2: number, lon2: number): number {
  const dLon = (lon2 - lon1) * Math.PI / 180
  const la1 = lat1 * Math.PI / 180
  const la2 = lat2 * Math.PI / 180
  const y = Math.sin(dLon) * Math.cos(la2)
  const x = Math.cos(la1) * Math.sin(la2) - Math.sin(la1) * Math.cos(la2) * Math.cos(dLon)
  return (Math.atan2(y, x) * 180 / Math.PI + 360) % 360
}

function addPathArrow(
  group: L.LayerGroup,
  fromLat: number, fromLon: number,
  toLat: number, toLon: number,
  color: string,
) {
  const midLat = (fromLat + toLat) / 2
  const midLon = (fromLon + toLon) / 2
  const bearing = bearingDeg(fromLat, fromLon, toLat, toLon)
  const arrowIcon = L.divIcon({
    html: `<div style="width:0;height:0;border-left:6px solid transparent;border-right:6px solid transparent;border-bottom:12px solid ${color};transform:rotate(${bearing}deg);transform-origin:6px 6px;pointer-events:none;"></div>`,
    className: '',
    iconSize: [12, 12],
    iconAnchor: [6, 6],
  })
  group.addLayer(L.marker([midLat, midLon], { icon: arrowIcon, interactive: false, zIndexOffset: -50 }))
}

type PathSegment = {
  fromLat: number
  fromLon: number
  toLat: number
  toLon: number
  fromCallsign: string
  toCallsign: string
  hopIndexFrom: number
  isLastHop: boolean
  unknownsBetween: ResolvedPathEntry[]
}

function buildPathSegments(path: ResolvedPathEntry[]): PathSegment[] {
  const segments: PathSegment[] = []
  let lastKnownIdx = -1

  for (let i = 0; i < path.length; i++) {
    const entry = path[i]!
    if (!(entry.known ?? false) || entry.latitude == null || entry.longitude == null) continue

    if (lastKnownIdx >= 0) {
      const from = path[lastKnownIdx]!
      const unknownsBetween = path.slice(lastKnownIdx + 1, i).filter(e => !(e.known ?? false))
      segments.push({
        fromLat: from.latitude!,
        fromLon: from.longitude!,
        toLat: entry.latitude,
        toLon: entry.longitude,
        fromCallsign: from.callsign,
        toCallsign: entry.callsign,
        hopIndexFrom: from.hopIndex ?? 0,
        isLastHop: i === path.length - 1,
        unknownsBetween,
      })
    }
    lastKnownIdx = i
  }

  return segments
}

function unknownHopLabel(unknowns: ResolvedPathEntry[]): string {
  if (unknowns.length === 0) return ''
  const allGeneric = unknowns.every(e => isGenericAlias(e.callsign))
  if (allGeneric) {
    const aliases = [...new Set(unknowns.map(e => escapeHtml(e.callsign)))].join(', ')
    return `Via ${aliases} (path not traced)`
  }
  return unknowns.length === 1 ? '1 unknown hop' : `${unknowns.length} unknown hops`
}

function hopSegmentColor(hopIndexFrom: number, isLastHop: boolean, isUnknown: boolean): string {
  if (isUnknown) return UNKNOWN_SEGMENT_COLOR
  if (isLastHop) return FINAL_HOP_COLOR
  return HOP_SEGMENT_COLORS[hopIndexFrom] ?? HOP_COLOR_FALLBACK
}

export function usePacketPaths(
  map: ShallowRef<L.Map | undefined>,
  deps: {
    stationCache: Map<string, StationDto>
    staleStationCache: Map<string, StationDto>
    formatTime: (iso: string) => string
  },
) {
  const { stationCache, staleStationCache, formatTime } = deps
  const selectionStore = useStationSelectionStore()
  const { formatDistance } = useUnits()

  const activePaths = new Map<string, PathEntry>()

  // Packet Path Visualisation

  function removePath(callsign: string) {
    const entry = activePaths.get(callsign)
    if (!entry) {
      console.warn('[Path] No activePaths entry to remove for', callsign, '— map has', activePaths.size, 'entries:', [...activePaths.keys()])
      return
    }
    if (entry.fadeTimer) clearTimeout(entry.fadeTimer)
    entry.group.remove()
    activePaths.delete(callsign)
  }

  function clearAllPaths() {
    for (const [, entry] of activePaths) {
      if (entry.fadeTimer) clearTimeout(entry.fadeTimer)
      entry.group.remove()
    }
    activePaths.clear()
  }

  function redrawAllPaths() {
    if (!map.value) return
    for (const [, entry] of activePaths) {
      entry.group.clearLayers()
      drawPathLayers(entry.group, entry.resolvedPath)
    }
  }

  function schedulePathFade(callsign: string) {
    const entry = activePaths.get(callsign)
    if (!entry || entry.persistent) return

    // Begin CSS opacity fade at 5 s; fully remove layers at 8 s (3 s transition).
    entry.fadeTimer = setTimeout(() => {
      const e = activePaths.get(callsign)
      if (!e) return
      e.group.eachLayer(layer => {
        if (layer instanceof L.Polyline) {
          const el = (layer as unknown as { _path?: SVGPathElement })._path
          if (el) {
            el.style.transition = 'opacity 3s ease-out'
            el.style.opacity = '0'
          }
        } else if (layer instanceof L.Marker) {
          const el = layer.getElement()
          if (el) {
            el.style.transition = 'opacity 3s ease-out'
            el.style.opacity = '0'
          }
        }
      })
      e.fadeTimer = setTimeout(() => removePath(callsign), 3000)
    }, 5000)
  }

  /** Populate a Leaflet LayerGroup with polylines and arrowheads for a resolved path. */
  function drawPathLayers(group: L.LayerGroup, resolvedPath: ResolvedPathEntry[]) {
    const segments = buildPathSegments(resolvedPath)
    if (segments.length === 0) return false

    const totalHops = resolvedPath.length - 1  // excludes source entry (hopIndex 0)

    for (const seg of segments) {
      const isUnknown = seg.unknownsBetween.length > 0
      const color = hopSegmentColor(seg.hopIndexFrom, seg.isLastHop, isUnknown)
      const distKm = haversineKm(seg.fromLat, seg.fromLon, seg.toLat, seg.toLon)
      const distStr = formatDistance(distKm)

      const line = L.polyline(
        [[seg.fromLat, seg.fromLon], [seg.toLat, seg.toLon]],
        {
          color,
          weight: 3,
          opacity: 1,
          dashArray: isUnknown ? '8 6' : undefined,
          lineCap: 'round',
        },
      )

      if (isUnknown) {
        const label = unknownHopLabel(seg.unknownsBetween)
        line.bindTooltip(
          `<strong>${escapeHtml(seg.fromCallsign)} → ${escapeHtml(seg.toCallsign)}</strong> (${label})<br>${distStr}`,
          { sticky: true, direction: 'top' },
        )
      } else {
        const hopNum = seg.hopIndexFrom + 1
        const toStation = stationCache.get(seg.toCallsign) ?? staleStationCache.get(seg.toCallsign)
        const lastSeenStr = toStation ? ` · ${formatTime(toStation.lastSeen)}` : ''
        line.bindTooltip(
          `<strong>${escapeHtml(seg.fromCallsign)} → ${escapeHtml(seg.toCallsign)}</strong><br>Hop ${hopNum} of ${totalHops}${lastSeenStr}<br>${distStr}`,
          { sticky: true, direction: 'top' },
        )
      }

      group.addLayer(line)

      if (isUnknown) {
        const label = unknownHopLabel(seg.unknownsBetween)
        const midLat = (seg.fromLat + seg.toLat) / 2
        const midLon = (seg.fromLon + seg.toLon) / 2
        const midMarker = L.marker([midLat, midLon], {
          icon: L.divIcon({ html: '', className: '', iconSize: [0, 0], iconAnchor: [0, 0] }),
          interactive: false,
          zIndexOffset: -100,
        })
        midMarker.bindTooltip(label, { permanent: true, className: 'path-unknown-label', direction: 'top' })
        group.addLayer(midMarker)
      }

      addPathArrow(group, seg.fromLat, seg.fromLon, seg.toLat, seg.toLon, color)
    }

    return true
  }

  /**
   * Auto-draw a path from a SignalR broadcast packet.  Fades out after 8 s.
   * If the station is currently selected (persistent path), the auto-draw is
   * skipped — the persistent path already shows from the API fetch.
   */
  function drawAutoPath(callsign: string, resolvedPath: ResolvedPathEntry[]) {
    if (!map.value || resolvedPath.length < 2) return
    if (callsign === selectionStore.selectedCallsign) return

    // Clear any existing auto-path for this callsign (reset timer on re-beacon)
    removePath(callsign)

    const group = L.layerGroup()
    if (!drawPathLayers(group, resolvedPath)) return

    group.addTo(map.value)
    const entry: PathEntry = { group, fadeTimer: null, persistent: false, resolvedPath }
    activePaths.set(callsign, entry)
    schedulePathFade(callsign)
  }

  /**
   * Draw a station's path persistently from an already-known resolved path
   * (e.g. straight from a SignalR broadcast — no HTTP round trip).
   * The path stays until the station is deselected.
   */
  function drawPersistentPath(callsign: string, resolvedPath: ResolvedPathEntry[]) {
    if (!map.value) return
    // Clear any existing path (auto or persistent) for this callsign
    removePath(callsign)
    if (resolvedPath.length < 2) return

    const group = L.layerGroup()
    if (!drawPathLayers(group, resolvedPath)) return

    group.addTo(map.value)
    activePaths.set(callsign, { group, fadeTimer: null, persistent: true, resolvedPath })
  }

  /**
   * Fetch the most recent packet for a station and draw its path persistently.
   * Used when the user explicitly selects a station (click or sidebar).
   */
  async function showPacketPath(callsign: string) {
    if (!map.value) return
    try {
      const { items } = await getStationPackets(callsign, 1, 1)
      // Guard: station may have been deselected while the fetch was in flight
      if (selectionStore.selectedCallsign !== callsign) {
        return
      }
      drawPersistentPath(callsign, items[0]?.resolvedPath ?? [])
    } catch (err) {
      console.error(`Failed to show packet path for ${callsign}:`, err)
    }
  }

  return {
    removePath,
    clearAllPaths,
    redrawAllPaths,
    drawAutoPath,
    drawPersistentPath,
    showPacketPath,
  }
}
