import http from './axios'

export interface CleanupResult {
  startedAt: string
  completedAt: string
  rfDeleted: number
  aprsIsDeleted: number
  ownDeleted: number
  vacuumed: boolean
  vacuumError?: string | null
  sizeBeforeBytes: number
  sizeAfterBytes: number
  error?: string | null
}

export interface RetentionDto {
  rfDays: number
  aprsIsDays: number
  ownDays: number
}

export interface MaintenanceStatusDto {
  isRunning: boolean
  databaseSizeBytes: number
  retention: RetentionDto
  cleanupIntervalHours: number
  vacuumOnCleanup: boolean
  lastResult: CleanupResult | null
}

export async function getMaintenanceStatus(): Promise<MaintenanceStatusDto> {
  const { data } = await http.get<MaintenanceStatusDto>('/api/v0/maintenance/status')
  return data
}

export async function updateRetention(retention: RetentionDto): Promise<void> {
  await http.put('/api/v0/maintenance/retention', retention)
}

/** Triggers a cleanup run (prune + VACUUM) in the background. */
export async function runCleanup(): Promise<void> {
  await http.post('/api/v0/maintenance/cleanup')
}

// ─── Packet reprocessing ──────────────────────────────────────────────────────

export interface ReprocessRequest {
  /** Re-derive every matching packet, not just ones behind the current parser version. */
  force?: boolean
  source?: 'Rf' | 'AprsIs' | 'Own' | null
  /** Inclusive lower bound on ReceivedAt (UTC ISO). */
  after?: string | null
  /** Exclusive upper bound on ReceivedAt (UTC ISO). */
  before?: string | null
  /** After the run, delete stations left with no packets (watch-listed excluded). */
  deleteOrphanStations?: boolean
}

export interface ReprocessResult {
  startedAt: string
  completedAt: string
  processed: number
  failed: number
  orphanStationsDeleted: number
  error: string | null
}

export interface ReprocessStatusDto {
  isRunning: boolean
  processed: number
  /** Matched row count — 0 until the count query finishes after start. */
  total: number
  currentParserVersion: number
  lastResult: ReprocessResult | null
}

export async function getReprocessStatus(): Promise<ReprocessStatusDto> {
  const { data } = await http.get<ReprocessStatusDto>('/api/v0/maintenance/reprocess')
  return data
}

/** Starts a background reprocess run; 409s if one is already running. */
export async function startReprocess(request?: ReprocessRequest): Promise<void> {
  await http.post('/api/v0/maintenance/reprocess', request ?? {})
}
