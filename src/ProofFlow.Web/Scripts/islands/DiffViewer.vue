<script setup lang="ts">
import { Icon } from '../lib/Icon';
import { computed, onMounted, onUnmounted, ref, watch } from 'vue';
import { t } from '../lib/i18n';
import type { DiffResult, DiffRow } from './baselineTypes';

/**
 * What moved since the baseline was approved.
 *
 * Three things shape this component.
 *
 * It virtualises from the first line rather than as a later optimisation. A response with forty
 * thousand fields is not exotic — it is one page of search results — and building that many rows
 * locks the tab for seconds. Only the rows in view exist.
 *
 * Ignored fields stay visible, greyed. A diff that silently drops what a rule set aside is one
 * nobody can audit, and the reader's next question is always "what else did you not show me".
 *
 * And the summary is a control, not a caption. Six numbers that cannot be clicked are decoration;
 * these jump to the first of their kind, and n and p walk the findings the way a person reads.
 */

const props = defineProps<{
  result: DiffResult | null;
  pending: boolean;
  canAccept: boolean;

  /**
   * What the left-hand side is.
   *
   * The viewer draws two JSON documents differing, and that is useful in more than one place — a
   * response against an approved answer, and one environment against another. Only the words
   * change, so only the words are a prop: "identical to the baseline" is a false statement on a
   * page where no baseline is involved.
   */
  subject?: 'baseline' | 'other';
}>();
const emit = defineEmits<{ accept: [paths: string[]] }>();

/** Row height in pixels. Fixed, because a virtual list needs to know where row 30,000 is. */
const ROW = 30;
const OVERSCAN = 10;
const MAX_HEIGHT = 560;

const scroller = ref<HTMLElement | null>(null);
const scrollTop = ref(0);
const viewportHeight = ref(560);
const showUnchanged = ref(false);

/**
 * Side by side, when there is room for it.
 *
 * Two aligned columns are the better reading of a value that changed, and the worse reading of
 * everything else — an added field has nothing to put in the left column. So it is a choice, and
 * it is only offered where the columns are wide enough to hold a URL without wrapping.
 */
const split = ref(false);
const wide = ref(true);
const accepted = ref(new Set<string>());
const cursor = ref(-1);

/**
 * The rows actually on screen.
 *
 * Unchanged rows are hidden by default and the count says how many — showing every field of a
 * two-thousand-line response to point at three differences buries them, and hiding them without
 * saying so is the other half of the same mistake.
 */
const visible = computed<DiffRow[]>(() => {
  const rows = props.result?.rows ?? [];
  if (showUnchanged.value) return rows;

  return rows.filter((row) =>
    row.kind !== 'Unchanged' || row.hasFindings || row.depth === 0);
});

const hiddenCount = computed(() =>
  (props.result?.rows.length ?? 0) - visible.value.length);

const first = computed(() => Math.max(0, Math.floor(scrollTop.value / ROW) - OVERSCAN));
const last = computed(() =>
  Math.min(visible.value.length, Math.ceil((scrollTop.value + viewportHeight.value) / ROW) + OVERSCAN));

const rowWindow = computed(() => visible.value.slice(first.value, last.value));
const totalHeight = computed(() => visible.value.length * ROW);

/**
 * The scroller is as tall as it needs to be, up to a ceiling.
 *
 * A fixed height means a four-row diff sits in five hundred pixels of blank card, which reads as
 * "something failed to load". The ceiling is what makes it scroll rather than push the accept
 * controls off the bottom of a long response.
 */
const scrollerHeight = computed(() => Math.min(totalHeight.value, MAX_HEIGHT));

/** Findings as positions in the *visible* list, which is what the cursor moves through. */
const findingPositions = computed(() => {
  const positions: number[] = [];
  visible.value.forEach((row, index) => {
    if (row.kind !== 'Unchanged' && row.kind !== 'Ignored' && !row.hasChildren) positions.push(index);
  });
  return positions;
});

const counts = computed(() => {
  const raw = props.result?.counts ?? {};
  return ([
    ['Added', 'diff-added'], ['Removed', 'diff-removed'], ['Changed', 'diff-changed'],
    ['TypeChanged', 'diff-type'], ['OrderChanged', 'diff-order'], ['RuleViolation', 'diff-rule'],
    ['Ignored', 'diff-ignored'],
  ] as const)
    .map(([kind, tone]) => ({ kind, tone, count: raw[kind] ?? 0 }))
    .filter((entry) => entry.count > 0);
});

watch(() => props.result, () => {
  accepted.value = new Set();
  cursor.value = -1;
  scrollTop.value = 0;
  if (scroller.value) scroller.value.scrollTop = 0;
});

function onScroll(): void {
  if (!scroller.value) return;
  scrollTop.value = scroller.value.scrollTop;
  viewportHeight.value = scroller.value.clientHeight;
}

function goTo(position: number): void {
  if (position < 0 || position >= visible.value.length) return;
  cursor.value = position;
  // Centred rather than scrolled-to-top: a difference at the very edge of the viewport gives the
  // reader no context on either side of it.
  if (scroller.value) {
    scroller.value.scrollTop = Math.max(0, position * ROW - viewportHeight.value / 2);
  }
}

