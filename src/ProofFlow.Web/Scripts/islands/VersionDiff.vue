<script setup lang="ts">
import { Icon } from '../lib/Icon';
import { onMounted, onUnmounted, ref } from 'vue';
import DiffViewer from './DiffViewer.vue';
import { api, ApiError } from '../lib/api';
import { t } from '../lib/i18n';
import { toast } from '../lib/toast';
import type { DiffResult } from './baselineTypes';

/**
 * One waiting version's diff, opened where the row is.
 *
 * An island per row rather than one owning the whole table, and that is the decision worth
 * stating. The approval inbox is a server-rendered table whose Approve buttons are real forms and
 * whose checkboxes are real form controls; making Vue own it would mean rebuilding all of that in
 * a component and losing the page entirely when the bundle fails — on the one page whose job is to
 * tell somebody what is waiting on them. The diff is the only part that is genuinely reactive: it
 * is fetched when asked for and never before, because eight rows is eight comparisons nobody has
 * yet said they want to read. So the island is only the diff, and the table stays HTML.
 *
 * The panel is fetched once and kept. Closing and reopening a row is something a reader does while
 * comparing two of them, and it should not cost a round trip either time.
 */

const props = defineProps<{
  /** Where the comparison lives. The full path, so this component never rebuilds a route. */
  path: string;

  /** Identifies the row in the control's accessible name — "what changed" alone names nothing. */
  endpointName: string;
  number: number;

  /** The panel's id, which is what `aria-controls` on the button points at. */
  panelId: string;
}>();

const open = ref(false);
const pending = ref(false);
const loaded = ref(false);
const first = ref(false);
const diff = ref<DiffResult | null>(null);

/**
 * One row open at a time, coordinated by an event rather than by a parent.
 *
 * These are separate Vue applications with no store between them, and they do need to agree about
 * one thing: DiffViewer binds n and p on the document to walk its findings, so three open diffs
 * mean one keypress moving three cursors. An accordion is also simply the better reading of an
 * inbox — the list stays a list.
 */
const CHANNEL = 'proofflow:approval-diff';

function onSomebodyElseOpened(event: Event): void {
  if ((event as CustomEvent<string>).detail !== props.panelId) open.value = false;
}

onMounted(() => document.addEventListener(CHANNEL, onSomebodyElseOpened));
onUnmounted(() => document.removeEventListener(CHANNEL, onSomebodyElseOpened));

async function toggle(): Promise<void> {
  open.value = !open.value;
  if (!open.value) return;

  document.dispatchEvent(new CustomEvent(CHANNEL, { detail: props.panelId }));

  if (loaded.value || pending.value) return;

  pending.value = true;

  try {
    const answer = await api.get<{ first: boolean; diff: DiffResult | null }>(props.path);
    first.value = answer.first;
    diff.value = answer.diff;
    loaded.value = true;
  } catch (error) {
    // Left closed rather than open over an empty panel: an expander that opens onto nothing reads
    // as "there are no differences", which is the one thing this must never say by accident.
    open.value = false;
    toast(error instanceof ApiError ? error.message : t('error.body'), 'error');
  } finally {
    pending.value = false;
  }
}
</script>

<template>
  <div class="approval-detail">
    <button
      type="button"
      class="btn btn-ghost btn-sm approval-disclose"
      :aria-expanded="open"
      :aria-controls="panelId"
      @click="toggle"
    >
      <!-- icon-forward so the closed chevron points into the text on a Persian page rather than
           away from it. The open one is symmetrical about that flip, so one class covers both. -->
      <Icon :name="open ? 'chevron-down' : 'chevron-right'" :size="16" class="icon-forward" />
      {{ t('approval.whatChanged') }}
      <span class="sr-only">{{ t('approval.whatChanged.of', endpointName, number) }}</span>
    </button>

    <!--
      Always in the document so `aria-controls` resolves to something, hidden with v-show. The
      viewer itself is mounted only while open, because it binds document-level keys.
    -->
    <div :id="panelId" v-show="open" class="approval-detail-panel">
      <p v-if="first && loaded" class="approval-first">
        <Icon name="sparkles" :size="16" />
        {{ t('approval.first') }}
      </p>
      <DiffViewer v-else-if="open" :result="diff" :pending="pending" :can-accept="false" />
    </div>
  </div>
</template>
