import http from "./axios";
import type { StatisticsDto, DigipeaterAnalysisEntry } from "@/types/station";

export async function getStatistics(): Promise<StatisticsDto> {
  const { data } = await http.get<StatisticsDto>("/api/statistics");
  return data;
}

export async function getDigipeaterAnalysis(): Promise<DigipeaterAnalysisEntry[]> {
  const { data } = await http.get<DigipeaterAnalysisEntry[]>("/api/analysis/digipeaters");
  return data;
}
