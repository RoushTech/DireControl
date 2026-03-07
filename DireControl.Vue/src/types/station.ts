export enum StationType {
  Fixed = 0,
  Mobile = 1,
  Weather = 2,
  Digipeater = 3,
  IGate = 4,
  Unknown = 5,
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

export enum AprsIsConnectionState {
  Disabled = 'Disabled',
  Connecting = 'Connecting',
  Connected = 'Connected',
  AuthFailed = 'AuthFailed',
  Disconnected = 'Disconnected',
}

export interface QrzLookupData {
  name: string | null;
  city: string | null;
  state: string | null;
  licenseClass: string | null;
  gridSquare: string | null;
}

export interface CallsignLookupDto {
  name: string | null;
  city: string | null;
  state: string | null;
  licenseClass: string | null;
  gridSquare: string | null;
}

export interface StationStatisticDto {
  packetsToday: number;
  packetsAllTime: number;
  averagePacketsPerHour: number;
  longestGapMinutes: number;
  packetsPerHour: number[];
}

export interface StationDto {
  callsign: string;
  firstSeen: string;
  lastSeen: string;
  lastLat: number | null;
  lastLon: number | null;
  lastHeading: number | null;
  lastSpeed: number | null;
  lastAltitude: number | null;
  symbol: string;
  status: string;
  isWeatherStation: boolean;
  stationType: StationType;
  qrzLookupData: QrzLookupData | null;
  isOnWatchList: boolean;
  gridSquare: string | null;
  heardVia: HeardVia;
  lastHeardRf: string | null;
  lastHeardAprsIs: string | null;
}

export interface HomePositionDto {
  lat: number;
  lon: number;
}

export interface SettingsDto {
  ourCallsign: string;
  homePosition: HomePositionDto | null;
  stationExpiryTimeoutMinutes: number;
  direwolfHost: string;
  direwolfPort: number;
  direwolfReconnectDelaySeconds: number;
  maxRetryAttempts: number;
  initialRetryDelaySeconds: number;
  outboundPath: string;
  aprsIsEnabled: boolean;
  aprsIsHost: string;
  aprsIsPort: number;
  aprsIsPasscodeOverride: number | null;
  aprsIsPasscodeComputed: number;
  aprsIsFilter: string;
  deduplicationWindowSeconds: number;
  openWeatherMapApiKey: string | null;
  tomorrowIoApiKey: string | null;
}

export interface CallsignCountDto {
  callsign: string;
  count: number;
  averagePerHour: number;
}

export interface RecentlyHeardDto {
  callsign: string;
  firstSeen: string;
  stationType: StationType;
}

export interface StatisticsDto {
  packetsToday: number;
  uniqueStationsToday: number;
  uniqueStationsThisWeek: number;
  uniqueStationsAllTime: number;
  packetsPerHour: number[];
  busiestDigipeaters: CallsignCountDto[];
  busiestStations: CallsignCountDto[];
  recentlyFirstHeard: RecentlyHeardDto[];
  gridSquares: string[];
}

export interface DigipeaterAnalysisEntry {
  callsign: string;
  totalPacketsForwarded: number;
  last24h: number;
  averageHopsFromUs: number;
}

