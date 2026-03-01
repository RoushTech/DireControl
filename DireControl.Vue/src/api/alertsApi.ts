import http from './axios'
import type {
  AlertDto,
  GeofenceDto,
  CreateGeofenceRequest,
  ProximityRuleDto,
  CreateProximityRuleRequest,
} from '@/types/alert'

export async function getAlerts(unacknowledged?: boolean): Promise<AlertDto[]> {
  const { data } = await http.get<AlertDto[]>('/api/v0/alerts', {
    params: unacknowledged ? { unacknowledged: true } : undefined,
  })
  return data
}

export async function acknowledgeAlert(id: number): Promise<void> {
  await http.put(`/api/v0/alerts/${id}/acknowledge`)
}

export async function getGeofences(): Promise<GeofenceDto[]> {
  const { data } = await http.get<GeofenceDto[]>('/api/v0/geofences')
  return data
}

export async function createGeofence(request: CreateGeofenceRequest): Promise<GeofenceDto> {
  const { data } = await http.post<GeofenceDto>('/api/v0/geofences', request)
  return data
}

export async function deleteGeofence(id: number): Promise<void> {
  await http.delete(`/api/v0/geofences/${id}`)
}

export async function getProximityRules(): Promise<ProximityRuleDto[]> {
  const { data } = await http.get<ProximityRuleDto[]>('/api/v0/proximityrules')
  return data
}

export async function createProximityRule(
  request: CreateProximityRuleRequest,
): Promise<ProximityRuleDto> {
  const { data } = await http.post<ProximityRuleDto>('/api/v0/proximityrules', request)
  return data
}

export async function deleteProximityRule(id: number): Promise<void> {
  await http.delete(`/api/v0/proximityrules/${id}`)
}
