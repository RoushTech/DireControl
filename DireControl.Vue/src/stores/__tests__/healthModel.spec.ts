import { describe, expect, it } from 'vitest'
import { ModemStates } from '@/api/modemApi'
import { AprsIsStates } from '@/api/statusApi'
import {
  agoShort,
  deriveBanners,
  deriveHealth,
  deriveSetupSteps,
  type HealthInput,
  type ModemHealthInput,
} from '@/stores/healthModel'

// The eight mock states (plan/ux-overhaul-mock-2026-11/src/02-engine.js),
// rebuilt as raw DTO inputs.
const T = Date.UTC(2026, 10, 21, 12, 0, 0)
const iso = (msAgo: number) => new Date(T - msAgo).toISOString()

function modem(o: Partial<ModemHealthInput> = {}): ModemHealthInput {
  return {
    state: ModemStates.Running,
    errorMessage: null,
    lastDecodedAt: iso(14_000),
    runningSince: iso(3_600_000),
    carrierDetected: false,
    carrierSince: null,
    preamblesSinceCarrier: 0,
    carrierStuck: false,
    pttStuck: false,
    transmitting: false,
    csmaWaitingSince: null,
    txQueueDepth: 0,
    txEnabled: true,
    txError: null,
    ...o,
  }
}

function input(o: Partial<HealthInput> = {}, m: Partial<ModemHealthInput> = {}): HealthInput {
  return {
    now: T,
    apiOffline: false,
    hubState: 'connected',
    hubDownSince: null,
    hubEverConnected: true,
    radiosLoaded: true,
    radios: [
      { radioId: 'r1', name: 'FT-8100R', modemEnabled: true, txConfigured: true, modem: modem(m) },
    ],
    callsign: 'N7DCX-10',
    externalTncConnected: false,
    lastRfPacketAt: null,
    aprsIs: {
      state: AprsIsStates.Connected,
      lastError: null,
      serverName: 'T2DENVER',
      lastPacketAt: T - 2000,
    },
    rfSilentMs: 15 * 60_000,
    ...o,
  }
}

const bannerOpts = { csmaMaxSeconds: 20, setupDone: 4, setupTotal: 4, setupDismissed: false }

