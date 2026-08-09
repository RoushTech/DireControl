import { defineStore } from 'pinia'
import { ref } from 'vue'

export interface Toast {
  id: number
  message: string
  color: string
  show: boolean
}

let toastSeq = 0

/**
 * Single app-wide toast stack, rendered by App.vue. All transient feedback
 * (alerts, action results, failures) goes through here so notifications look
 * and behave the same everywhere.
 */
export const useToastStore = defineStore('toasts', () => {
  const toasts = ref<Toast[]>([])

  function toast(message: string, color = 'info', timeoutMs = 6000) {
    const id = ++toastSeq
    toasts.value.push({ id, message, color, show: true })
    setTimeout(() => dismiss(id), timeoutMs)
  }

  function dismiss(id: number) {
    const t = toasts.value.find((t) => t.id === id)
    if (t) t.show = false
    // Clean up after the hide animation
    setTimeout(() => {
      const idx = toasts.value.findIndex((t) => t.id === id)
      if (idx !== -1) toasts.value.splice(idx, 1)
    }, 400)
  }

  return { toasts, toast, dismiss }
})
