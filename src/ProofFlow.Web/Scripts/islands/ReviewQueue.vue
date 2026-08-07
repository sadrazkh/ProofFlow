<script setup lang="ts">
import { Icon } from '../lib/Icon';
import { computed, onMounted, onUnmounted, ref, watch } from 'vue';
import DiffViewer from './DiffViewer.vue';
import { api, ApiError } from '../lib/api';
import { formatDuration, t } from '../lib/i18n';
import { toast } from '../lib/toast';
import type { DiffResult } from './baselineTypes';
import {
  SAMPLE_STATUSES, iconFor, toneFor,
  type CaptureSessionState, type SamplePage, type SampleRow, type SampleStatus,
} from './dataTypes';

/**
 * Two thousand answers, one decision at a time — or forty at once.
 *
 * The shape of this screen comes from what the work actually is. Somebody sits down with a sweep
 * that found sixty differences among two thousand samples and has to decide, for each one, whether
 * the API changed on purpose. That is a keyboard task: j and k to move, a and r to decide, and the
 * diff for whatever is under the cursor already on screen rather than a click away.
 *
 * Bulk selection exists because the sixty are usually the same difference sixty times. It is
 * deliberately explicit — a count in the bar and a named action — rather than a "select all"
 * that quietly includes the four rows nobody scrolled to.
 */

const props = defineProps<{
  projectId: string;
  sessionId: string;
  canReview: boolean;
}>();

const PAGE = 100;

const session = ref<CaptureSessionState | null>(null);
const rows = ref<SampleRow[]>([]);
const total = ref(0);
const loading = ref(false);

const filter = ref<SampleStatus | ''>('');
const differingOnly = ref(false);

const cursor = ref(-1);
const selected = ref(new Set<string>());
const diff = ref<DiffResult | null>(null);
const diffPending = ref(false);
const reviewing = ref(false);

const current = computed(() => rows.value[cursor.value] ?? null);
const shown = computed(() => SAMPLE_STATUSES.filter((entry) => (session.value?.counts[entry.status] ?? 0) > 0));

onMounted(() => {
  void load();
  document.addEventListener('keydown', onKey);
});

onUnmounted(() => document.removeEventListener('keydown', onKey));

watch([filter, differingOnly], () => {
  cursor.value = -1;
  selected.value = new Set();
  void load();
});

async function load(): Promise<void> {
  loading.value = true;

  const query = new URLSearchParams({ skip: '0', take: String(PAGE) });
  if (filter.value) query.set('status', filter.value);
  if (differingOnly.value) query.set('differing', 'true');

  try {
    const page = await api.get<SamplePage>(
      `/projects/${props.projectId}/captures/${props.sessionId}/samples?${query}`);

    session.value = page.session;
    rows.value = page.rows;
    total.value = page.total;

    // The first sample is opened rather than left to be clicked: the reader came here to look at
    // one, and an empty right-hand panel is a screen that has not started yet.
    if (rows.value.length > 0 && cursor.value < 0) select(0);
  } catch (error) {
    toast(error instanceof ApiError ? error.message : t('error.body'), 'error');
  } finally {
    loading.value = false;
  }
}

async function select(index: number): Promise<void> {
  if (index < 0 || index >= rows.value.length) return;

  cursor.value = index;
  const row = rows.value[index]!;

  diff.value = null;
  diffPending.value = true;

  try {
    diff.value = await api.get<DiffResult>(
      `/projects/${props.projectId}/captures/${props.sessionId}/samples/${row.id}/diff`);
  } catch (error) {
    toast(error instanceof ApiError ? error.message : t('error.body'), 'error');
  } finally {
    diffPending.value = false;
  }

  document.querySelector(`[data-sample="${row.id}"]`)
    ?.scrollIntoView({ block: 'nearest' });
}

function toggle(id: string): void {
  const next = new Set(selected.value);
  if (next.has(id)) next.delete(id);
  else next.add(id);
  selected.value = next;
}

function selectAllShown(): void {
  selected.value = new Set(rows.value.map((row) => row.id));
}

function clearSelection(): void {
  selected.value = new Set();
}

/**
 * Records a decision about the selection, or about the row under the cursor when nothing is
 * selected.
 *
 * That fallback is what makes the keyboard work: a and r have to mean "this one" while somebody is
 * walking the list, and "these forty" the moment they have ticked forty.
 */
async function decide(status: 'Approved' | 'Rejected' | 'Reviewed'): Promise<void> {
  if (!props.canReview || reviewing.value) return;

  const ids = selected.value.size > 0
    ? [...selected.value]
    : current.value ? [current.value.id] : [];

  if (ids.length === 0) return;

  reviewing.value = true;

  try {
    await api.post(`/projects/${props.projectId}/captures/${props.sessionId}/review`,
      { sampleIds: ids, status });

    toast(t(`capture.marked.${status}`, ids.length), 'success');

    const position = cursor.value;
    selected.value = new Set();
    await load();

    // Back to where they were, then one further on: deciding about a sample is finishing with it.
    if (position >= 0) await select(Math.min(position + 1, rows.value.length - 1));
  } catch (error) {
    toast(error instanceof ApiError ? error.message : t('error.body'), 'error');
  } finally {
    reviewing.value = false;
  }
}