describe('deriveHealth — mock states', () => {
  it('healthy: all normal, lanes ok, RF shows time since last decode', () => {
    const h = deriveHealth(input())
    expect(h.worst).toBe('ok')
    expect(h.label).toBe('All normal')
    expect(h.lanes.rf).toMatchObject({ level: 'ok', value: '14 s' })
    expect(h.lanes.is).toMatchObject({ level: 'ok', value: '2 s' })
    expect(h.lanes.tx).toMatchObject({ level: 'ok', value: 'Idle' })
    expect(h.lanes.link.level).toBe('ok')
    expect(h.txBlockedReason).toBeNull()
  })

  it('RF deaf with IS flowing: RF silent even though APRS-IS is fresh', () => {
    const x = input({}, { lastDecodedAt: iso(23 * 60_000 + 12_000) })
    const h = deriveHealth(x)
    expect(h.lanes.rf).toMatchObject({ level: 'warn', value: 'Silent 23 min' })
    expect(h.lanes.is.level).toBe('ok')
    expect(h.label).toBe('RF silent')
    const b = deriveBanners(x, h, bannerOpts)
    expect(b.map((y) => y.id)).toEqual(['silent-r1'])
    expect(b[0]!.text).toContain('APRS-IS is flowing')
  })

  it('RF silent threshold is configurable', () => {
    const h = deriveHealth(input({ rfSilentMs: 60 * 60_000 }, { lastDecodedAt: iso(23 * 60_000) }))
    expect(h.lanes.rf.level).toBe('ok')
  })

  it('carrier held: warn after 10 s with no preambles; TX waiting is visible', () => {
    const x = input(
      {},
      {
        carrierDetected: true,
        carrierSince: iso(47_000),
        preamblesSinceCarrier: 0,
        csmaWaitingSince: iso(6000),
        txQueueDepth: 3,
      },
    )
    const h = deriveHealth(x)
    expect(h.lanes.rf).toMatchObject({ level: 'warn', value: 'Carrier held 47 s' })
    expect(h.lanes.tx).toMatchObject({ level: 'warn', value: 'Waiting 6 s' })
    expect(h.label).toBe('Carrier held')
    const b = deriveBanners(x, h, bannerOpts)
    expect(b[0]!.id).toBe('carrier-r1')
    expect(b[0]!.text).toContain('discarded after 20 s')
  })

  it('a long carrier that produces preambles is a real transmission, not held', () => {
    const h = deriveHealth(
      input({}, { carrierDetected: true, carrierSince: iso(12_000), preamblesSinceCarrier: 2 }),
    )
    expect(h.lanes.rf.level).toBe('ok')
  })

  it('modem error: RF and TX error, Restart modem banner', () => {
    const x = input(
      {},
      { state: ModemStates.Error, errorMessage: 'snd_pcm_open failed: No such device' },
    )
    const h = deriveHealth(x)
    expect(h.worst).toBe('err')
    expect(h.label).toBe('Modem error')
    expect(h.lanes.rf.value).toBe('Modem error')
    expect(h.lanes.tx.value).toBe('Unavailable')
    expect(h.txBlockedReason).toContain('modem is in error')
    const b = deriveBanners(x, h, bannerOpts)
    expect(b[0]!.id).toBe('modem-r1')
    expect(b[0]!.actions.map((a) => a.label)).toEqual(['Restart modem', 'Open Radio'])
  })

  it('PTT stuck: label names it, banner offers Force unkey / Stop modem', () => {
    const x = input(
      {},
      { state: ModemStates.Error, pttStuck: true, txError: 'PTT could not be released' },
    )
    const h = deriveHealth(x)
    expect(h.label).toBe('PTT stuck')
    expect(h.lanes.tx).toMatchObject({ level: 'err', value: 'PTT stuck' })
    expect(h.lanes.rf.value).toBe('Blind (TX)')
    expect(h.txBlockedReason).toContain('PTT is stuck')
    const b = deriveBanners(x, h, bannerOpts)
    expect(b[0]!.id).toBe('ptt-r1')
    expect(b[0]!.actions.map((a) => a.label)).toEqual(['Force unkey', 'Stop modem'])
  })

  it('APRS-IS auth failed: IS lane error, RF still ok', () => {
    const x = input({
      aprsIs: {
        state: AprsIsStates.AuthFailed,
        lastError: 'unverified',
        serverName: null,
        lastPacketAt: T - 41 * 60_000,
      },
    })
    const h = deriveHealth(x)
    expect(h.lanes.is).toMatchObject({ level: 'err', value: 'Auth failed' })
    expect(h.lanes.is.action?.to).toBe('/settings/aprsis')
    expect(h.lanes.rf.level).toBe('ok')
    expect(h.label).toBe('APRS-IS auth failed')
    const b = deriveBanners(x, h, bannerOpts)
    expect(b.map((y) => y.id)).toEqual(['is-auth'])
  })

  it('hub down: link error, other lanes greyed and stale, only the feed banner', () => {
    const x = input(
      { hubState: 'disconnected', hubDownSince: T - 19_000 },
      { lastDecodedAt: iso(23 * 60_000) },
    )
    const h = deriveHealth(x)
    expect(h.live).toBe(false)
    expect(h.lanes.link).toMatchObject({ level: 'err', value: 'Offline 19 s' })
    expect(h.label).toBe('Not live')
    for (const k of ['rf', 'tx', 'is'] as const) {
      expect(h.lanes[k].stale).toBe(true)
      expect(h.lanes[k].level).toBe('off')
    }
    const b = deriveBanners(x, h, bannerOpts)
    expect(b.map((y) => y.id)).toEqual(['hub'])
    expect(b[0]!.actions[0]).toMatchObject({ run: 'hubReconnect' })
  })

  it('hub down keeps errors red rather than greying them', () => {
    const h = deriveHealth(
      input({ hubState: 'disconnected', hubDownSince: T - 5000 }, { state: ModemStates.Error }),
    )
    expect(h.lanes.rf.level).toBe('err')
    expect(h.lanes.rf.stale).toBe(true)
  })

  it('resyncing: link warn, label Resyncing', () => {
    const h = deriveHealth(input({ hubState: 'resyncing' }))
    expect(h.lanes.link).toMatchObject({ level: 'warn', value: 'Resyncing…' })
    expect(h.label).toBe('Resyncing')
    expect(h.live).toBe(true)
  })

  it('first run: N0CALL and no radios → Setup needed, TX blocked', () => {
    const x = input({
      callsign: 'N0CALL-10',
      radios: [],
      aprsIs: {
        state: AprsIsStates.Disabled,
        lastError: null,
        serverName: null,
        lastPacketAt: null,
      },
    })
    const h = deriveHealth(x)
    expect(h.setupNeeded).toBe(true)
    expect(h.label).toBe('Setup needed')
    expect(h.worst).toBe('info')
    expect(h.lanes.rf).toMatchObject({ level: 'info', value: 'No radio' })
    expect(h.lanes.is.level).toBe('off')
    expect(h.txBlockedReason).toContain('No radio')
    const b = deriveBanners(x, h, { ...bannerOpts, setupDone: 0 })
    expect(b.map((y) => y.id)).toEqual(['setup'])
    expect(b[0]!.text).toContain('N0CALL-10')
  })

  it('N0CALL with a working radio blocks TX', () => {
    const h = deriveHealth(input({ callsign: 'n0call' }))
    expect(h.lanes.tx).toMatchObject({ level: 'warn', value: 'Blocked' })
    expect(h.txBlockedReason).toBe('Set your callsign before transmitting.')
    expect(h.label).toBe('Setup needed')
  })

  it('dismissing setup hides the banner, not the status label', () => {
    const x = input({ callsign: 'N0CALL' })
    const h = deriveHealth(x)
    expect(deriveBanners(x, h, { ...bannerOpts, setupDismissed: true })).toEqual([])
    expect(h.label).toBe('Setup needed')
  })
})

