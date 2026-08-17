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
 *
 * Before any of that there is one more state: an endpoint with nothing recorded. The same button
 * sends it once and shows what came back, and the only decision on offer is whether that is
 * correct. Until this existed, a new endpoint met two disabled buttons and a sentence explaining
 * why — true, and a dead end.
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

/** The response itself, sent back only when there is nothing yet to compare it against. */
const answer = ref<{ body: string; contentType: string | null; statusCode: number } | null>(null);
const keeping = ref(false);

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
    const response = await api.post<{
      diff: DiffResult;
      suggestions: Suggestion[];
      body: string | null;
      contentType: string | null;
    }>(
      `/projects/${props.projectId}/endpoints/${props.baselineId}/compare`,
      { environmentId: environmentId.value || null },
    );

    diff.value = response.diff;
    suggestions.value = response.suggestions ?? [];
    // A suggestion ticked against the previous response means nothing against this one.
    acceptedSuggestions.value = [];

    answer.value = response.body === null || response.body === undefined
      ? null
      : {
          body: response.body,
          contentType: response.contentType,
          statusCode: response.diff.statusCode ?? 0,
        };

    if (answer.value === null && response.diff.matches) toast(t('baseline.identical'), 'success');
  } catch (error) {
    toast(error instanceof ApiError ? error.message : t('error.body'), 'error');
  } finally {
    pending.value = false;
  }
}

/**
 * Keeps what just came back as the first answer.
 *
 * The response the reader is looking at, taken from where the server held it — not a fresh call.
 * On anything with a clock or a counter in it those are two different bodies, and the one worth
 * recording is the one somebody agreed to.
 */
async function keep(): Promise<void> {
  if (keeping.value) return;
  keeping.value = true;

  try {
    await api.post(`/projects/${props.projectId}/endpoints/${props.baselineId}/record`);

    toast(t('baseline.captured'), 'success');
    // Reloaded rather than patched: the timeline, the status badge and this panel's whole reason
    // for being in this state are all server-rendered.
    location.reload();
  } catch (error) {
    toast(error instanceof ApiError ? error.message : t('error.body'), 'error');
    keeping.value = false;
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

        <button type="button" class="btn btn-primary" :disabled="!canRun || pending" @click="compare">
          <Icon name="play" />
          {{ pending
            ? (hasApprovedVersion ? t('baseline.comparing') : t('baseline.sending'))
            : (hasApprovedVersion ? t('baseline.compareNow') : t('baseline.sendOnce')) }}
        </button>
      </div>

      <p v-if="!hasApprovedVersion" class="response-notice">
        <Icon name="info" />{{ t('baseline.noAnswerYet') }}
      </p>
      <p v-else-if="!canRun" class="response-notice">
        <Icon name="lock" />{{ t('error.403body') }}
      </p>

      <!--
        Nothing recorded yet: what came back, and one decision about it. The diff viewer is not
        shown at all here — it would say «nothing to compare against», which is the sentence above
        it and not a finding.
      -->
      <section v-if="answer" class="first-answer">
        <header class="first-answer-head">
          <span class="status" :class="answer.statusCode < 400 ? 'status-pass' : 'status-fail'">
            <span class="status-dot" aria-hidden="true"></span>
            <span class="tabular">{{ answer.statusCode }}</span>
          </span>

          <div class="section-head grow">
            <h3 class="text-sm semibold">{{ t('baseline.firstAnswer') }}</h3>
            <p class="text-xs subtle">{{ t('baseline.firstAnswerHelp') }}</p>
          </div>

          <button
            v-if="canRecord"
            type="button"
            class="btn btn-primary"
            :disabled="keeping"
            @click="keep"
          >
            <Icon name="check" />
            {{ keeping ? t('common.saving') : t('baseline.saveAs') }}
          </button>
        </header>

        <pre class="first-answer-body" dir="ltr">{{ answer.body }}</pre>
      </section>

      <!--
        Only once there is something to compare against. Before that its empty state says «press
        Compare to see what moved», which is the wrong verb for a button labelled «send it once»
        and a second sentence about the same panel — and two sentences is how one of them goes
        stale.
      -->
      <DiffViewer
        v-if="hasApprovedVersion"
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
        v-if="hasApprovedVersion"
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