function step(direction: 1 | -1): void {
  const positions = findingPositions.value;
  if (positions.length === 0) return;

  const current = positions.indexOf(cursor.value);
  const next = current < 0
    ? (direction === 1 ? 0 : positions.length - 1)
    : (current + direction + positions.length) % positions.length;

  goTo(positions[next]!);
}

/** Jumps to the first difference of one kind — what makes the summary numbers worth clicking. */
function jumpToKind(kind: string): void {
  const position = visible.value.findIndex((row) => row.kind === kind);
  if (position >= 0) goTo(position);
}

function toggleAccept(row: DiffRow): void {
  const next = new Set(accepted.value);
  if (next.has(row.path)) next.delete(row.path);
  else next.add(row.path);
  accepted.value = next;
}

function acceptAll(): void {
  accepted.value = new Set(
    visible.value.filter((r) => isFinding(r)).map((r) => r.path));
}

function isFinding(row: DiffRow): boolean {
  return row.kind !== 'Unchanged' && row.kind !== 'Ignored' && !row.hasChildren;
}

function onKey(event: KeyboardEvent): void {
  // Only when nothing is being typed into: n is a letter before it is a shortcut.
  const target = event.target as HTMLElement | null;
  if (target && ['INPUT', 'TEXTAREA', 'SELECT'].includes(target.tagName)) return;
  if (event.metaKey || event.ctrlKey || event.altKey) return;

  if (event.key === 'n') { event.preventDefault(); step(1); }
  if (event.key === 'p') { event.preventDefault(); step(-1); }
}

/** Below this the two columns are narrower than the values they hold, so the choice is withdrawn. */
const roomForColumns = globalThis.matchMedia?.('(min-width: 900px)');

function onWidthChange(event: MediaQueryListEvent): void {
  wide.value = event.matches;
  if (!event.matches) split.value = false;
}

onMounted(() => {
  document.addEventListener('keydown', onKey);
  if (scroller.value) viewportHeight.value = scroller.value.clientHeight;

  if (roomForColumns) {
    wide.value = roomForColumns.matches;
    roomForColumns.addEventListener('change', onWidthChange);
  }
});

onUnmounted(() => {
  document.removeEventListener('keydown', onKey);
  roomForColumns?.removeEventListener('change', onWidthChange);
});

/**
 * What belongs in each column, or null when the row has nothing to put there.
 *
 * Split mode still renders the empty cell — an added field with a blank left column is the
 * clearest possible statement that it did not exist before.
 */
function expectedOf(row: DiffRow): string | null {
  return row.kind === 'Changed' || row.kind === 'TypeChanged'
      || row.kind === 'RuleViolation' || row.kind === 'Removed'
    ? short(row.expected)
    : null;
}

function actualOf(row: DiffRow): string | null {
  return row.kind === 'Changed' || row.kind === 'TypeChanged'
      || row.kind === 'RuleViolation' || row.kind === 'Added'
    ? short(row.actual)
    : null;
}

/**
 * The single character that carries the category alongside its colour.
 *
 * Around one man in twelve cannot separate the red from the green, so every row says what it is
 * twice: once in colour and once in a mark — and a third time in the label only a screen reader
 * hears.
 */
function marker(kind: string): string {
  switch (kind) {
    case 'Added': return '+';
    case 'Removed': return '−';
    case 'Changed': return '~';
    case 'TypeChanged': return '⌥';
    case 'OrderChanged': return '⇄';
    case 'RuleViolation': return '!';
    case 'Ignored': return '·';
    default: return ' ';
  }
}

function short(value: string | null | undefined): string {
  if (value == null) return '—';
  return value.length > 120 ? `${value.slice(0, 120)}…` : value;
}
</script>

