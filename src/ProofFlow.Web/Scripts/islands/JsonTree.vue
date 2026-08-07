<script setup lang="ts">
import { Icon } from '../lib/Icon';
import { computed, ref } from 'vue';
import { t } from '../lib/i18n';

/**
 * One node of a JSON document, rendered as a collapsible row.
 *
 * Recursive, and deliberately shallow-by-default below the second level: a response with four
 * hundred items expanded is a page nobody can scroll past, and the reader almost always wants the
 * shape first and one branch second.
 *
 * Every row carries its JSON path. That path is the thing the rest of the product is built on —
 * an ignore rule, an assertion, a variable extraction are all "this path, this matcher" — so the
 * click menu here is where a person first meets the idea, spelled the same way as everywhere else.
 */

const props = withDefaults(defineProps<{
  name?: string;
  value: unknown;
  path: string;
  depth?: number;
  isLast?: boolean;
}>(), { depth: 0, isLast: true });

const emit = defineEmits<{ pick: [path: string, value: unknown, event: MouseEvent] }>();

// Two levels open, everything below closed.
const open = ref(props.depth < 2);

const kind = computed(() => {
  if (props.value === null) return 'null';
  if (Array.isArray(props.value)) return 'array';
  return typeof props.value;
});

const isBranch = computed(() => kind.value === 'array' || kind.value === 'object');

const children = computed<{ key: string; value: unknown; path: string }[]>(() => {
  if (Array.isArray(props.value)) {
    return props.value.map((item, index) => ({
      key: String(index),
      value: item,
      path: `${props.path}[${index}]`,
    }));
  }

  if (kind.value === 'object') {
    return Object.entries(props.value as Record<string, unknown>).map(([key, value]) => ({
      key,
      value,
      // Bracket-quote any key that is not a plain identifier, so a path with a dot or a space in
      // the key is still a path that can be pasted back in and mean the same thing.
      path: /^[A-Za-z_$][\w$]*$/.test(key) ? `${props.path}.${key}` : `${props.path}['${key}']`,
    }));
  }

  return [];
});

/** "3 items" / "5 fields" — the shape, without opening it. */
const summary = computed(() => {
  const count = children.value.length;
  return kind.value === 'array' ? t('response.items', count) : t('response.fields', count);
});

const display = computed(() => {
  switch (kind.value) {
    case 'string': return `"${props.value as string}"`;
    case 'null': return 'null';
    default: return String(props.value);
  }
});
</script>

<template>
  <div class="json-row" :style="{ '--depth': depth }">
    <div class="json-line">
      <button
        v-if="isBranch && children.length > 0"
        type="button"
        class="json-toggle"
        :aria-expanded="open"
        :aria-label="open ? t('response.collapse') : t('response.expand')"
        @click="open = !open"
      >
        <Icon :name="open ? 'chevron-down' : 'chevron-right'" />
      </button>
      <span v-else class="json-toggle-spacer" aria-hidden="true"></span>

      <button
        type="button"
        class="json-field"
        :title="path"
        @click="(event) => emit('pick', path, value, event)"
      >
        <span v-if="name !== undefined" class="json-key">{{ name }}</span>
        <span v-if="name !== undefined" class="json-punct">:</span>

        <template v-if="isBranch">
          <span class="json-punct">{{ kind === 'array' ? '[' : '{' }}</span>
          <span v-if="!open || children.length === 0" class="json-summary">{{ summary }}</span>
          <span v-if="!open || children.length === 0" class="json-punct">
            {{ kind === 'array' ? ']' : '}' }}
          </span>
        </template>

        <span v-else :class="`json-${kind}`">{{ display }}</span>
        <span v-if="!isLast && !isBranch" class="json-punct">,</span>
      </button>
    </div>

    <template v-if="isBranch && open && children.length > 0">
      <JsonTree
        v-for="(child, index) in children"
        :key="child.path"
        :name="kind === 'array' ? undefined : child.key"
        :value="child.value"
        :path="child.path"
        :depth="depth + 1"
        :is-last="index === children.length - 1"
        @pick="(p, v, e) => emit('pick', p, v, e)"
      />
      <div class="json-line json-close" :style="{ '--depth': depth }">
        <span class="json-toggle-spacer" aria-hidden="true"></span>
        <span class="json-punct">{{ kind === 'array' ? ']' : '}' }}<template v-if="!isLast">,</template></span>
      </div>
    </template>
  </div>
</template>
