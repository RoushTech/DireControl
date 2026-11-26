export enum StationType {
  Unknown = 0,
  Fixed = 1,
  Mobile = 2,
  Weather = 3,
  Digipeater = 4,
  IGate = 5,
  Gateway = 6,
}

export enum HeardVia {
  Unknown = 0,
  Direct = 1,
  Digi = 2,
  DirectAndDigi = 3,
  Internet = 4,
  IgateRf = 5,
  IgateRfDigi = 6,
}

export interface QrzLookupData {
  name: string | null
  city: string | null
  state: string | null
  licenseClass: string | null
  gridSquare: string | null
}

export interface CallsignLookupDto {
  name: string | null
  city: string | null
  state: string | null
  licenseClass: string | null
  gridSquare: string | null
}

export interface StationStatisticDto {
  packetsToday: number
  packetsAllTime: number
  averagePacketsPerHour: number
  longestGapMinutes: number
  packetsPerHour: number[]
}

export interface StationDto {
  callsign: string
  firstSeen: string
  lastSeen: string
  lastLat: number | null
  lastLon: number | null
  lastHeading: number | null
  lastSpeed: number | null
  lastAltitude: number | null
  symbol: string
  status: string
  isWeatherStation: boolean
  stationType: StationType
  qrzLookupData: QrzLookupData | null
  isOnWatchList: boolean
  gridSquare: string | null
  heardVia: HeardVia
  lastHeardRf: string | null
  lastHeardAprsIs: string | null
  lastMode: string | null
  lastFrequencyMhz: string | null
}

export interface HomePositionDto {
  lat: number
  lon: number
}

export interface SettingsDto {
  ourCallsign: string
  homePosition: HomePositionDto | null
  stationExpiryTimeoutMinutes: number
  direwolfHost: string
  direwolfPort: number
  direwolfReconnectDelaySeconds: number
  maxRetryAttempts: number
  initialRetryDelaySeconds: number
  outboundPath: string
  aprsIsEnabled: boolean
  aprsIsHost: string
  aprsIsPort: number
  aprsIsPasscodeOverride: number | null
  aprsIsPasscodeComputed: number
  aprsIsFilter: string
  deduplicationWindowSeconds: number
  direwolfEnabled: boolean
  digipeaterEnabled: boolean
  digipeaterMaxWideN: number
  digipeaterFillInOnly: boolean
  kissServerEnabled: boolean
  kissServerPort: number
  kissServerBindAddress: string
  rfToIsGatingEnabled: boolean
  isToRfGatingEnabled: boolean
  isToRfPath: string
  isToRfRecentHeardMinutes: number
  connectedModeInboundEnabled: boolean
  connectedModeMaxSessions: number
  connectedModeDefaultPaclen: number
  connectedModeWindowSize: number
  connectedModeT1Seconds: number
  connectedModeRetries: number
  connectedModePreferMod128: boolean
  pmsEnabled: boolean
  pmsSsid: number
  pmsBannerText: string
  pmsRetentionDays: number
  agwpeServerEnabled: boolean
  agwpeServerPort: number
  agwpeServerBindAddress: string
  terminalTranscriptRetentionDays: number
  lightningAlertEnabled: boolean
  lightningAlertRadiusKm: number
  lightningAlertCooldownMinutes: number
  lightningAlertAutoPan: boolean
}

export interface CallsignCountDto {
  callsign: string
  count: number
  averagePerHour: number
}

export interface RecentlyHeardDto {
  callsign: string
  firstSeen: string
  stationType: StationType
}

export interface StatisticsDto {
  packetsToday: number
  uniqueStationsToday: number
  uniqueStationsThisWeek: number
  uniqueStationsAllTime: number
  packetsPerHour: number[]
  busiestStations: CallsignCountDto[]
  recentlyFirstHeard: RecentlyHeardDto[]
  gridSquares: string[]
}

export interface StationFrequencyDto {
  callsign: string
  frequencyMhz: string
  mode: string | null
  stationType: StationType
  lastSeen: string
}

export interface DigipeaterAnalysisEntry {
  callsign: string
  totalPacketsForwarded: number
  last24h: number
  averageHopsFromUs: number
}
