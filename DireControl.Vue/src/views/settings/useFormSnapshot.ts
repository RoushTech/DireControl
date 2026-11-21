import { computed, ref } from 'vue'

/**
 * Dirty tracking for a settings form: `markClean()` records the current
 * values as saved, and `dirty` is true while they differ from that baseline.
 * Stays clean until the first `markClean()` (i.e. until the form has loaded).
 */
export function useFormSnapshot(values: () => unknown) {
  const baseline = ref<string | null>(null)
  const dirty = computed(
    () => baseline.value !== null && JSON.stringify(values()) !== baseline.value,
  )

  function markClean() {
    baseline.value = JSON.stringify(values())
  }

  return { dirty, markClean }
}
