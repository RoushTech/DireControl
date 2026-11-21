import type { StationDto } from '@/types/station'

/** Which stations the map shows, by how they reached us. */
export type StationSourceFilter = 'all' | 'rf' | 'is'

type HeardTimes = Pick<StationDto, 'lastHeardRf' | 'lastSeen'>

/**
 * True when the radio heard the station within `windowMs` of the reference time.
 *
 * The reference is "now" for an active station, so RF means "would still be on the
 * map with APRS-IS switched off". A stale station is measured against its own last
 * packet instead, otherwise every stale station would read as internet-only.
 */
export function heardOnRf(
  station: HeardTimes,
  windowMs: number,
  nowMs: number,
  stale = false,
): boolean {
  if (!station.lastHeardRf) return false
  const referenceMs = stale ? new Date(station.lastSeen).getTime() : nowMs
  return new Date(station.lastHeardRf).getTime() >= referenceMs - windowMs
}

/** RF and APRS-IS partition the stations: every station matches exactly one of them. */
export function matchesSourceFilter(
  filter: StationSourceFilter,
  station: HeardTimes,
  windowMs: number,
  nowMs: number,
  stale = false,
): boolean {
  if (filter === 'all') return true
  return heardOnRf(station, windowMs, nowMs, stale) === (filter === 'rf')
}
