/**
 * Theme-driven chart colours. Chart.js draws on a canvas, so it can't use CSS
 * theme variables — read them from `useTheme().current.value.colors` and pass
 * them through here. A theme change only needs to touch this file.
 */
type ThemeColors = Readonly<Record<string, unknown>>

/** A theme colour by name, when it is a plain string (hex). */
function pick(colors: ThemeColors, name: string): string | undefined {
  const c = colors[name]
  return typeof c === 'string' ? c : undefined
}

/** RF is the amber/warning family, APRS-IS a violet. */
export function sourceColors(colors: ThemeColors): { rf: string; is: string } {
  return {
    rf: pick(colors, 'rf') ?? pick(colors, 'warning') ?? '#b45309',
    is: pick(colors, 'is') ?? '#9575cd',
  }
}

/** `#rrggbb` (or `#rgb`) → `rgba(r, g, b, alpha)`; other strings pass through unchanged. */
export function withAlpha(color: string, alpha: number): string {
  let hex = color.trim()
  if (!hex.startsWith('#')) return color
  hex = hex.slice(1)
  if (hex.length === 3)
    hex = hex
      .split('')
      .map((c) => c + c)
      .join('')
  if (hex.length !== 6 && hex.length !== 8) return color
  const r = parseInt(hex.slice(0, 2), 16)
  const g = parseInt(hex.slice(2, 4), 16)
  const b = parseInt(hex.slice(4, 6), 16)
  return `rgba(${r}, ${g}, ${b}, ${alpha})`
}

/** Tick-label ink and grid-line colours derived from the theme's on-surface colour. */
export function axisColors(colors: ThemeColors): { ink: string; grid: string } {
  const onSurface = pick(colors, 'on-surface') ?? '#1b2733'
  return { ink: withAlpha(onSurface, 0.72), grid: withAlpha(onSurface, 0.1) }
}
