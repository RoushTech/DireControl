/**
 * Settings sections, in nav order. `restart` is what a save of that section
 * restarts (review 1.10); `instant` sections apply without a save.
 */
export interface SettingsSectionMeta {
  id: string
  title: string
  icon: string
  restart: string | null
  instant?: boolean
}

export const SETTINGS_SECTIONS: SettingsSectionMeta[] = [
  {
    id: 'station',
    title: 'Station',
    icon: 'mdi-account',
    restart: 'Callsign change reconnects APRS-IS and restarts PMS and terminal listeners',
  },
  {
    id: 'radios',
    title: 'Radios',
    icon: 'mdi-radio',
    restart: "Each radio change restarts only that radio's modem; KISS clients stay connected",
  },
  {
    id: 'rf',
    title: 'RF services',
    icon: 'mdi-radio-tower',
    restart: 'Restarts the modem and KISS server; connected KISS clients drop',
  },
  {
    id: 'packet',
    title: 'Packet & PMS',
    icon: 'mdi-console-network',
    restart: 'Restarts PMS, terminal listener and AGWPE; open sessions end',
  },
  { id: 'aprsis', title: 'APRS-IS', icon: 'mdi-web', restart: 'Reconnects to APRS-IS' },
  { id: 'map', title: 'Map & weather', icon: 'mdi-map', restart: null },
  { id: 'zones', title: 'Alert zones', icon: 'mdi-map-marker-radius', restart: null },
  { id: 'maintenance', title: 'Maintenance', icon: 'mdi-database', restart: null },
  { id: 'display', title: 'Display', icon: 'mdi-monitor', restart: null, instant: true },
]

export const DEFAULT_SETTINGS_SECTION = 'station'

export function isSettingsSection(id: unknown): id is string {
  return typeof id === 'string' && SETTINGS_SECTIONS.some((s) => s.id === id)
}

export function settingsSection(id: string): SettingsSectionMeta {
  return SETTINGS_SECTIONS.find((s) => s.id === id) ?? SETTINGS_SECTIONS[0]!
}
