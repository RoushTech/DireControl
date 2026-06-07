export interface LogEntryDto {
  sequence: number
  timestamp: string
  level: string
  category: string
  message: string
  exception?: string | null
}

/** Vuetify colors keyed by log level name. */
export const LOG_LEVEL_COLORS: Record<string, string> = {
  Trace: 'grey',
  Debug: 'blue-grey',
  Information: 'info',
  Warning: 'warning',
  Error: 'error',
  Critical: 'red-darken-3',
}

/** Numeric rank for "this level and above" filtering. */
export const LOG_LEVEL_RANK: Record<string, number> = {
  Trace: 0,
  Debug: 1,
  Information: 2,
  Warning: 3,
  Error: 4,
  Critical: 5,
}

/** Last dotted segment of a logger category, e.g. "KissTcpService". */
export function shortCategory(category: string): string {
  const idx = category.lastIndexOf('.')
  return idx >= 0 ? category.slice(idx + 1) : category
}
