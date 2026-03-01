export interface AlertDto {
  id: number
  alertType: number
  alertTypeName: string
  callsign: string
  triggeredAt: string
  isAcknowledged: boolean
  distanceMeters: number | null
  geofenceName: string | null
  direction: string | null
  ruleName: string | null
  messageText: string | null
}

export interface AlertBroadcastDto {
  id: number
  alertTypeName: string
  callsign: string
  triggeredAt: string
  geofenceName: string | null
  direction: string | null
  ruleName: string | null
  distanceMeters: number | null
}

export interface GeofenceDto {
  id: number
  name: string
  centerLat: number
  centerLon: number
  radiusMeters: number
  isActive: boolean
  alertOnEnter: boolean
  alertOnExit: boolean
}

export interface CreateGeofenceRequest {
  name: string
  centerLat: number
  centerLon: number
  radiusMeters: number
  alertOnEnter: boolean
  alertOnExit: boolean
}

export interface ProximityRuleDto {
  id: number
  name: string
  targetCallsign: string | null
  centerLat: number
  centerLon: number
  radiusMetres: number
  isActive: boolean
}

export interface CreateProximityRuleRequest {
  name: string
  targetCallsign: string | null
  centerLat: number
  centerLon: number
  radiusMetres: number
}

export const ALERT_TYPE_COLORS: Record<string, string> = {
  WatchList: 'amber',
  Proximity: 'blue',
  Geofence: 'green',
  NewMessage: 'purple',
}
