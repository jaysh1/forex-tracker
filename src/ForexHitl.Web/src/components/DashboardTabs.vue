<script setup lang="ts">
type Page = 'forex' | 'cfd' | 'learn'
defineProps<{ modelValue: Page }>()
const emit = defineEmits<{ 'update:modelValue': [page: Page] }>()
const pages: Page[] = ['forex', 'cfd', 'learn']

function onKeydown(event: KeyboardEvent) {
  if (event.key !== 'ArrowLeft' && event.key !== 'ArrowRight') return
  event.preventDefault()
  const current = pages.indexOf((event.currentTarget as HTMLButtonElement).id.replace('-tab', '') as Page)
  const direction = event.key === 'ArrowRight' ? 1 : -1
  const next = pages[(current + direction + pages.length) % pages.length]
  emit('update:modelValue', next)
  document.getElementById(`${next}-tab`)?.focus()
}
</script>

<template>
  <nav class="market-tabs" role="tablist" aria-label="Dashboard pages">
    <button v-for="page in pages" :id="`${page}-tab`" :key="page" class="market-tab"
      :class="{ selected: modelValue === page }" role="tab" :aria-selected="modelValue === page"
      :aria-controls="`${page}-panel`" :tabindex="modelValue === page ? 0 : -1"
      @keydown="onKeydown" @click="emit('update:modelValue', page)">
      {{ page === 'cfd' ? 'CFDs' : page === 'learn' ? 'Learn' : 'Forex' }}
    </button>
  </nav>
</template>
