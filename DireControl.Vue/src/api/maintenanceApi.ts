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
