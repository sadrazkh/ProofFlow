<script setup lang="ts">
import { Icon } from '../lib/Icon';
import { computed, ref } from 'vue';
import DiffViewer from './DiffViewer.vue';
import RuleBuilder from './RuleBuilder.vue';
import SuggestionList from './SuggestionList.vue';
import { api, ApiError } from '../lib/api';
import { t } from '../lib/i18n';
import { toast } from '../lib/toast';
import type { BaselineEnvironment, DiffResult, Rule, Suggestion } from './baselineTypes';

/**
 * The review loop, on one screen.
 *
 * Replay, read what moved, decide field by field, and either write the decision down as a rule
 * ("this always changes") or as a new version ("this change was meant"). Those two answers are
 * different and the interface has to keep them apart — a rule silences a field forever, a version
 * blesses one value once, and people reach for whichever is nearer when they are tired.
 *
 * Neither happens without an explicit press. Comparing is read-only; nothing here writes until
 * somebody saves rules or proposes a version, and a proposed version still needs an approver.
 */

const props = defineProps<{
  projectId: string;
  baselineId: string;
  environments: BaselineEnvironment[];
  initialRules: Rule[];
  defaultEnvironmentId: string | null;
  canRecord: boolean;
  canRun: boolean;
  hasApprovedVersion: boolean;
}>();

const environmentId = ref(props.defaultEnvironmentId ?? props.environments[0]?.id ?? '');
const tab = ref<'compare' | 'rules'>('compare');

const diff = ref<DiffResult | null>(null);
const suggestions = ref<Suggestion[]>([]);
const pending = ref(false);

const rules = ref<Rule[]>(props.initialRules.map((rule) => ({ ...rule })));
const savedRules = ref(JSON.stringify(props.initialRules));
const acceptedSuggestions = ref<string[]>([]);
const saving = ref(false);
const proposing = ref(false);

const rulesDirty = computed(() => JSON.stringify(rules.value) !== savedRules.value);
const environment = computed(() => props.environments.find((e) => e.id === environmentId.value));

/** Findings only — what the accept count is a fraction of, and what the rule tab badges. */
const findingCount = computed(() => diff.value?.findingIndexes.length ?? 0);

async function compare(): Promise<void> {
  if (pending.value) return;
  pending.value = true;

  try {
    const response = await api.post<{ diff: DiffResult; suggestions: Suggestion[] }>(
      `/projects/${props.projectId}/endpoints/${props.baselineId}/compare`,
      { environmentId: environmentId.value || null },
    );

    diff.value = response.diff;
    suggestions.value = response.suggestions ?? [];
    // A suggestion ticked against the previous response means nothing against this one.
    acceptedSuggestions.value = [];

    if (response.diff.matches) toast(t('baseline.identical'), 'success');
  } catch (error) {
    toast(error instanceof ApiError ? error.message : t('error.body'), 'error');
  } finally {
    pending.value = false;
  }
}

async function saveRules(): Promise<void> {
  saving.value = true;

  try {
    await api.post(`/projects/${props.projectId}/endpoints/${props.baselineId}/rules`,
      rules.value.filter((rule) => rule.path.trim().length > 0));

    savedRules.value = JSON.stringify(rules.value);
    toast(t('rule.saved'), 'success');

    // The rules that just changed are the rules the diff was computed under, so what is on screen
    // is now describing a comparison that would no longer come out the same way.
    if (diff.value) {
      diff.value = null;
      suggestions.value = [];
      toast(t('rule.compareAgain'), 'info');
    }
  } catch (error) {
    toast(error instanceof ApiError ? error.message : t('error.body'), 'error');
  } finally {
    saving.value = false;
  }
}

/**
 * Turns ticked suggestions into saved rules, and nothing else.
 *
 * Separate from proposing a version because the two answers are different. "This field is a
 * generated id" is a statement about the endpoint that holds for every future run; "this value is
 * the new correct one" is a statement about one response. The commonest comparison is entirely
 * the first kind, and folding it into the second would bless a timestamp as a baseline value.
 */
async function applySuggestions(chosen: Suggestion[]): Promise<void> {
  rules.value = [
    ...rules.value,
    ...chosen.map((suggestion): Rule => ({
      id: null,
      path: suggestion.path,
      matcher: suggestion.matcher,
      text: null,
      number: null,
      number2: null,
      note: suggestion.note,
      enabled: true,
    })),
  ];

  await saveRules();
  acceptedSuggestions.value = [];
}

/**
 * Turns the reviewer's decisions into a proposed version.
 *
 * The ticked suggestions ride along, because "this field is a timestamp" and "these three changes
 * were intended" are usually decided in the same sitting, and making them two saves means the
 * second one gets forgotten.
 */
