export function timeAgo(isoDate: string): string {
  const diff = Math.floor((Date.now() - new Date(isoDate).getTime()) / 1000)
  if (diff < 60) return `${diff}s ago`
  if (diff < 3600) return `${Math.floor(diff / 60)}m ago`
  if (diff < 86400) return `${Math.floor(diff / 3600)}h ago`
  return `${Math.floor(diff / 86400)}d ago`
}

export function formatUtc(isoDate: string): string {
  return new Date(isoDate).toLocaleString()
}

const COMPASS_DIRS = ['N', 'NE', 'E', 'SE', 'S', 'SW', 'W', 'NW'] as const

export function compassDir(degrees: number): string {
  return COMPASS_DIRS[Math.round((((degrees % 360) + 360) % 360) / 45) % 8] ?? 'N'
}

const COMPASS_DIRS_16 = [
  'N', 'NNE', 'NE', 'ENE', 'E', 'ESE', 'SE', 'SSE',
  'S', 'SSW', 'SW', 'WSW', 'W', 'WNW', 'NW', 'NNW',
] as const

export function compassDir16(degrees: number): string {
  return COMPASS_DIRS_16[Math.round((((degrees % 360) + 360) % 360) / 22.5) % 16] ?? 'N'
}
