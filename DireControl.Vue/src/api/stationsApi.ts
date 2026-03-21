import http from "./axios";
import type { StationDto, SettingsDto, CallsignLookupDto, StationStatisticDto } from "@/types/station";
import type { TrackPointDto, PacketDto, WeatherReadingDto, SignalPointDto } from "@/types/packet";

export async function getStations(includeStale = false): Promise<StationDto[]> {
  const { data } = await http.get<StationDto[]>("/api/v0/stations", {
    params: includeStale ? { includeStale: true } : undefined,
  });
  return data;
}

export async function getStation(callsign: string): Promise<StationDto> {
  const { data } = await http.get<StationDto>(
    `/api/v0/stations/${encodeURIComponent(callsign)}`,
  );
  return data;
}

export async function getStationTrack(callsign: string, durationMinutes = 60): Promise<TrackPointDto[]> {
  const { data } = await http.get<TrackPointDto[]>(
    `/api/v0/stations/${encodeURIComponent(callsign)}/track`,
    { params: { durationMinutes } },
  );
  return data;
}

export async function getStationPackets(
  callsign: string,
  page = 1,
  pageSize = 50,
): Promise<{ items: PacketDto[]; totalCount: number }> {
  const { data } = await http.get<{ items: PacketDto[]; totalCount: number }>(
    `/api/v0/stations/${encodeURIComponent(callsign)}/packets`,
    { params: { page, pageSize } },
  );
  return data;
}

export async function getRecentPackets(): Promise<PacketDto[]> {
  const { data } = await http.get<PacketDto[]>("/api/v0/packets/recent");
  return data;
}

export async function getPacketsSince(since: string, limit = 200): Promise<PacketDto[]> {
  const { data } = await http.get<PacketDto[]>("/api/v0/packets", {
    params: { since, limit },
  });
  return data;
}

export async function getPacket(id: number): Promise<PacketDto> {
  const { data } = await http.get<PacketDto>(`/api/v0/packets/${id}`);
  return data;
}

export async function getSettings(): Promise<SettingsDto> {
  const { data } = await http.get<SettingsDto>("/api/v0/settings");
  return data;
}

export async function getStationWeather(
  callsign: string,
  from?: string,
  to?: string,
): Promise<WeatherReadingDto[]> {
  const { data } = await http.get<WeatherReadingDto[]>(
    `/api/v0/stations/${encodeURIComponent(callsign)}/weather`,
    { params: from != null || to != null ? { from, to } : undefined },
  );
  return data;
}

export async function getWatchList(): Promise<StationDto[]> {
  const { data } = await http.get<StationDto[]>('/api/v0/stations/watchlist')
  return data
}

export async function toggleWatch(callsign: string): Promise<void> {
  await http.put(`/api/v0/stations/${encodeURIComponent(callsign)}/watch`)
}

export async function lookupCallsign(callsign: string): Promise<CallsignLookupDto | null> {
  try {
    const { data } = await http.get<CallsignLookupDto>(
      `/api/v0/stations/${encodeURIComponent(callsign)}/lookup`,
    )
    return data
  } catch {
    return null
  }
}

export async function getStationStats(callsign: string): Promise<StationStatisticDto> {
  const { data } = await http.get<StationStatisticDto>(
    `/api/v0/stations/${encodeURIComponent(callsign)}/stats`,
  )
  return data
}

export async function getStationSignal(callsign: string): Promise<SignalPointDto[]> {
  const { data } = await http.get<SignalPointDto[]>(
    `/api/v0/stations/${encodeURIComponent(callsign)}/signal`,
  )
  return data
}

export async function updateOutboundPath(outboundPath: string): Promise<void> {
  await http.put('/api/v0/settings/outbound-path', { outboundPath })
}

export async function updateAprsIsSettings(payload: {
  aprsIsEnabled: boolean
  aprsIsHost: string
  aprsIsPort: number
  aprsIsPasscodeOverride: number | null
  aprsIsFilter: string
  deduplicationWindowSeconds: number
}): Promise<void> {
  await http.put('/api/v0/settings/aprs-is', payload)
}

export const RadarProvider = {
  IemNexrad: 0,
  RainViewer: 1,
  RainViewerPro: 2,
} as const
export type RadarProvider = typeof RadarProvider[keyof typeof RadarProvider]

export async function updateWeatherApiKeys(
  openWeatherMapApiKey: string | null,
  tomorrowIoApiKey: string | null,
  radarProvider: RadarProvider,
  rainViewerProApiKey: string | null,
): Promise<void> {
  await http.put('/api/v0/settings/weather-keys', { openWeatherMapApiKey, tomorrowIoApiKey, radarProvider, rainViewerProApiKey })
}
