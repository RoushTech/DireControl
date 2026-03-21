import http from "./axios";
import type { StatisticsDto, DigipeaterAnalysisEntry, StationFrequencyDto } from "@/types/station";

export async function getStatistics(): Promise<StatisticsDto> {
  const { data } = await http.get<StatisticsDto>("/api/v0/statistics");
  return data;
}

export async function getDigipeaterAnalysis(): Promise<DigipeaterAnalysisEntry[]> {
  const { data } = await http.get<DigipeaterAnalysisEntry[]>("/api/v0/analysis/digipeaters");
  return data;
}

export async function getStationFrequencies(): Promise<StationFrequencyDto[]> {
  const { data } = await http.get<StationFrequencyDto[]>("/api/v0/stations/frequencies");
  return data;
}
