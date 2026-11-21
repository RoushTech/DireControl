import type { HealthLevel } from '@/stores/healthModel'

/** Vuetify colour for a health level. */
export function levelColor(level: HealthLevel): string {
  switch (level) {
    case 'ok':
      return 'success'
    case 'warn':
      return 'warning'
    case 'err':
      return 'error'
    case 'info':
      return 'info'
    default:
      return 'grey'
  }
}

export function levelIcon(level: HealthLevel): string {
  switch (level) {
    case 'ok':
      return 'mdi-check-circle'
    case 'warn':
      return 'mdi-alert'
    case 'err':
      return 'mdi-alert-circle'
    case 'info':
      return 'mdi-information'
    default:
      return 'mdi-circle-outline'
  }
}
