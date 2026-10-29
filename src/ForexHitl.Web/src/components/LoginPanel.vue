<script setup lang="ts">
defineProps<{ busy: boolean; error: string | null }>()
const emit = defineEmits<{ submit: [password: string] }>()
import { ref } from 'vue'

const password = ref('')
function submit() {
  emit('submit', password.value)
  password.value = ''
}
</script>

<template>
  <main class="card login">
    <h1>Forex approver</h1>
    <form @submit.prevent="submit">
      <label for="pw">Password</label>
      <input id="pw" v-model="password" type="password" autocomplete="current-password" />
      <button type="submit" class="btn approve" :disabled="busy">
        {{ busy ? 'Signing in…' : 'Sign in' }}
      </button>
    </form>
    <p v-if="error" class="error" role="alert">{{ error }}</p>
  </main>
</template>
