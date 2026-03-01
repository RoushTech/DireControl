import http from "./axios";
import type { StationDto, SettingsDto, CallsignLookupDto, StationStatisticDto } from "@/types/station";
import type { TrackPointDto, PacketDto, WeatherReadingDto, SignalPointDto } from "@/types/packet";

export async function getStations(includeStale = false): Promise<StationDto[]> {
  const { data } = await http.get<StationDto[]>("/api/stations", {
    params: includeStale ? { includeStale: true } : undefined,
  });
  return data;
}

export async function getStation(callsign: string): Promise<StationDto> {
  const { data } = await http.get<StationDto>(
    `/api/stations/${encodeURIComponent(callsign)}`,
  );
  return data;
}

export async function getStationTrack(callsign: string): Promise<TrackPointDto[]> {
  const { data } = await http.get<TrackPointDto[]>(
    `/api/stations/${encodeURIComponent(callsign)}/track`,
  );
  return data;
}

export async function getStationPackets(
  callsign: string,
  page = 1,
  pageSize = 50,
): Promise<{ items: PacketDto[]; totalCount: number }> {
  const { data } = await http.get<{ items: PacketDto[]; totalCount: number }>(
    `/api/stations/${encodeURIComponent(callsign)}/packets`,
    { params: { page, pageSize } },
  );
  return data;
}

export async function getRecentPackets(): Promise<PacketDto[]> {
  const { data } = await http.get<PacketDto[]>("/api/packets/recent");
  return data;
}

export async function getSettings(): Promise<SettingsDto> {
  const { data } = await http.get<SettingsDto>("/api/settings");
  return data;
}

export async function getStationWeather(callsign: string): Promise<WeatherReadingDto[]> {
  const { data } = await http.get<WeatherReadingDto[]>(
    `/api/stations/${encodeURIComponent(callsign)}/weather`,
  );
  return data;
}

export async function getWatchList(): Promise<StationDto[]> {
  const { data } = await http.get<StationDto[]>('/api/stations/watchlist')
  return data
}

export async function toggleWatch(callsign: string): Promise<void> {
  await http.put(`/api/stations/${encodeURIComponent(callsign)}/watch`)
}

export async function lookupCallsign(callsign: string): Promise<CallsignLookupDto | null> {
  try {
    const { data } = await http.get<CallsignLookupDto>(
      `/api/stations/${encodeURIComponent(callsign)}/lookup`,
    )
    return data
  } catch {
    return null
  }
}

export async function getStationStats(callsign: string): Promise<StationStatisticDto> {
  const { data } = await http.get<StationStatisticDto>(
    `/api/stations/${encodeURIComponent(callsign)}/stats`,
  )
  return data
}

export async function getStationSignal(callsign: string): Promise<SignalPointDto[]> {
  const { data } = await http.get<SignalPointDto[]>(
    `/api/stations/${encodeURIComponent(callsign)}/signal`,
  )
  return data
}
