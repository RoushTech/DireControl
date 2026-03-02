export enum PacketType {
  Position = 0,
  Message = 1,
  Weather = 2,
  Telemetry = 3,
  Object = 4,
  Item = 5,
  Status = 6,
  Unknown = 7,
  Unparseable = 8,
}

export const PACKET_TYPE_LABELS: Record<PacketType, string> = {
  [PacketType.Position]: 'Position',
  [PacketType.Message]: 'Message',
  [PacketType.Weather]: 'Weather',
  [PacketType.Telemetry]: 'Telemetry',
  [PacketType.Object]: 'Object',
  [PacketType.Item]: 'Item',
  [PacketType.Status]: 'Status',
  [PacketType.Unknown]: 'Unknown',
  [PacketType.Unparseable]: 'Unparseable',
}

export const PACKET_TYPE_COLORS: Record<PacketType, string> = {
  [PacketType.Position]: 'blue',
  [PacketType.Message]: 'green',
  [PacketType.Weather]: 'teal',
  [PacketType.Telemetry]: 'purple',
  [PacketType.Object]: 'orange',
  [PacketType.Item]: 'deep-orange',
  [PacketType.Status]: 'blue-grey',
  [PacketType.Unknown]: 'grey',
  [PacketType.Unparseable]: 'grey',
}

/** Maps the string name from PacketBroadcastDto to the enum value. */
export function parsedTypeFromString(s: string): PacketType {
  const map: Record<string, PacketType> = {
    Position: PacketType.Position,
    Message: PacketType.Message,
    Weather: PacketType.Weather,
    Telemetry: PacketType.Telemetry,
    Object: PacketType.Object,
    Item: PacketType.Item,
    Status: PacketType.Status,
    Unknown: PacketType.Unknown,
    Unparseable: PacketType.Unparseable,
  }
  return map[s] ?? PacketType.Unknown
}

export interface WeatherData {
  temperatureF: number | null
  humidityPercent: number | null
  windSpeedMph: number | null
  windDirectionDeg: number | null
  windGustMph: number | null
  pressureMbar: number | null
  rainfallLastHourIn: number | null
  rainfallLast24hIn: number | null
  rainfallSinceMidnightIn: number | null
}

export interface TelemetryData {
  sequenceNumber: string | null
  analogs: number[] | null
  digitals: boolean[] | null
  comment: string | null
}

export interface MessageData {
  addressee: string
  text: string
  messageId: string | null
}

export interface PacketBroadcastDto {
  id: number
  callsign: string
  parsedType: string
  receivedAt: string
  latitude: number | null
  longitude: number | null
  summary: string
  hopCount: number
  resolvedPath: ResolvedPathEntry[]
}

export interface TrackPointDto {
  latitude: number
  longitude: number
  receivedAt: string
  speed: number | null
  heading: number | null
}

export interface ResolvedPathEntry {
  callsign: string
  latitude: number | null
  longitude: number | null
  known: boolean
  hopIndex: number
}

export interface SignalData {
  decodeQuality: number | null
  frequencyOffsetHz: number | null
}

export interface SignalPointDto {
  receivedAt: string
  decodeQuality: number | null
  frequencyOffsetHz: number | null
}

export interface PacketDto {
  id: number
  stationCallsign: string
  receivedAt: string
  rawPacket: string
  /** Integer enum value from backend (PacketType) */
  parsedType: number
  latitude: number | null
  longitude: number | null
  path: string
  resolvedPath: ResolvedPathEntry[]
  hopCount: number
  unknownHopCount: number
  isDirectHeard: boolean
  comment: string
  gridSquare: string | null
  signalData: SignalData | null
  weatherData: WeatherData | null
  telemetryData: TelemetryData | null
  messageData: MessageData | null
}

export interface WeatherReadingDto {
  receivedAt: string
  temperature: number | null
  humidity: number | null
  windSpeed: number | null
  windDirection: number | null
  windGust: number | null
  pressure: number | null
  rainLastHour: number | null
  rainLast24h: number | null
  rainSinceMidnight: number | null
}
