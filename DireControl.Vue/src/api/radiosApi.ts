import http from './axios'
import type {
  RadioDto,
  CreateRadioRequest,
  UpdateRadioRequest,
  LastBeaconDto,
  OwnBeaconHistoryItemDto,
} from '@/types/radio'

export async function getRadios(): Promise<RadioDto[]> {
  const { data } = await http.get<RadioDto[]>('/api/v0/radios')
  return data
}

export async function getRadio(id: string): Promise<RadioDto> {
  const { data } = await http.get<RadioDto>(`/api/v0/radios/${encodeURIComponent(id)}`)
  return data
}

export async function createRadio(request: CreateRadioRequest): Promise<RadioDto> {
  const { data } = await http.post<RadioDto>('/api/v0/radios', request)
  return data
}

export async function updateRadio(id: string, request: UpdateRadioRequest): Promise<RadioDto> {
  const { data } = await http.put<RadioDto>(`/api/v0/radios/${encodeURIComponent(id)}`, request)
  return data
}

export async function deleteRadio(id: string): Promise<void> {
  await http.delete(`/api/v0/radios/${encodeURIComponent(id)}`)
}

export async function toggleRadioActive(id: string): Promise<RadioDto> {
  const { data } = await http.patch<RadioDto>(`/api/v0/radios/${encodeURIComponent(id)}/active`)
  return data
}

export async function getLastBeacon(id: string): Promise<LastBeaconDto> {
  const { data } = await http.get<LastBeaconDto>(`/api/v0/radios/${encodeURIComponent(id)}/lastbeacon`)
  return data
}

export async function getBeaconHistory(id: string, limit = 50): Promise<OwnBeaconHistoryItemDto[]> {
  const { data } = await http.get<OwnBeaconHistoryItemDto[]>(
    `/api/v0/radios/${encodeURIComponent(id)}/beaconhistory`,
    { params: { limit } },
  )
  return data
}
