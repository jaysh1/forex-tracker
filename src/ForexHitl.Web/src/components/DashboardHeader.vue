<script setup lang="ts">
defineProps<{ modeLabel?: string; modeClass?: string }>()
defineEmits<{ signOut: [] }>()
</script>

<template>
  <header class="top">
    <h1>Trading dashboard</h1>
    <span v-if="modeLabel" class="badge" :class="modeClass">{{ modeLabel }}</span>
    <button class="btn small" @click="$emit('signOut')">Sign out</button>
  </header>
</template>
