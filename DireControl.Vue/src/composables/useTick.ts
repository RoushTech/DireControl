import { ref, onMounted, onUnmounted, type Ref } from 'vue'

interface TickState {
  now: Ref<number>
  timer: ReturnType<typeof setInterval> | null
  subscribers: number
}

const ticks = new Map<number, TickState>()

export function useTick(intervalMs = 5000) {
  if (!ticks.has(intervalMs)) {
    ticks.set(intervalMs, { now: ref(Date.now()), timer: null, subscribers: 0 })
  }
  const state = ticks.get(intervalMs)!

  onMounted(() => {
    if (state.subscribers === 0) {
      state.timer = setInterval(() => { state.now.value = Date.now() }, intervalMs)
    }
    state.subscribers++
  })

  onUnmounted(() => {
    state.subscribers--
    if (state.subscribers === 0 && state.timer !== null) {
      clearInterval(state.timer)
      state.timer = null
    }
  })

  return { now: state.now }
}