describe('deriveHealth — multi-radio and lane order', () => {
  it('the strip shows the worst radio; per-radio lanes are kept', () => {
    const x = input()
    x.radios.push({
      radioId: 'r2',
      name: 'Second',
      modemEnabled: true,
      txConfigured: false,
      modem: modem({ lastDecodedAt: iso(40 * 60_000) }),
    })
    const h = deriveHealth(x)
    expect(h.radios).toHaveLength(2)
    expect(h.lanes.rf.detail).toContain('Second')
    expect(h.lanes.tx.value).toBe('Idle')
  })

  it('on air shows in the TX lane', () => {
    const h = deriveHealth(input({}, { transmitting: true }))
    expect(h.lanes.tx).toMatchObject({ value: 'On air', onAir: true, level: 'ok' })
  })

  it('link outranks RF when both are bad (lane order link → rf → tx → is)', () => {
    const h = deriveHealth(input({ apiOffline: true }, { state: ModemStates.Error }))
    expect(h.label).toBe('API offline')
  })

  it('external-TNC radio: RF from hub packets, warns when the TNC is down', () => {
    const x = input({
      radios: [
        { radioId: 'k', name: 'KISS', modemEnabled: false, txConfigured: true, modem: null },
      ],
      externalTncConnected: false,
    })
    expect(deriveHealth(x).lanes.rf.value).toBe('No TNC')
    x.externalTncConnected = true
    x.lastRfPacketAt = T - 5000
    expect(deriveHealth(x).lanes.rf).toMatchObject({ level: 'ok', value: '5 s' })
  })
})

describe('agoShort', () => {
  it.each([
    [14_000, '14 s'],
    [23 * 60_000, '23 min'],
    [125 * 60_000, '2 h 5 m'],
    [3 * 86_400_000, '3 d'],
  ])('%d ms → %s', (msAgo, out) => {
    expect(agoShort(T - msAgo, T)).toBe(out)
  })
  it('never', () => expect(agoShort(null, T)).toBe('never'))
})

describe('deriveSetupSteps', () => {
  it('first run: nothing done, callsign flagged', () => {
    const s = deriveSetupSteps({
      callsign: 'N0CALL-10',
      homeSet: false,
      radios: [],
      pttTested: false,
      aprsIsState: AprsIsStates.Disabled,
      aprsIsServerName: null,
    })
    expect(s.map((x) => x.done)).toEqual([false, false, false, false, false])
    expect(s[0]!.bad).toBe(true)
    expect(s[4]!.optional).toBe(true)
  })

  it('PTT step needs a confirmed test on an audio radio', () => {
    const base = {
      callsign: 'N7DCX',
      homeSet: true,
      radios: [{ name: 'FT', modemEnabled: true, txEnabled: true, captureDevice: 'default' }],
      aprsIsState: AprsIsStates.Connected,
      aprsIsServerName: 'T2',
    }
    expect(deriveSetupSteps({ ...base, pttTested: false })[3]!.done).toBe(false)
    expect(deriveSetupSteps({ ...base, pttTested: true }).every((x) => x.done)).toBe(true)
  })
})
