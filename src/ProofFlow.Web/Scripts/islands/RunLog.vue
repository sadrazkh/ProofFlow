<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue';
import { Icon } from '../lib/Icon';
import { t } from '../lib/i18n';
import type { LogLevel, RunEventRow } from './runTypes';

/**
 * The live log.
 *
 * Two problems, and they pull against each other. A busy run produces tens of thousands of lines,
 * and a list that renders all of them stops being scrollable at about two thousand — so only the
 * visible window is in the DOM. And somebody watching wants the newest line without pressing
 * anything, but somebody reading line four hundred wants to stay on line four hundred — so it
 * follows the tail until the reader scrolls up, and then stops until they come back.
 *
 * The rows are a fixed height. Not a stylistic choice: variable heights mean measuring every row
 * to know where any row starts, which is the thing virtualising was meant to avoid.
 */

const props = defineProps<{
  lines: RunEventRow[];
  running: boolean;
}>();

/** Matches --run-log-line in run.css. Both change together or the window drifts. */
const ROW = 22;

/** Rows rendered beyond the window, so a fast scroll does not show a blank band. */
const OVERSCAN = 12;

const LEVELS: LogLevel[] = ['Debug', 'Info', 'Warning', 'Error'];

const viewport = ref<HTMLElement | null>(null);
const scrollTop = ref(0);
const height = ref(320);

/** Follows the newest line until the reader scrolls away from the bottom. */
const following = ref(true);

const minimum = ref<LevelIndex>(1);
const search = ref('');

/**
 * Shows one step's lines.
 *
 * Written into the search box rather than held as a separate filter, so the reader can see what
 * narrowed the list and clear it the same way they would have typed it.
 */
function focusOn(nodeName: string): void {
  search.value = nodeName;
  viewport.value?.focus();
}

defineExpose({ focusOn });

type LevelIndex = 0 | 1 | 2 | 3;

const shown = computed(() => {
  const needle = search.value.trim().toLowerCase();

  return props.lines.filter((line) => {
    if (LEVELS.indexOf(line.level) < minimum.value) return false;
    if (!needle) return true;

    return line.message.toLowerCase().includes(needle)
      || (line.nodeName ?? '').toLowerCase().includes(needle);
  });
});

const first = computed(() => Math.max(0, Math.floor(scrollTop.value / ROW) - OVERSCAN));

const last = computed(() =>
  Math.min(shown.value.length, first.value + Math.ceil(height.value / ROW) + OVERSCAN * 2));

const window_ = computed(() => shown.value.slice(first.value, last.value));

const padTop = computed(() => first.value * ROW);
const total = computed(() => shown.value.length * ROW);

watch(() => props.lines.length, async () => {
  if (!following.value) return;

  await nextTick();
  toBottom();
});

function onScroll(): void {
  const element = viewport.value;
  if (!element) return;

  scrollTop.value = element.scrollTop;
  height.value = element.clientHeight;

  // Within one row of the bottom counts as at the bottom: a scroll container's arithmetic is not
  // exact, and a reader who dragged the thumb to the end means to follow.
  following.value = element.scrollHeight - element.scrollTop - element.clientHeight < ROW * 1.5;
}

function toBottom(): void {
  const element = viewport.value;
  if (!element) return;

  element.scrollTop = element.scrollHeight;
  following.value = true;
}

function levelClass(level: LogLevel): string {
  return `log-${level.toLowerCase()}`;
}

/** Time only. The date is on the run, and repeating it on forty thousand rows says nothing. */
function clock(at: string): string {
  const date = new Date(at);
  return Number.isNaN(date.getTime())
    ? ''
    : date.toLocaleTimeString(document.documentElement.lang || 'en', {
        hour12: false, hour: '2-digit', minute: '2-digit', second: '2-digit',
      });
}
</script>

<template>
  <section class="run-log" aria-labelledby="run-log-heading">
    <header class="run-log-bar">
      <h3 id="run-log-heading" class="text-sm">{{ t('run.log') }}</h3>

      <select v-model.number="minimum" class="select" :aria-label="t('run.log.filter')">
        <option v-for="(level, index) in LEVELS" :key="level" :value="index">
          {{ t(`run.level.${level.toLowerCase()}`) }}
        </option>
      </select>

      <input
        v-model="search"
        type="search"
        class="input grow"
        :placeholder="t('run.log.search')"
        :aria-label="t('run.log.search')"
      />

      <span class="text-xs subtle tabular" dir="ltr">{{ shown.length }}</span>

      <div class="segmented">
        <button
          type="button"
          :aria-pressed="following"
          @click="following ? (following = false) : toBottom()"
        >
          <Icon name="arrow-down-to-line" :size="14" />{{ t('run.follow') }}
        </button>
      </div>
    </header>

    <!--
      role=log with aria-live=polite: a screen reader hears new lines without the page being
      re-announced. Off while the run is over, because a finished log is a document, not an event.
    -->
    <div
      ref="viewport"
      class="run-log-viewport"
      role="log"
      :aria-live="running ? 'polite' : 'off'"
      :aria-label="t('run.log')"
      tabindex="0"
      @scroll="onScroll"
    >
      <div class="run-log-spacer" :style="{ height: `${total}px` }">
        <ol class="run-log-lines" :style="{ transform: `translateY(${padTop}px)` }">
          <li
            v-for="line in window_"
            :key="line.sequence"
            class="run-log-line"
            :class="levelClass(line.level)"
          >
            <span class="run-log-time tabular" dir="ltr">{{ clock(line.at) }}</span>
            <span class="run-log-level" :aria-label="t(`run.level.${line.level.toLowerCase()}`)">
              {{ t(`run.level.${line.level.toLowerCase()}.short`) }}
            </span>
            <span v-if="line.nodeName" class="run-log-node" dir="auto">{{ line.nodeName }}</span>
            <span class="run-log-message" dir="auto">{{ line.message }}</span>
          </li>
        </ol>
      </div>

      <p v-if="!shown.length" class="run-log-empty text-sm subtle">
        {{ lines.length ? t('run.log.noMatch') : t('run.log.empty') }}
      </p>
    </div>
  </section>
</template>
