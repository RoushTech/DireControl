import { describe, expect, it } from 'vitest'
import { RetryState, type InboxMessageDto } from '@/types/message'
import { groupThread, isResendable, parsePart } from '../messageParts'

const ME = 'N7DCX'

function msg(id: number, from: string, body: string, extra: Partial<InboxMessageDto> = {}) {
  return {
    id,
    fromCallsign: from,
    toCallsign: from === ME ? 'K0LTR-9' : ME,
    body,
    messageId: String(id),
    pathUsed: null,
    receivedAt: '2026-11-21T12:00:00Z',
    receiveCount: 1,
    lastReceivedAt: null,
    source: null,
    heardVia: null,
    path: null,
    isRead: true,
    ackSent: false,
    replySent: false,
    retryCount: 0,
    maxRetries: 3,
    nextRetryAt: null,
    retryState: RetryState.Pending,
    lastSentAt: null,
    ...extra,
  } satisfies InboxMessageDto
}

const out = (m: InboxMessageDto) => m.fromCallsign === ME

describe('parsePart', () => {
  it('reads the server suffix', () => {
    expect(parsePart('Net reminder Thursday (2/3)')).toEqual({
      n: 2,
      m: 3,
      text: 'Net reminder Thursday',
    })
  })
  it('ignores single messages and nonsense counters', () => {
    expect(parsePart('73 de N7DCX')).toBeNull()
    expect(parsePart('ok (1/1)')).toBeNull()
    expect(parsePart('ok (4/3)')).toBeNull()
  })
})

describe('groupThread', () => {
  it('folds one compose into one item, in part order, even when stored out of order', () => {
    const items = groupThread(
      [
        msg(1, 'K0LTR-9', 'hi'),
        msg(3, ME, 'b (2/3)'),
        msg(2, ME, 'a (1/3)'),
        msg(4, ME, 'c (3/3)'),
        msg(5, 'K0LTR-9', 'got it'),
      ],
      out,
    )
    expect(items.map((i) => i.kind)).toEqual(['single', 'multi', 'single'])
    const multi = items[1]!
    expect(multi.kind === 'multi' && multi.parts.map((p) => p.msg?.id)).toEqual([2, 3, 4])
  })

  it('splits back-to-back composes and fills never-queued gaps', () => {
    const items = groupThread(
      [msg(1, ME, 'a (1/4)'), msg(2, ME, 'b (2/4)'), msg(3, ME, 'x (1/2)'), msg(4, ME, 'y (2/2)')],
      out,
    )
    expect(items).toHaveLength(2)
    const first = items[0]!
    expect(first.kind === 'multi' && first.parts.map((p) => p.msg?.id ?? null)).toEqual([
      1,
      2,
      null,
      null,
    ])
  })

  it('leaves inbound "(n/m)" text alone', () => {
    const items = groupThread([msg(1, 'K0LTR-9', 'a (1/2)'), msg(2, 'K0LTR-9', 'b (2/2)')], out)
    expect(items.map((i) => i.kind)).toEqual(['single', 'single'])
  })
})

describe('isResendable', () => {
  it('is true only for failed or cancelled parts', () => {
    expect(isResendable(msg(1, ME, 'a', { retryState: RetryState.Failed }))).toBe(true)
    expect(isResendable(msg(1, ME, 'a', { retryState: RetryState.Cancelled }))).toBe(true)
    expect(isResendable(msg(1, ME, 'a', { retryState: RetryState.Retrying }))).toBe(false)
    expect(isResendable(null)).toBe(false)
  })
})
