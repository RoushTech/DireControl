import type { Ref, ShallowRef } from 'vue'
import L from 'leaflet'
import { getStationTrack } from '@/api/stationsApi'
import type { StationDto } from '@/types/station'
import type { TrackPointDto } from '@/types/packet'

// Movement tracks state.
// Each entry keeps the rendered layer group plus the raw points so the trail
// can be re-rendered (older points fading and dropping off) as time passes —
// without re-hitting the API. Points older than TRACK_WINDOW_MS age out, so a
// station that stops beaconing has its trail shrink and vanish rather than
// stay drawn forever.
const TRACK_WINDOW_MS = 60 * 60 * 1000 // 60 min — matches the track API's default window
type TrackEntry = {
  group: L.LayerGroup
  points: TrackPointDto[]
  segments: { line: L.Polyline; toReceivedAt: string }[]
  circles: { circle: L.CircleMarker; receivedAt: string }[]
}
const TRACK_FETCH_THROTTLE_MS = 10_000

export function useTracks(
  map: ShallowRef<L.Map | undefined>,
  deps: {
    showTracks: Ref<boolean>
    stationCache: Map<string, StationDto>
    isMobileStation: (station: StationDto) => boolean
    formatTime: (iso: string) => string
  },
) {
  const { showTracks, stationCache, isMobileStation, formatTime } = deps

  const trackLayers = new Map<string, TrackEntry>()
  let trackAgeInterval: ReturnType<typeof setInterval> | null = null
  // Live positions extend cached tracks locally; the API is hit only when no
  // cached track exists, at most once per station per window.
  const trackFetchLast = new Map<string, number>()

  // Movement Tracks

  async function fetchAndDrawTrack(callsign: string) {
    if (!map.value) return
    try {
      const points = await getStationTrack(callsign)
      renderTrack(callsign, points)
    } catch (err) {
      console.error(`Failed to fetch track for ${callsign}:`, err)
    }
  }

  // (Re)draw a station's track from raw points, fading and dropping points by age.
  // Each point's brightness/thickness scales with how recent it is; points older
  // than TRACK_WINDOW_MS are discarded so the trail trails off and eventually
  // disappears once the station stops transmitting. Called both on fetch and on
  // the periodic aging tick.
  function renderTrack(callsign: string, points: TrackPointDto[]) {
    if (!map.value) return

    const now = Date.now()
    const fresh = points.filter(p => now - new Date(p.receivedAt).getTime() <= TRACK_WINDOW_MS)

    // Swap out any existing layer group for this callsign.
    const previous = trackLayers.get(callsign)
    if (previous) previous.group.remove()

    if (fresh.length < 2) {
      trackLayers.delete(callsign)
      return
    }

    // Fade factor for a point: 1.0 when just received, → 0 as it nears the window edge.
    const ageFrac = (receivedAt: string) =>
      Math.max(0, Math.min(1, 1 - (now - new Date(receivedAt).getTime()) / TRACK_WINDOW_MS))

    const group = L.layerGroup()
    const segments: TrackEntry['segments'] = []
    const circles: TrackEntry['circles'] = []
    for (let i = 0; i < fresh.length - 1; i++) {
      const from = fresh[i]!
      const to = fresh[i + 1]!
      const frac = ageFrac(to.receivedAt)
      const opacity = 0.15 + (0.85 * frac)
      const weight = 2 + Math.round(2 * frac)
      const segment = L.polyline(
        [[from.latitude, from.longitude], [to.latitude, to.longitude]],
        { color: '#1976D2', weight, opacity, lineCap: 'round', lineJoin: 'round' },
      )
      segments.push({ line: segment, toReceivedAt: to.receivedAt })
      group.addLayer(segment)
    }
    for (const pt of fresh) {
      const opacity = 0.2 + (0.8 * ageFrac(pt.receivedAt))
      const circle = L.circleMarker([pt.latitude, pt.longitude], {
        radius: 4,
        color: '#1976D2',
        fillColor: '#1976D2',
        fillOpacity: opacity,
        weight: 1,
        opacity,
      })
      const speedStr = pt.speed != null ? `${pt.speed.toFixed(1)} knots` : 'N/A'
      circle.bindPopup(
        `<strong>Track Point</strong><br>Time: ${formatTime(pt.receivedAt)}<br>Speed: ${speedStr}`,
      )
      circles.push({ circle, receivedAt: pt.receivedAt })
      group.addLayer(circle)
    }

    trackLayers.set(callsign, { group, points: fresh, segments, circles })
    if (showTracks.value) {
      group.addTo(map.value)
    }
  }

  // Extends a station's cached track with a live position instead of refetching
  // the whole track per packet; falls back to a throttled fetch when no cache.
  function appendTrackPoint(callsign: string, latitude: number, longitude: number, receivedAt: string) {
    const entry = trackLayers.get(callsign)
    if (entry) {
      entry.points.push({ latitude, longitude, receivedAt, speed: null, heading: null })
      renderTrack(callsign, entry.points)
      return
    }
    const last = trackFetchLast.get(callsign) ?? 0
    if (Date.now() - last < TRACK_FETCH_THROTTLE_MS) return
    trackFetchLast.set(callsign, Date.now())
    fetchAndDrawTrack(callsign)
  }

  // Periodic pass: fade points in place; rebuild a track only when points age
  // out of the window, and drop tracks whose station is gone.
  function ageTracks() {
    const now = Date.now()
    for (const callsign of trackLayers.keys()) {
      if (!stationCache.has(callsign)) {
        removeTrack(callsign)
        continue
      }
      const entry = trackLayers.get(callsign)
      if (!entry) continue

      if (entry.points.some(p => now - new Date(p.receivedAt).getTime() > TRACK_WINDOW_MS)) {
        renderTrack(callsign, entry.points)
        continue
      }
      if (!showTracks.value) continue

      const ageFrac = (receivedAt: string) =>
        Math.max(0, Math.min(1, 1 - (now - new Date(receivedAt).getTime()) / TRACK_WINDOW_MS))
      for (const seg of entry.segments) {
        const frac = ageFrac(seg.toReceivedAt)
        seg.line.setStyle({ opacity: 0.15 + (0.85 * frac), weight: 2 + Math.round(2 * frac) })
      }
      for (const c of entry.circles) {
        const o = 0.2 + (0.8 * ageFrac(c.receivedAt))
        c.circle.setStyle({ opacity: o, fillOpacity: o })
      }
    }
  }

  function removeTrack(callsign: string) {
    const existing = trackLayers.get(callsign)
    if (existing) {
      existing.group.remove()
      trackLayers.delete(callsign)
    }
  }

  function toggleTracks() {
    showTracks.value = !showTracks.value
    if (!map.value) return
    if (showTracks.value) {
      for (const [callsign, station] of stationCache) {
        if (isMobileStation(station) && station.lastLat != null && station.lastLon != null) {
          const existing = trackLayers.get(callsign)
          if (existing) {
            existing.group.addTo(map.value)
          } else {
            fetchAndDrawTrack(callsign)
          }
        }
      }
    } else {
      for (const entry of trackLayers.values()) {
        entry.group.remove()
      }
    }
  }

  async function loadTracksForMobileStations() {
    if (!showTracks.value) return
    for (const [callsign, station] of stationCache) {
      if (isMobileStation(station) && station.lastLat != null && station.lastLon != null) {
        if (!trackLayers.has(callsign)) {
          await fetchAndDrawTrack(callsign)
        }
      }
    }
  }

  // Age movement tracks every 30 s so old points fade out and drop off
  function startTrackAging() {
    trackAgeInterval = setInterval(ageTracks, 30_000)
  }

  function cleanup() {
    if (trackAgeInterval) {
      clearInterval(trackAgeInterval)
      trackAgeInterval = null
    }
    for (const entry of trackLayers.values()) {
      entry.group.remove()
    }
    trackLayers.clear()
  }

  return {
    appendTrackPoint,
    removeTrack,
    toggleTracks,
    loadTracksForMobileStations,
    startTrackAging,
    cleanup,
  }
}
