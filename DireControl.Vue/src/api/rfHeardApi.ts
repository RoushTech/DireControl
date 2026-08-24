import http from './axios'
import type { RfHeardDailyDto, RfHeardStationDto, RfHeardSummaryDto } from '@/types/rfHeard'

/**
 * Per-radio, per-day direct reception. Days with no reception come back as explicit zero
 * rows, so the caller can plot a continuous line without filling gaps itself.
 */
export async function getRfHeardDaily(days: number, channel?: number): Promise<RfHeardDailyDto[]> {
  const { data } = await http.get<RfHeardDailyDto[]>('/api/v0/rf-heard/daily', {
    params: { days, channel },
  })
  return data
}

export async function getRfHeardStations(
  channel?: number,
  limit = 500,
): Promise<RfHeardStationDto[]> {
  const { data } = await http.get<RfHeardStationDto[]>('/api/v0/rf-heard/stations', {
    params: { channel, limit },
  })
  return data
}

export async function getRfHeardSummary(): Promise<RfHeardSummaryDto[]> {
  const { data } = await http.get<RfHeardSummaryDto[]>('/api/v0/rf-heard/summary')
  return data
}
