<script setup lang="ts">
import { Icon } from '../lib/Icon';
import { computed } from 'vue';
import { t } from '../lib/i18n';
import type { Suggestion } from './baselineTypes';

/**
 * Fields that look like they change on their own, offered as rules.
 *
 * Section 12 of the brief is explicit that nothing here is applied without confirmation, and the
 * reason is worth restating: every one of these rows proposes to stop checking something. A
 * detector that silently excluded a field would turn a passing test into a test that cannot fail,
 * and nobody would find out until the field that mattered went missing.
 *
 * So every box starts clear, the evidence is shown next to the proposal, and the effect is spelled
 * out in words rather than left implicit in a matcher name.
 */

const props = defineProps<{
  suggestions: Suggestion[];
  accepted: string[];
  readonly: boolean;
}>();

const emit = defineEmits<{
  'update:accepted': [paths: string[]];
  apply: [suggestions: Suggestion[]];
}>();

const acceptedSet = computed(() => new Set(props.accepted));
const chosen = computed(() => props.suggestions.filter((s) => acceptedSet.value.has(s.path)));

function toggle(path: string): void {
  const next = new Set(acceptedSet.value);
  if (next.has(path)) next.delete(path);
  else next.add(path);
  emit('update:accepted', [...next]);
}

function selectAll(): void {
  emit('update:accepted', props.suggestions.map((suggestion) => suggestion.path));
}

function clear(): void {
  emit('update:accepted', []);
}

/** Certain and Likely carry a tone; Possible stays neutral so the eye lands on the confident ones. */
function confidenceTone(confidence: string): string {
  if (confidence === 'Certain') return 'badge-pass';
  if (confidence === 'Likely') return 'badge-warn';
  return 'badge-idle';
}
</script>

<template>
  <section v-if="suggestions.length > 0" class="card suggestions">
    <h3 class="section-title">
      <Icon name="lightbulb" />
      {{ t('rule.suggestionsTitle', suggestions.length) }}
    </h3>
    <p class="section-help">{{ t('rule.suggestionsHelp') }}</p>

    <ul class="suggestion-list">
      <li v-for="suggestion in suggestions" :key="suggestion.path" class="suggestion">
        <label class="suggestion-label">
          <input
            class="checkbox"
            type="checkbox"
            :checked="acceptedSet.has(suggestion.path)"
            :disabled="readonly"
            @change="toggle(suggestion.path)"
          />
          <span class="suggestion-body">
            <code class="suggestion-path" dir="ltr">{{ suggestion.path }}</code>

            <span class="suggestion-reason">
              {{ t(`dynamic.${suggestion.reason}`) }}
              <span :class="['badge', confidenceTone(suggestion.confidence)]">
                {{ t(`confidence.${suggestion.confidence}`) }}
              </span>
            </span>

            <span v-if="suggestion.sample" class="suggestion-sample mono" dir="ltr">
              {{ suggestion.sample }}
            </span>

            <span class="suggestion-effect">
              {{ t('rule.suggestionEffect', t(`matcher.${suggestion.matcher}`)) }}
            </span>
          </span>
        </label>
      </li>
    </ul>

    <div v-if="!readonly" class="suggestion-foot">
      <button type="button" class="btn btn-ghost btn-sm" @click="selectAll">
        {{ t('rule.selectAll') }}
      </button>
      <button type="button" class="btn btn-ghost btn-sm" :disabled="accepted.length === 0" @click="clear">
        {{ t('rule.clearAll') }}
      </button>
      <span class="grow"></span>
      <span class="text-xs subtle">{{ t('rule.selectedCount', accepted.length, suggestions.length) }}</span>

      <!--
        Its own action, and it has to be. The common case is a comparison where every difference is
        a field that changes on its own: the right answer is three rules and no new version, and
        without this the only button on the page proposes a version instead.
      -->
      <button
        type="button"
        class="btn btn-primary btn-sm"
        :disabled="chosen.length === 0"
        @click="emit('apply', chosen)"
      >
        <Icon name="plus" />{{ t('rule.applySelected') }}
      </button>
    </div>
  </section>
</template>
