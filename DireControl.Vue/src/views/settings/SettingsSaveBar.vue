<script setup lang="ts">
/**
 * One save bar per settings section: names what the save
 * restarts, flags unsaved changes, and holds the section's Save button.
 */
withDefaults(
  defineProps<{
    /** What a save restarts; null → "No service restart needed". */
    restart?: string | null
    /** Section applies instantly in this browser — no save button. */
    instant?: boolean
    dirty?: boolean
    saving?: boolean
    /** Omit for sections that save elsewhere (dialogs). */
    saveLabel?: string
    disabled?: boolean
  }>(),
  { restart: null, instant: false, dirty: false, saving: false, saveLabel: '', disabled: false },
)

const emit = defineEmits<{ save: [] }>()
</script>

<template>
  <div class="settings-save-bar">
    <span v-if="instant" class="text-body-2 text-medium-emphasis">
      Changes apply immediately to this browser.
    </span>
    <span v-else-if="restart" class="text-body-2 d-inline-flex align-center restart-note">
      <v-icon size="16" class="mr-1 flex-shrink-0">mdi-restart</v-icon>
      {{ restart }}
    </span>
    <span v-else class="text-body-2 text-medium-emphasis">No service restart needed</span>
    <v-spacer />
    <span v-if="dirty && !instant" class="text-body-2 text-warning font-weight-medium">
      Unsaved changes
    </span>
    <v-btn
      v-if="saveLabel && !instant"
      color="primary"
      variant="flat"
      prepend-icon="mdi-content-save"
      :loading="saving"
      :disabled="disabled"
      @click="emit('save')"
    >
      {{ saveLabel }}
    </v-btn>
  </div>
</template>

<style scoped>
.settings-save-bar {
  position: sticky;
  bottom: 0;
  z-index: 2;
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px 12px;
  margin-top: 8px;
  padding: 12px 16px;
  border: 1px solid rgba(var(--v-border-color), var(--v-border-opacity));
  border-radius: 10px;
  background: rgb(var(--v-theme-surface));
}

.restart-note {
  min-width: 0;
}
</style>
