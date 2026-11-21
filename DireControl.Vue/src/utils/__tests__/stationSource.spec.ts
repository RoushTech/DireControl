import { describe, expect, it } from 'vitest'
import { heardOnRf, matchesSourceFilter } from '../stationSource'

const NOW = Date.parse('2026-11-21T12:00:00Z')
const WINDOW = 120 * 60_000

function at(minutesAgo: number): string {
  return new Date(NOW - minutesAgo * 60_000).toISOString()
}

describe('heardOnRf', () => {
  it('is false for a station the radio never heard', () => {
    expect(heardOnRf({ lastHeardRf: null, lastSeen: at(1) }, WINDOW, NOW)).toBe(false)
  })

  it('is true while the last RF reception is inside the window', () => {
    expect(heardOnRf({ lastHeardRf: at(119), lastSeen: at(1) }, WINDOW, NOW)).toBe(true)
  })

  it('is false once the last RF reception ages out, even if APRS-IS keeps it active', () => {
    expect(heardOnRf({ lastHeardRf: at(121), lastSeen: at(1) }, WINDOW, NOW)).toBe(false)
  })

  it('measures a stale station against its own last packet', () => {
    const stale = { lastHeardRf: at(600), lastSeen: at(590) }
    expect(heardOnRf(stale, WINDOW, NOW)).toBe(false)
    expect(heardOnRf(stale, WINDOW, NOW, true)).toBe(true)
  })

  it('keeps a stale station internet-only when RF was long before its last packet', () => {
    const stale = { lastHeardRf: at(900), lastSeen: at(590) }
    expect(heardOnRf(stale, WINDOW, NOW, true)).toBe(false)
  })
})

describe('matchesSourceFilter', () => {
  const rf = { lastHeardRf: at(5), lastSeen: at(5) }
  const isOnly = { lastHeardRf: null, lastSeen: at(5) }

  it('shows everything for "all"', () => {
    expect(matchesSourceFilter('all', rf, WINDOW, NOW)).toBe(true)
    expect(matchesSourceFilter('all', isOnly, WINDOW, NOW)).toBe(true)
  })

  it('puts each station in exactly one of RF and APRS-IS', () => {
    expect(matchesSourceFilter('rf', rf, WINDOW, NOW)).toBe(true)
    expect(matchesSourceFilter('is', rf, WINDOW, NOW)).toBe(false)
    expect(matchesSourceFilter('rf', isOnly, WINDOW, NOW)).toBe(false)
    expect(matchesSourceFilter('is', isOnly, WINDOW, NOW)).toBe(true)
  })
})
