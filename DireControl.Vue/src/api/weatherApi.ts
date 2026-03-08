import http from "./axios";

export interface WeatherLayerStatus {
  available: boolean;
  frameCount?: number;
  lastUpdated?: string;
  reason?: string;
}

export interface WeatherStatus {
  radar: WeatherLayerStatus;
  wind: WeatherLayerStatus;
  lightning: WeatherLayerStatus;
  radarProvider: number;
  rainViewerProKeyConfigured: boolean;
}

export interface WeatherFrame {
  time: number;
  path: string;
}

export interface WeatherManifest {
  generated: number;
  maxNativeZoom: number;
  tileSize: number;
  radar: {
    past: WeatherFrame[];
    nowcast: WeatherFrame[];
  };
}

export async function getWeatherManifest(): Promise<WeatherManifest> {
  const { data } = await http.get<WeatherManifest>("/api/weather/radar/manifest");
  return data;
}

export async function getWeatherStatus(): Promise<WeatherStatus> {
  const { data } = await http.get<WeatherStatus>("/api/weather/status");
  return data;
}
