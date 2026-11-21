<script setup lang="ts">
import { computed } from 'vue'
import { TxFrameKinds, TxFrameStates } from '@/api/txApi'
import type { TxItem } from '@/stores/tx'
import { durationShort } from '@/stores/health'
import { useHealthStore } from '@/stores/health'

/**
 * One frame's lifecycle as steps: Queued → Clear channel → On air → done
 * (Heard / Acked / Sent). Failures mark the step where it stopped with the
 * reason. "Queued" is never "sent" — only the last step means it left the
 * radio (review 1.2).
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