async function propose(paths: string[]): Promise<void> {
  proposing.value = true;

  const newRules = suggestions.value
    .filter((suggestion) => acceptedSuggestions.value.includes(suggestion.path))
    .map((suggestion): Rule => ({
      id: null,
      path: suggestion.path,
      matcher: suggestion.matcher,
      text: null,
      number: null,
      number2: null,
      note: suggestion.note,
      enabled: true,
    }));

  try {
    const result = await api.post<{ number: number }>(
      `/projects/${props.projectId}/endpoints/${props.baselineId}/accept`,
      { acceptedPaths: paths, newRules, description: null },
    );

    toast(t('baseline.versionProposed', result.number), 'success');
    // Reloaded rather than patched: the timeline, the status badge and the approval panel are all
    // server-rendered, and three separate client-side updates is three chances to disagree.
    location.reload();
  } catch (error) {
    toast(error instanceof ApiError ? error.message : t('error.body'), 'error');
    proposing.value = false;
  }
}
</script>

<template>
  <div class="workbench">
    <!--
      A group of toggles rather than a tablist, matching the response viewer's Tree/Raw control.
      A real tablist owes the reader roving tabindex and arrow keys; claiming the role without
      them is worse for a screen reader than not claiming it.
    -->
    <div class="segmented" role="group" :aria-label="t('baseline.workbench')">
      <button type="button" :aria-pressed="tab === 'compare'" @click="tab = 'compare'">
        {{ t('baseline.compareTab') }}
        <span v-if="findingCount" class="segment-count tabular">{{ findingCount }}</span>
      </button>
      <button type="button" :aria-pressed="tab === 'rules'" @click="tab = 'rules'">
        {{ t('baseline.rulesTab') }}
        <span v-if="rules.length" class="segment-count tabular">{{ rules.length }}</span>
        <span v-if="rulesDirty" class="status-warn" :title="t('common.unsaved')">
          <span class="status-dot"></span>
          <span class="sr-only">{{ t('common.unsaved') }}</span>
        </span>
      </button>
    </div>

    <section v-show="tab === 'compare'" class="workbench-panel">
      <div class="workbench-bar">
        <label class="field field-inline">
          <span class="field-label">{{ t('environment.title') }}</span>
          <select v-model="environmentId" class="select" :disabled="pending">
            <option v-for="env in environments" :key="env.id" :value="env.id">{{ env.name }}</option>
          </select>
        </label>

        <span
          v-if="environment?.isProduction"
          class="badge badge-warn has-tip"
          :data-tip="t('environment.productionWarning')"
        >
          <Icon name="shield-alert" />{{ t('environment.production') }}
        </span>

        <span class="grow"></span>

        <button
          type="button"
          class="btn btn-primary"
          :disabled="!canRun || pending || !hasApprovedVersion"
          @click="compare"
        >
          <Icon name="play" />
          {{ pending ? t('baseline.comparing') : t('baseline.compareNow') }}
        </button>
      </div>

      <p v-if="!hasApprovedVersion" class="response-notice">
        <Icon name="info" />{{ t('baseline.needsApproved') }}
      </p>
      <p v-else-if="!canRun" class="response-notice">
        <Icon name="lock" />{{ t('error.403body') }}
      </p>

      <DiffViewer
        :result="diff"
        :pending="pending"
        :can-accept="canRecord && !proposing"
        @accept="propose"
      />

      <!--
        Suggestions live under the diff and not on the rules tab: they are read off the response
        that was just compared, and asking somebody to change tabs to see them is asking them not
        to see them.
      -->
      <SuggestionList
        v-model:accepted="acceptedSuggestions"
        :suggestions="suggestions"
        :readonly="!canRecord"
        @apply="applySuggestions"
      />
    </section>

    <section v-show="tab === 'rules'" class="workbench-panel">
      <p class="section-help">{{ t('rule.help') }}</p>

      <RuleBuilder v-model="rules" :readonly="!canRecord" />

      <div class="workbench-bar workbench-foot">
        <span v-if="rulesDirty" class="text-xs subtle">
          <Icon name="circle-dot" />{{ t('common.unsaved') }}
        </span>
        <span class="grow"></span>
        <button
          type="button"
          class="btn btn-primary"
          :disabled="!canRecord || !rulesDirty || saving"
          @click="saveRules"
        >
          <Icon name="save" />
          {{ saving ? t('common.saving') : t('rule.save') }}
        </button>
      </div>
    </section>
  </div>
</template>
