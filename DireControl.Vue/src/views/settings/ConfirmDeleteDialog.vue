<script setup lang="ts">
import { ref, watch } from 'vue'
import { apiErrorDetail } from '@/api/axios'

/**
 * Delete confirmation that runs the delete itself and stays open with the
 * server's error when it fails, rather than closing on a silent failure.
 */
const open = defineModel<boolean>({ default: false })
const props = defineProps<{ message: string; action: (() => Promise<void>) | null }>()

const error = ref('')
const busy = ref(false)

async function confirm() {
  error.value = ''
  busy.value = true
  try {
    if (props.action) await props.action()
    open.value = false
  } catch (e) {
    error.value = apiErrorDetail(e)
  } finally {
    busy.value = false
  }
}

watch(open, (o) => {
  if (!o) error.value = ''
})
</script>

<template>
  <v-dialog v-model="open" max-width="400">
    <v-card>
      <v-card-title>
        <v-icon color="error" class="mr-2">mdi-alert-circle-outline</v-icon>
        Confirm Delete
      </v-card-title>
      <v-card-text>
        {{ message }}
        <v-alert v-if="error" type="error" variant="tonal" density="compact" class="mt-3">
          Delete failed: {{ error }}
        </v-alert>
      </v-card-text>
      <v-card-actions>
        <v-spacer />
        <v-btn variant="text" @click="open = false">Cancel</v-btn>
        <v-btn color="error" variant="tonal" :loading="busy" @click="confirm">Delete</v-btn>
      </v-card-actions>
    </v-card>
  </v-dialog>
</template>
