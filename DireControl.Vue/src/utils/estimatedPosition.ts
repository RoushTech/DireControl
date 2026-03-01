/**
 * Dead-reckoning utility for projecting a mobile station's estimated position
 * forward from its last known position using heading, speed, and elapsed time.
 */

const EARTH_RADIUS_M = 6371000
// Default station expiry — must match backend default (120 min)
const STATION_EXPIRY_MINUTES = 120

export interface EstimatedPosition {
  lat: number
  lon: number
  elapsedMinutes: number
  /** Uncertainty circle radius in metres — grows with elapsed time. */
  uncertaintyRadiusMeters: number
}

/**
 * Project a dead-reckoned position from the station's last known state.
 *
 * Returns null when:
 *   - lastSpeed is null/zero
 *   - lastHeading is null
 *   - elapsed time since lastSeen exceeds the station expiry threshold
 */
export function estimatePosition(
  lastLat: number,
  lastLon: number,
  lastHeading: number | null | undefined,
  lastSpeed: number | null | undefined,  // knots
  lastSeen: string,                       // ISO UTC string
): EstimatedPosition | null {
  if (lastHeading == null || lastSpeed == null || lastSpeed <= 0) return null

  const elapsedMs = Date.now() - new Date(lastSeen).getTime()
  const elapsedMinutes = elapsedMs / 60_000

  if (elapsedMinutes >= STATION_EXPIRY_MINUTES) return null

  // Convert speed from knots to m/s and compute distance travelled
  const distanceM = lastSpeed * 0.514_444 * (elapsedMs / 1000)

  // Great-circle projection (direct formula)
  const bearingRad = (lastHeading * Math.PI) / 180
  const lat1 = (lastLat * Math.PI) / 180
  const lon1 = (lastLon * Math.PI) / 180
  const angDist = distanceM / EARTH_RADIUS_M

  const lat2 = Math.asin(
    Math.sin(lat1) * Math.cos(angDist) +
    Math.cos(lat1) * Math.sin(angDist) * Math.cos(bearingRad),
  )
  const lon2 =
    lon1 +
    Math.atan2(
      Math.sin(bearingRad) * Math.sin(angDist) * Math.cos(lat1),
      Math.cos(angDist) - Math.sin(lat1) * Math.sin(lat2),
    )

  return {
    lat: (lat2 * 180) / Math.PI,
    lon: (lon2 * 180) / Math.PI,
    elapsedMinutes,
    // 100 m per minute elapsed, minimum 100 m
    uncertaintyRadiusMeters: Math.max(100, elapsedMinutes * 100),
  }
}
