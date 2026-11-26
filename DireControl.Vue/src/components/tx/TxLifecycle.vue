<script setup lang="ts">
import { computed } from 'vue'
import { TxFrameKinds, TxFrameStates } from '@/api/txApi'
import { BEACON_HEARD_WINDOW_MS, type TxItem } from '@/stores/tx'
import { durationShort } from '@/stores/health'
import { useHealthStore } from '@/stores/health'

/**
 * One frame's lifecycle as steps: Queued → Clear channel → On air → done
 * (Heard / Acked / Sent). Failures mark the step where it stopped with the
 * reason. "Queued" is never "sent" — only the last step means it left the
 * radio.
 */
const props = defineProps<{
  item: TxItem
  compact?: boolean
  /** Message parts: the ACK outcome lives on the message, not the frame. */
  acked?: boolean
  gaveUp?: boolean
}>()

const health = useHealthStore()

type StepState = 'done' | 'cur' | 'fail' | 'todo'
interface Step {
  key: string
  label: string
  /** Plain-language explanation of the step, for the tooltip. */
  tip: string
  state: StepState
  warn?: boolean
  live?: boolean
}

const doneLabel = computed(() => {
  switch (props.item.kind) {
    case TxFrameKinds.Beacon:
      return 'Heard'
    case TxFrameKinds.Message:
      return 'Acked'
    case TxFrameKinds.TestTone:
      return 'Done'
    default:
      return 'Sent'
  }
})

/** Sent, but the kind has a confirmation still to come. */
const awaiting = computed(
  () =>
    props.item.state === TxFrameStates.Sent &&
    ((props.item.kind === TxFrameKinds.Beacon && !props.item.outcome) ||
      (props.item.kind === TxFrameKinds.Message && !props.acked && !props.gaveUp)),
)

const failed = computed(
  () =>
    props.item.state === TxFrameStates.Failed ||
    props.item.state === TxFrameStates.Dropped ||
    (props.item.kind === TxFrameKinds.Message && props.gaveUp && !props.acked),
)

const HEARD_WINDOW_S = Math.round(BEACON_HEARD_WINDOW_MS / 1000)

/** What the step means in its current state, since the dots alone say nothing. */
function stepTip(i: number, state: StepState, failAt: number): string {
  const it = props.item
  const why = it.reason ? ` Reason: ${it.reason}` : ''
  if (state === 'fail') {
    if (failAt === 0)
      return it.state === TxFrameStates.Dropped
        ? `Removed from the transmit queue before it was sent.${why}`
        : `Never reached the radio, so nothing was transmitted.${why}`
    if (failAt === 1)
      return `The frequency never went quiet long enough to transmit, so the frame was abandoned.${why}`
    if (failAt === 2) return `The radio failed while transmitting this frame.${why}`
    return props.gaveUp
      ? 'Transmitted on every retry, but the recipient never acknowledged it.'
      : `Transmitted, but not confirmed.${why}`
  }
  switch (i) {
    case 0:
      return state === 'cur'
        ? 'In the transmit queue, waiting for frames ahead of it.'
        : 'Took its turn in the transmit queue.'
    case 1:
      return state === 'cur'
        ? 'Waiting for the frequency to go quiet so it does not transmit over another station.'
        : state === 'done'
          ? 'The frequency was quiet, so it was safe to transmit.'
          : 'Not reached. Waits for the frequency to go quiet before transmitting.'
    case 2:
      return state === 'cur'
        ? 'The radio is keyed and transmitting this frame right now.'
        : state === 'done'
          ? 'The radio keyed up and transmitted the frame.'
          : 'Not reached. The radio keys up and transmits the frame.'
  }
  if (it.kind === TxFrameKinds.Beacon) {
    if (it.outcome === 'unverified')
      return `Transmitted, but no digipeater was heard repeating it within ${HEARD_WINDOW_S} s. It may still have got out: either nothing in range repeated it, or this receiver missed the repeat.`
    if (state === 'cur')
      return `Transmitted. Listening up to ${HEARD_WINDOW_S} s for a digipeater to repeat it, which proves it got out.`
    if (state === 'done')
      return `A digipeater${it.heardVia ? ` (${it.heardVia})` : ''} repeated the beacon and it was heard back, so it got out.`
    return 'Not reached. A digipeater repeating the beacon proves it got out.'
  }
  if (it.kind === TxFrameKinds.Message) {
    if (state === 'cur') return 'Transmitted. Waiting for the recipient to acknowledge it.'
    if (state === 'done') return 'The recipient acknowledged the message.'
    return 'Not reached. The recipient acknowledges the message.'
  }
  return state === 'done'
    ? 'Transmission finished. This kind of frame has no confirmation to wait for.'
    : 'Not reached. Transmission finished.'
}

