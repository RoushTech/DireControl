import http from './axios'

/** Mirrors DireControl.Enums.TxFrameKind (numeric JSON). */
export const TxFrameKinds = {
  Unknown: 0,
  Beacon: 1,
  Message: 2,
  Ack: 3,
  Digipeat: 4,
  Session: 5,
  Kiss: 6,
  Agwpe: 7,
  IsToRf: 8,
  TestTone: 9,
} as const
export type TxFrameKind = (typeof TxFrameKinds)[keyof typeof TxFrameKinds]

export const txFrameKindLabels: Record<TxFrameKind, string> = {
  [TxFrameKinds.Unknown]: 'Frame',
  [TxFrameKinds.Beacon]: 'Beacon',
  [TxFrameKinds.Message]: 'Message',
  [TxFrameKinds.Ack]: 'ACK',
  [TxFrameKinds.Digipeat]: 'Digipeat',
  [TxFrameKinds.Session]: 'Session',
  [TxFrameKinds.Kiss]: 'KISS',
  [TxFrameKinds.Agwpe]: 'AGWPE',
  [TxFrameKinds.IsToRf]: 'IS→RF',
  [TxFrameKinds.TestTone]: 'Test',
}

/** Mirrors DireControl.Enums.TxFrameState. Only Sent means it left the radio. */
export const TxFrameStates = {
  Unknown: 0,
  Queued: 1,
  WaitingForChannel: 2,
  OnAir: 3,
  Sent: 4,
  Failed: 5,
  Dropped: 6,
} as const
export type TxFrameState = (typeof TxFrameStates)[keyof typeof TxFrameStates]

export const isFinalTxState = (s: TxFrameState) =>
  s === TxFrameStates.Sent || s === TxFrameStates.Failed || s === TxFrameStates.Dropped

/** Mirrors DireControl.Enums.PttState. */
export const PttStates = { Unknown: 0, Released: 1, Keyed: 2, Stuck: 3 } as const
export type PttState = (typeof PttStates)[keyof typeof PttStates]

export interface TxFrameDto {
  id: number
  radioId: string | null
  radioName: string | null
  kind: TxFrameKind
  state: TxFrameState
  reason: string | null
  at: string
  queuedAt: string
  waitingSince: string | null
  onAirAt: string | null
  sentAt: string | null
  title: string
  detail: string | null
  /** The APRS message number for messages and ACKs. */
  ref: string | null
}

export interface TxQueuedResponse {
  txFrameIds: number[]
}

export async function getRecentTx(limit = 50): Promise<TxFrameDto[]> {
  const { data } = await http.get<TxFrameDto[]>('/api/v0/tx/recent', { params: { limit } })
  return data
}
