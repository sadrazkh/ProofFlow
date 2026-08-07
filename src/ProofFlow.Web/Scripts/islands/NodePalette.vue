<script setup lang="ts">
import { Icon } from '../lib/Icon';
import { computed, ref } from 'vue';
import { t } from '../lib/i18n';
import { GROUPS, type NodeSpecDto } from './graphTypes';

/**
 * Seventy node types, findable.
 *
 * A flat list of seventy is a wall, so they arrive in the five groups the brief names, collapsed
 * to the one somebody is working in. The search is the real answer though: somebody who knows they
 * want to check a status code types "status", and the groups are for somebody who does not yet
 * know what they want.
 *
 * Searching matches the translated name and the key, so both "assert" and "بررسی" find the same
 * node — the key is what appears in exported files and in support conversations.
 */

const props = defineProps<{ specs: NodeSpecDto[]; canEdit: boolean }>();
const emit = defineEmits<{ add: [key: string] }>();

const query = ref('');
const open = ref<Record<string, boolean>>({ Core: true, Data: false, Testing: false, Flow: false, Auth: false });

const matches = computed(() => {
  const needle = query.value.trim().toLowerCase();
  if (!needle) return props.specs;

  return props.specs.filter((spec) =>
    spec.key.toLowerCase().includes(needle)
    || t(`node.${spec.key}.title`).toLowerCase().includes(needle)
    || t(`node.${spec.key}.summary`).toLowerCase().includes(needle));
});

const grouped = computed(() =>
  GROUPS.map((group) => ({
    group,
    specs: matches.value.filter((spec) => spec.group === group),
  })).filter((entry) => entry.specs.length > 0));

/** Searching opens everything: a closed group holding the only match is a search that found nothing. */
const searching = computed(() => query.value.trim().length > 0);

function toggle(group: string): void {
  open.value = { ...open.value, [group]: !open.value[group] };
}

function onDragStart(event: DragEvent, key: string): void {
  event.dataTransfer?.setData('application/proofflow-node', key);
  if (event.dataTransfer) event.dataTransfer.effectAllowed = 'copy';
}
</script>

<template>
  <aside class="node-palette" :aria-label="t('canvas.palette')">
    <div class="node-palette-search">
      <Icon name="search" :size="14" />
      <input
        v-model="query"
        class="input"
        type="search"
        :placeholder="t('canvas.searchNodes')"
        :aria-label="t('canvas.searchNodes')"
      />
    </div>

    <div v-if="grouped.length === 0" class="empty empty-inline">
      <p class="empty-body">{{ t('canvas.noNodesFound', query) }}</p>
    </div>

    <div v-for="entry in grouped" :key="entry.group" class="node-palette-group">
      <button
        type="button"
        class="node-palette-group-head"
        :aria-expanded="searching || open[entry.group]"
        @click="toggle(entry.group)"
      >
        <Icon :name="searching || open[entry.group] ? 'chevron-down' : 'chevron-right'" :size="14" />
        {{ t(`nodeGroup.${entry.group}`) }}
        <span class="grow"></span>
        <span class="text-xs subtle tabular">{{ entry.specs.length }}</span>
      </button>

      <ul v-show="searching || open[entry.group]" class="node-palette-list">
        <li v-for="spec in entry.specs" :key="spec.key">
          <!--
            A button as well as a draggable. Dragging is the pleasant way and the keyboard way has
            to exist: a palette that can only be used with a mouse is a canvas nobody can build
            with a keyboard.
          -->
          <button
            type="button"
            class="node-palette-item"
            :draggable="canEdit"
            :disabled="!canEdit"
            @dragstart="onDragStart($event, spec.key)"
            @click="emit('add', spec.key)"
          >
            <span class="node-palette-icon" :class="`is-${spec.group.toLowerCase()}`">
              <Icon :name="spec.icon" :size="14" />
            </span>
            <span class="node-palette-text">
              <span class="node-palette-title">{{ t(`node.${spec.key}.title`) }}</span>
              <span class="node-palette-summary">{{ t(`node.${spec.key}.summary`) }}</span>
            </span>
            <span v-if="spec.reaches" class="has-tip" :data-tip="t('canvas.reachesOut')">
              <Icon name="globe" :size="12" />
            </span>
          </button>
        </li>
      </ul>
    </div>
  </aside>
</template>
