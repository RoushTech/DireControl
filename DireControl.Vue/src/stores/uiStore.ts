import { defineStore } from 'pinia'
import { ref } from 'vue'

export const useUiStore = defineStore('ui', () => {
  const pendingComposeOpen = ref(false)

  function triggerCompose() {
    pendingComposeOpen.value = true
  }

  function consumeCompose() {
    pendingComposeOpen.value = false
  }

  return { pendingComposeOpen, triggerCompose, consumeCompose }
})
