// Leaflet popup/tooltip strings are injected as innerHTML; escape anything RF-derived.
export function escapeHtml(s: string): string {
  return s.replace(/[&<>"']/g, (c) => `&#${c.charCodeAt(0)};`)
}
