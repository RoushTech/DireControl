/** One digipeater that confirmed (repeated) our beacons. */
export interface RepeaterRow {
  callsign: string
  count: number
  medianSeconds: number
  /** Last known position of the digipeater from its confirmations, if any. */
  lat: number | null
  lon: number | null
}

/** Great-circle distance in km. */
export function distanceKm(lat1: number, lon1: number, lat2: number, lon2: number): number {
  const rad = Math.PI / 180
  const dLat = (lat2 - lat1) * rad
  const dLon = (lon2 - lon1) * rad
  const a =
    Math.sin(dLat / 2) ** 2 + Math.cos(lat1 * rad) * Math.cos(lat2 * rad) * Math.sin(dLon / 2) ** 2
  return 6371 * 2 * Math.atan2(Math.sqrt(a), Math.sqrt(1 - a))
}
