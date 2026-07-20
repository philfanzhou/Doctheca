<script setup lang="ts">
import { Icon, type IconName } from '../utils/icons'

defineProps<{
  icon?: IconName
  label: string
  value: number | string
  suffix?: string
  foot?: string
  trendUp?: string
  trendDown?: string
}>()
</script>

<template>
  <div class="card hoverable stat-card">
    <div class="stat-label">
      <Icon v-if="$props.icon" :name="$props.icon" />
      <span>{{ label }}</span>
    </div>
    <div
      class="stat-num"
      :data-count="typeof value === 'number' ? value : 0"
      :data-suffix="suffix || ''"
    >
      0
    </div>
    <div class="stat-foot" v-if="foot || trendUp || trendDown">
      <span v-if="trendUp" class="trend-up">{{ trendUp }}</span>
      <span v-if="trendDown" class="trend-down">{{ trendDown }}</span>
      <span v-if="foot">{{ foot }}</span>
    </div>
  </div>
</template>
