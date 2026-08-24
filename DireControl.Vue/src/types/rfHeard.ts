import type { StationType } from '@/types/station'

/** One radio's direct-RF reception for one local calendar day. */
export interface RfHeardDailyDto {
  /** Local calendar day, ISO-8601 date (`YYYY-MM-DD`, no time component). */
  day: string
  channelNumber: number
  radioId: string | null
  radioName: string
  uniqueDirectStations: number
  newDirectStations: number
  directPackets: number
  maxDirectDistanceKm: number | null
  medianDirectDistanceKm: number | null
  directStationsWithPosition: number
}

/** A station this radio has heard directly on RF. */
export interface RfHeardStationDto {
  callsign: string
  channelNumber: number
  radioId: string | null
  radioName: string
  firstHeardDirect: string
  lastHeardDirect: string
  directPacketCount: number
  /**
   * Distance from home to the station's last known position. This is where it is now, not
   * the farthest copy ever heard — for a mobile the two differ.
   */
  distanceKm: number | null
  /** Null when the station is no longer in the station list. */
  symbol: string | null
  stationType: StationType
  lastSeen: string | null
}

/** Headline direct-RF figures for one radio. */
export interface RfHeardSummaryDto {
  channelNumber: number
  radioId: string | null
  radioName: string
  uniqueDirectToday: number
  uniqueDirect7d: number
  uniqueDirect30d: number
  uniqueDirectAllTime: number
  bestDistanceKm: number | null
  lastHeardDirect: string | null
}

/** Whether the one-time classification sweep has finished; figures are partial until it has. */
export interface RfHeardStatusDto {
  backfillInProgress: boolean
  packetsRemaining: number
  packetsClassified: number
  archiveReady: boolean
}
