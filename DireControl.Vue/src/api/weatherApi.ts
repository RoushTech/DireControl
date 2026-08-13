import http from './axios'

export interface WeatherLayerStatus {
  available: boolean
  frameCount?: number
  lastUpdated?: string
  reason?: string
}

export interface WeatherStatus {
  radar: WeatherLayerStatus
  wind: WeatherLayerStatus
  lightning: WeatherLayerStatus
  radarProvider: number
  rainViewerProKeyConfigured: boolean
}

export interface WeatherFrame {
  time: number
  path: string
}

export interface WeatherManifest {
  generated: number
  maxNativeZoom: number
  tileSize: number
  radar: {
    past: WeatherFrame[]
    nowcast: WeatherFrame[]
  }
}

export async function getWeatherManifest(): Promise<WeatherManifest> {
  const { data } = await http.get<WeatherManifest>('/api/weather/radar/manifest')
  return data
}

export async function getWeatherStatus(): Promise<WeatherStatus> {
  const { data } = await http.get<WeatherStatus>('/api/weather/status')
  return data
}

export interface LightningStrike {
  latitude: number
  longitude: number
  ageSeconds: number
}

export interface LightningStrikes {
  connected: boolean
  generatedAt: string
  strikes: LightningStrike[]
}

export async function getLightningStrikes(bounds: {
  minLat: number
  maxLat: number
  minLon: number
  maxLon: number
}): Promise<LightningStrikes> {
  const { data } = await http.get<LightningStrikes>('/api/weather/lightning/strikes', {
    params: bounds,
  })
  return data
}

export interface LightningHistoryStrike {
  latitude: number
  longitude: number
  /** Strike time as Unix epoch seconds (UTC) — same time base as radar frame times. */
  timeSeconds: number
}

export interface LightningHistory {
  generatedAt: string
  truncated: boolean
  strikes: LightningHistoryStrike[]
}

export async function getLightningHistory(params: {
  minLat: number
  maxLat: number
  minLon: number
  maxLon: number
  fromSeconds: number
  toSeconds: number
}): Promise<LightningHistory> {
  const { data } = await http.get<LightningHistory>('/api/weather/lightning/history', { params })
  return data
}
