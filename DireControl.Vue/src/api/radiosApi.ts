import http from './axios'
import type { TxQueuedResponse } from './txApi'
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
  const { data } = await http.get<LastBeaconDto>(
    `/api/v0/radios/${encodeURIComponent(id)}/lastbeacon`,
  )
  return data
}

export async function getBeaconHistory(id: string, limit = 50): Promise<OwnBeaconHistoryItemDto[]> {
  const { data } = await http.get<OwnBeaconHistoryItemDto[]>(
    `/api/v0/radios/${encodeURIComponent(id)}/beaconhistory`,
    { params: { limit } },
  )
  return data
}

/** Queues a beacon; returns its TX frame id(s) — queued, not yet sent. */
export async function beaconNow(id: string): Promise<TxQueuedResponse> {
  const { data } = await http.post<TxQueuedResponse>(
    `/api/v0/radios/${encodeURIComponent(id)}/beacon`,
  )
  return data
}

export interface PttTestResultDto {
  txFrameId: number
  /** The keyup completed: PTT keyed for 1 s and released. */
  keyed: boolean
  reason: string | null
}

/** Keys the radio's saved PTT for 1 s (no RF ID) and waits for the outcome. */
export async function pttTest(id: string): Promise<PttTestResultDto> {
  const { data } = await http.post<PttTestResultDto>(
    `/api/v0/radios/${encodeURIComponent(id)}/ptt-test`,
  )
  return data
}
