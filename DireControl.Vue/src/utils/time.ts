import { ref } from 'vue'
import { serverNow } from '@/utils/serverTime'

// ─── Display time zone ────────────────────────────────────────────────────────
// Every absolute time in the UI goes through the formatters below so the whole
// app shows one zone.  UTC (suffixed "Z") is the default; local time is a
// per-browser preference.

export type TimeZonePref = 'utc' | 'local'

const TIME_ZONE_KEY = 'direcontrol.timeZone'

function readTimeZonePref(): TimeZonePref {
  try {
    return localStorage.getItem(TIME_ZONE_KEY) === 'local' ? 'local' : 'utc'
  } catch {
    return 'utc'
  }
}

/** Reactive, so every formatted time re-renders when the preference changes. */
export const timeZonePref = ref<TimeZonePref>(readTimeZonePref())

export function setTimeZonePref(pref: TimeZonePref) {
  timeZonePref.value = pref
  try {
    localStorage.setItem(TIME_ZONE_KEY, pref)
  } catch {
    // Private window etc. — the choice lasts for this page load.
  }
}

const isUtc = () => timeZonePref.value === 'utc'
const zoneSuffix = () => (isUtc() ? 'Z' : '')
const pad = (n: number, w = 2) => String(n).padStart(w, '0')
const toDate = (value: string | number | Date) => (value instanceof Date ? value : new Date(value))

/** Hour of day (0–23) in the display zone — for "midnight" chart labels etc. */
export function hourOf(value: string | number | Date): number {
  const d = toDate(value)
  return isUtc() ? d.getUTCHours() : d.getHours()
}

/** "YYYY-MM-DD HH:MM:SS" in the display zone ("Z"-suffixed when UTC). */
export function formatDateTime(value: string | number | Date): string {
  const d = toDate(value)
  const [y, mo, day, h, mi, sec] = isUtc()
    ? [
        d.getUTCFullYear(),
        d.getUTCMonth() + 1,
        d.getUTCDate(),
        d.getUTCHours(),
        d.getUTCMinutes(),
        d.getUTCSeconds(),
      ]
    : [d.getFullYear(), d.getMonth() + 1, d.getDate(), d.getHours(), d.getMinutes(), d.getSeconds()]
  return `${y}-${pad(mo)}-${pad(day)} ${pad(h)}:${pad(mi)}:${pad(sec)}${zoneSuffix()}`
}

/** Time of day in the display zone — for tight columns where the date is implied. */
export function formatTimeOfDay(
  value: string | number | Date,
  { seconds = true, millis = false }: { seconds?: boolean; millis?: boolean } = {},
): string {
  const d = toDate(value)
  const [h, mi, sec, ms] = isUtc()
    ? [d.getUTCHours(), d.getUTCMinutes(), d.getUTCSeconds(), d.getUTCMilliseconds()]
    : [d.getHours(), d.getMinutes(), d.getSeconds(), d.getMilliseconds()]
  let out = `${pad(h)}:${pad(mi)}`
  if (seconds || millis) out += `:${pad(sec)}`
  if (millis) out += `.${pad(ms, 3)}`
  return out + zoneSuffix()
}

/** A locale-formatted date label (e.g. weekday, "Nov 21") in the display zone. */
export function formatDateLabel(
  value: string | number | Date,
  options: Intl.DateTimeFormatOptions,
): string {
  return toDate(value).toLocaleDateString([], { ...options, timeZone: isUtc() ? 'UTC' : undefined })
}

export function timeAgo(isoDate: string, now = serverNow()): string {
  const diff = Math.max(0, Math.floor((now - new Date(isoDate).getTime()) / 1000))
  if (diff < 60) return `${diff}s ago`
  if (diff < 3600) return `${Math.floor(diff / 60)}m ago`
  if (diff < 86400) return `${Math.floor(diff / 3600)}h ago`
  return `${Math.floor(diff / 86400)}d ago`
}

/** Finer-grained "ago" from a count of seconds: "45s ago", "3m 20s ago", "2h 5m ago". */
export function formatSecondsAgo(seconds: number | null): string {
  if (seconds === null) return 'never'
  const secs = Math.max(0, Math.floor(seconds))
  if (secs < 1) return 'just now'
  if (secs < 60) return `${secs}s ago`
  if (secs < 3600) return `${Math.floor(secs / 60)}m ${secs % 60}s ago`
  return `${Math.floor(secs / 3600)}h ${Math.floor((secs % 3600) / 60)}m ago`
}

const COMPASS_DIRS = ['N', 'NE', 'E', 'SE', 'S', 'SW', 'W', 'NW'] as const

export function compassDir(degrees: number): string {
  return COMPASS_DIRS[Math.round((((degrees % 360) + 360) % 360) / 45) % 8] ?? 'N'
}

const COMPASS_DIRS_16 = [
  'N',
  'NNE',
  'NE',
  'ENE',
  'E',
  'ESE',
  'SE',
  'SSE',
  'S',
  'SSW',
  'SW',
  'WSW',
  'W',
  'WNW',
  'NW',
  'NNW',
] as const

export function compassDir16(degrees: number): string {
  return COMPASS_DIRS_16[Math.round((((degrees % 360) + 360) % 360) / 22.5) % 16] ?? 'N'
}