const steps = computed<Step[]>(() => {
  const it = props.item
  const s = it.state
  const names = ['Queued', 'Clear channel', 'On air', doneLabel.value]
  // Index of the step reached (0..3), 3.5 = fully done.
  let cur: number
  switch (s) {
    case TxFrameStates.Queued:
      cur = 0
      break
    case TxFrameStates.WaitingForChannel:
      cur = 1
      break
    case TxFrameStates.OnAir:
      cur = 2
      break
    case TxFrameStates.Sent:
      cur = awaiting.value ? 3 : 3.5
      break
    default:
      cur = -1
  }
  let failAt = -1
  if (failed.value) {
    if (s === TxFrameStates.Sent) failAt = 3
    else failAt = it.onAirAt ? 2 : it.waitingSince ? 1 : 0
  }
  return names.map((name, i) => {
    let state: StepState = 'todo'
    if (failAt >= 0) state = i < failAt ? 'done' : i === failAt ? 'fail' : 'todo'
    else if (i < Math.floor(cur) || cur === 3.5) state = 'done'
    else if (i === cur) state = 'cur'
    let label = name
    if (i === 1 && s === TxFrameStates.WaitingForChannel && it.waitingSince)
      label = `Waiting for channel ${durationShort(health.now - new Date(it.waitingSince).getTime())}`
    if (i === 3 && awaiting.value)
      label = it.kind === TxFrameKinds.Beacon ? 'Listening for digi' : 'Awaiting ACK'
    if (i === 3 && it.kind === TxFrameKinds.Beacon && it.outcome === 'unverified')
      label = 'Unverified'
    if (state === 'fail')
      label =
        failAt === 0
          ? s === TxFrameStates.Dropped
            ? 'Dropped'
            : 'Not sent'
          : failAt === 1
            ? 'Channel timeout'
            : failAt === 2
              ? 'TX fault'
              : props.gaveUp
                ? 'No ACK'
                : 'Not heard'
    return {
      key: String(i),
      label,
      tip: stepTip(i, state, failAt),
      state,
      warn: i === 1 && s === TxFrameStates.WaitingForChannel,
      live: i === 2 && s === TxFrameStates.OnAir,
    }
  })
})

const unverified = computed(
  () => props.item.kind === TxFrameKinds.Beacon && props.item.outcome === 'unverified',
)

const stateWord = computed(
  () => steps.value.find((x) => x.state === 'cur' || x.state === 'fail')?.label ?? doneLabel.value,
)
</script>

<template>
  <ol class="txl" :class="{ 'txl--compact': compact }" :aria-label="`Transmit state: ${stateWord}`">
    <li
      v-for="st in steps"
      :key="st.key"
      class="txl-step"
      :class="[
        `txl-${st.state}`,
        {
          'txl-warn': st.warn,
          'txl-live': st.live,
          'txl-unverified': unverified && st.key === '3',
        },
      ]"
    >
      <span class="txl-dot" aria-hidden="true" />
      <span class="txl-label">{{ st.label }}</span>
      <v-tooltip activator="parent" location="top" max-width="280" open-on-click>
        <div class="font-weight-bold">{{ st.label }}</div>
        <div>{{ st.tip }}</div>
      </v-tooltip>
    </li>
  </ol>
</template>

<style scoped>
.txl {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 2px 0;
  list-style: none;
  margin: 0;
  padding: 0;
  font-size: 0.75rem;
}
.txl-step {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  cursor: help;
  color: rgba(var(--v-theme-on-surface), var(--v-disabled-opacity));
}
.txl-step + .txl-step::before {
  content: '';
  display: inline-block;
  width: 14px;
  height: 2px;
  margin: 0 6px;
  background: rgba(var(--v-theme-on-surface), 0.18);
  border-radius: 1px;
}
.txl-step.txl-done + .txl-step::before {
  background: rgb(var(--v-theme-success));
}
.txl-dot {
  width: 9px;
  height: 9px;
  border-radius: 50%;
  border: 2px solid currentColor;
  flex: 0 0 auto;
}
.txl-done {
  color: rgb(var(--v-theme-success));
}
.txl-done .txl-dot {
  background: currentColor;
}
.txl-cur {
  color: rgb(var(--v-theme-primary));
  font-weight: 600;
}
.txl-cur .txl-dot {
  animation: txl-pulse 1.4s ease-in-out infinite;
}
.txl-warn {
  color: rgb(var(--v-theme-warning));
}
.txl-live {
  color: rgb(var(--v-theme-error));
}
.txl-live .txl-dot {
  background: currentColor;
}
.txl-fail {
  color: rgb(var(--v-theme-error));
  font-weight: 600;
}
.txl-fail .txl-dot {
  background: currentColor;
}
.txl-unverified {
  color: rgb(var(--v-theme-warning));
}
.txl--compact .txl-label {
  display: none;
}
.txl--compact .txl-cur .txl-label,
.txl--compact .txl-fail .txl-label,
.txl--compact .txl-step:last-child .txl-label {
  display: inline;
}
@keyframes txl-pulse {
  50% {
    opacity: 0.35;
  }
}
@media (prefers-reduced-motion: reduce) {
  .txl-cur .txl-dot {
    animation: none;
  }
}
</style>
