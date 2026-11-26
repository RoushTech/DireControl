import http from './axios'
import type {
  StatisticsDto as BaseStatisticsDto,
  DigipeaterAnalysisEntry,
  StationFrequencyDto,
} from '@/types/station'

/**
 * Global statistics snapshot. The per-hour arrays are oldest first (index 23 is
 * the current hour); the RF and APRS-IS series exclude our own transmissions,
 * which only count towards the all-sources `packetsPerHour` total.
 */
export interface StatisticsDto extends BaseStatisticsDto {
  packetsPerHourRf: number[]
  packetsPerHourIs: number[]
}

export async function getStatistics(): Promise<StatisticsDto> {
  const { data } = await http.get<StatisticsDto>('/api/v0/statistics')
  return data
}

export async function getDigipeaterAnalysis(): Promise<DigipeaterAnalysisEntry[]> {
  const { data } = await http.get<DigipeaterAnalysisEntry[]>('/api/v0/analysis/digipeaters')
  return data
}

export async function getStationFrequencies(): Promise<StationFrequencyDto[]> {
  const { data } = await http.get<StationFrequencyDto[]>('/api/v0/stations/frequencies')
  return data
}