function onKey(event: KeyboardEvent): void {
  const target = event.target as HTMLElement | null;
  if (target && ['INPUT', 'TEXTAREA', 'SELECT'].includes(target.tagName)) return;
  if (event.metaKey || event.ctrlKey || event.altKey) return;

  switch (event.key) {
    case 'j': event.preventDefault(); void select(cursor.value + 1); break;
    case 'k': event.preventDefault(); void select(cursor.value - 1); break;
    case 'a': event.preventDefault(); void decide('Approved'); break;
    case 'r': event.preventDefault(); void decide('Rejected'); break;
    case 'x':
      event.preventDefault();
      if (current.value) toggle(current.value.id);
      break;
  }
}

function summary(row: SampleRow): string {
  const parts = Object.entries(row.diffCounts)
    .filter(([kind]) => kind !== 'Unchanged' && kind !== 'Ignored')
    .map(([kind, count]) => `${count} ${t(`diff.kind.${kind}`)}`);

  return parts.join(' · ');
}
</script>

<template>
  <div class="review">
    <!-- The six states as a filter, with the counts that make them worth pressing. -->
    <div class="review-bar">
      <button
        type="button"
        class="diff-chip"
        :class="{ 'is-active': filter === '' }"
        @click="filter = ''"
      >
        <span class="tabular">{{ session?.totalRows ?? 0 }}</span>{{ t('capture.all') }}
      </button>

      <button
        v-for="entry in shown"
        :key="entry.status"
        type="button"
        class="diff-chip"
        :class="[entry.tone, { 'is-active': filter === entry.status }]"
        @click="filter = entry.status"
      >
        <Icon :name="entry.icon" />
        <span class="tabular">{{ session?.counts[entry.status] ?? 0 }}</span>
        {{ t(`capture.status.${entry.status}`) }}
      </button>

      <label class="check-row" style="padding: 0;">
        <input v-model="differingOnly" class="checkbox" type="checkbox" />
        <span class="check-row-text">
          <span class="check-row-title text-xs">{{ t('capture.differingOnly') }}</span>
        </span>
      </label>

      <span class="grow"></span>

      <span class="kbd" aria-hidden="true">j</span>
      <span class="kbd" aria-hidden="true">k</span>
      <span class="kbd" aria-hidden="true">a</span>
      <span class="kbd" aria-hidden="true">r</span>
      <span class="sr-only">{{ t('capture.keyboardHelp') }}</span>
    </div>

    <div class="review-layout">
      <section class="card review-list" :aria-label="t('capture.queue')">
        <div v-if="loading && rows.length === 0" class="response-pending">
          <div class="skeleton skeleton-text" v-for="n in 8" :key="n"></div>
        </div>

        <div v-else-if="rows.length === 0" class="empty empty-inline">
          <div class="empty-art"><Icon name="inbox" /></div>
          <p class="empty-body">{{ t('capture.queueEmpty') }}</p>
        </div>

        <ul v-else class="sample-list">
          <li
            v-for="(row, index) in rows"
            :key="row.id"
            :data-sample="row.id"
            class="sample"
            :class="{ 'is-cursor': index === cursor, 'is-selected': selected.has(row.id) }"
          >
            <input
              v-if="canReview"
              class="checkbox"
              type="checkbox"
              :checked="selected.has(row.id)"
              :aria-label="t('capture.selectSample', row.key)"
              @change="toggle(row.id)"
            />

            <button type="button" class="sample-open" @click="select(index)">
              <span class="sample-key mono" dir="ltr">{{ row.key }}</span>

              <span :class="['badge', toneFor(row.status)]">
                <Icon :name="iconFor(row.status)" />{{ t(`capture.status.${row.status}`) }}
              </span>

              <span v-if="row.differs" class="sample-summary">{{ summary(row) }}</span>
              <span v-else-if="row.failureMessage" class="sample-summary sample-failed">
                {{ row.failureMessage }}
              </span>
              <span v-else class="sample-summary subtle">{{ t('capture.identical') }}</span>

              <span class="grow"></span>
              <span class="text-xs subtle tabular">{{ row.statusCode }}</span>
              <span class="text-xs subtle tabular">{{ formatDuration(row.durationMs) }}</span>
            </button>
          </li>
        </ul>

        <div v-if="total > rows.length" class="response-notice">
          <Icon name="info" />{{ t('capture.showingFirst', rows.length, total) }}
        </div>
      </section>

      <div class="review-detail stack">
        <div v-if="canReview" class="review-actions">
          <span v-if="selected.size" class="badge badge-accent">
            {{ t('capture.selectedCount', selected.size) }}
          </span>
          <span v-else-if="current" class="text-xs subtle">
            {{ t('capture.currentSample') }}
            <code class="mono" dir="ltr">{{ current.key }}</code>
          </span>

          <span class="grow"></span>

          <template v-if="selected.size">
            <button type="button" class="btn btn-ghost btn-sm" @click="clearSelection">
              {{ t('rule.clearAll') }}
            </button>
          </template>
          <button v-else type="button" class="btn btn-ghost btn-sm" :disabled="!rows.length" @click="selectAllShown">
            {{ t('capture.selectShown', rows.length) }}
          </button>

          <button type="button" class="btn btn-secondary btn-sm" :disabled="reviewing" @click="decide('Rejected')">
            <Icon name="circle-slash" />{{ t('action.reject') }}
          </button>
          <button type="button" class="btn btn-primary btn-sm" :disabled="reviewing" @click="decide('Approved')">
            <Icon name="check" />{{ t('action.approve') }}
          </button>
        </div>

        <DiffViewer :result="diff" :pending="diffPending" :can-accept="false" />
      </div>
    </div>
  </div>
</template>