<template>
  <section class="card diff" :class="{ 'is-split': split }">
    <div v-if="pending" class="response-pending">
      <div class="skeleton skeleton-title" style="inline-size: 40%;"></div>
      <div class="skeleton skeleton-text" v-for="n in 6" :key="n"></div>
    </div>

    <div v-else-if="!result" class="empty empty-inline">
      <div class="empty-art"><Icon name="git-compare-arrows" /></div>
      <p class="empty-body">{{ t('baseline.compareEmpty') }}</p>
    </div>

    <div v-else-if="result.failureMessage" class="empty empty-inline">
      <div class="empty-art response-failed"><Icon name="circle-alert" /></div>
      <h3 class="empty-title">{{ result.failureMessage }}</h3>
    </div>

    <template v-else>
      <!-- The summary is a control: each number jumps to the first of its kind. -->
      <div class="diff-summary">
        <span v-if="result.matches" class="badge badge-pass">
          <Icon name="circle-check" />{{ t(subject === 'other' ? 'diff.identical' : 'baseline.identical') }}
        </span>

        <button
          v-for="entry in counts"
          :key="entry.kind"
          type="button"
          class="diff-chip"
          :class="entry.tone"
          @click="jumpToKind(entry.kind)"
        >
          <span class="tabular">{{ entry.count }}</span>
          {{ t(`diff.kind.${entry.kind}`) }}
        </button>

        <span class="grow"></span>

        <span v-if="result.baselineVersion" class="text-xs subtle">
          {{ t(subject === 'other' ? 'diff.against' : 'baseline.against', result.baselineVersion) }}
        </span>

        <div v-if="wide" class="segmented" role="group" :aria-label="t('diff.layout')">
          <button type="button" :aria-pressed="!split" @click="split = false">{{ t('diff.inline') }}</button>
          <button type="button" :aria-pressed="split" @click="split = true">{{ t('diff.sideBySide') }}</button>
        </div>

        <div class="diff-nav" v-if="findingPositions.length">
          <button type="button" class="btn btn-ghost btn-icon btn-sm has-tip"
                  :data-tip="t('diff.previous')" :aria-label="t('diff.previous')" @click="step(-1)">
            <Icon name="chevron-up" />
          </button>
          <span class="text-xs subtle tabular" dir="ltr">
            {{ findingPositions.indexOf(cursor) + 1 || '–' }}/{{ findingPositions.length }}
          </span>
          <button type="button" class="btn btn-ghost btn-icon btn-sm has-tip"
                  :data-tip="t('diff.next')" :aria-label="t('diff.next')" @click="step(1)">
            <Icon name="chevron-down" />
          </button>
          <span class="kbd" aria-hidden="true">n</span>
          <span class="kbd" aria-hidden="true">p</span>
        </div>
      </div>

      <p v-if="result.invalidRules.length" class="response-notice">
        <Icon name="triangle-alert" />
        {{ t('diff.invalidRules', result.invalidRules.join(', ')) }}
      </p>

      <!--
        Fixed-height rows over a spacer, so row thirty thousand has a known position and only the
        ones on screen exist as elements.
      -->
      <div ref="scroller" class="diff-scroll" @scroll.passive="onScroll" tabindex="0"
           role="region" :aria-label="t('diff.title')"
           :style="{ blockSize: `${scrollerHeight}px` }">
        <div class="diff-spacer" :style="{ blockSize: `${totalHeight}px` }">
          <div class="diff-rows" :style="{ transform: `translateY(${first * ROW}px)` }">
            <div
              v-for="(row, offset) in rowWindow"
              :key="row.index"
              class="diff-row"
              :class="[`is-${row.kind.toLowerCase()}`, { 'is-cursor': first + offset === cursor }]"
              :style="{ '--depth': row.depth, blockSize: `${ROW}px` }"
            >
              <span class="diff-marker" aria-hidden="true">{{ marker(row.kind) }}</span>
              <span class="sr-only">{{ t(`diff.kind.${row.kind}`) }}</span>

              <span class="diff-path mono" :title="row.path">{{ row.leaf }}</span>

              <span v-if="split || expectedOf(row) !== null" class="diff-old mono">
                {{ expectedOf(row) ?? t('diff.absent') }}
              </span>
              <Icon
                v-if="!split && expectedOf(row) !== null && actualOf(row) !== null"
                name="arrow-right"
                class="diff-arrow icon-forward"
              />
              <span v-if="split || actualOf(row) !== null" class="diff-new mono">
                {{ actualOf(row) ?? t('diff.absent') }}
              </span>
              <span v-if="row.kind === 'Ignored'" class="diff-ignored-note">
                {{ row.reason || t('diff.kind.Ignored') }}
              </span>

              <span v-if="row.reason && row.kind !== 'Ignored'" class="diff-reason">{{ row.reason }}</span>

              <span class="grow"></span>

              <span v-if="row.ruleKind" class="badge badge-idle diff-rule-badge"
                    :title="row.rulePath ?? ''">{{ row.ruleKind }}</span>

              <!-- Field-level acceptance. Only on rows that are actually a finding. -->
              <label v-if="canAccept && isFinding(row)" class="diff-accept">
                <input
                  class="checkbox"
                  type="checkbox"
                  :checked="accepted.has(row.path)"
                  :aria-label="t('diff.acceptField', row.path)"
                  @change="toggleAccept(row)"
                />
              </label>
            </div>
          </div>
        </div>
      </div>

      <div class="diff-foot">
        <label class="check-row" style="padding: 0;">
          <input class="checkbox" type="checkbox" v-model="showUnchanged" />
          <span class="check-row-text">
            <span class="check-row-title text-xs">
              {{ t('diff.showUnchanged', hiddenCount) }}
            </span>
          </span>
        </label>

        <span class="grow"></span>

        <template v-if="canAccept && findingPositions.length">
          <button type="button" class="btn btn-ghost btn-sm" @click="acceptAll">
            {{ t('diff.acceptAll') }}
          </button>
          <span class="text-xs subtle">
            {{ t('diff.acceptedCount', accepted.size, findingPositions.length - accepted.size) }}
          </span>
          <button
            type="button"
            class="btn btn-primary btn-sm"
            :disabled="accepted.size === 0"
            @click="emit('accept', [...accepted])"
          >
            <Icon name="save" />{{ t('diff.saveAsVersion') }}
          </button>
        </template>
      </div>
    </template>
  </section>
</template>
