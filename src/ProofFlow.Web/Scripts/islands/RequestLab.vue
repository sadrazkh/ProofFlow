<script setup lang="ts">
import { Icon } from '../lib/Icon';
import { computed, onMounted, ref, watch } from 'vue';
import KeyValueTable, { type KeyValueRow } from './KeyValueTable.vue';
import ResponseViewer from './ResponseViewer.vue';
import { api, ApiError } from '../lib/api';
import { t } from '../lib/i18n';
import { toast } from '../lib/toast';
import {
  METHODS, METHODS_WITH_BODY,
  type LabEnvironment, type SendRequestResult, type VariableNames,
} from './requestTypes';

/**
 * Build one request, send it, read what came back.
 *
 * The smallest complete version of what this product does, and the first thing built on the
 * engine — the capture wizard and the HTTP node on the canvas are this same request definition
 * with something else driving it.
 */

const props = defineProps<{
  projectId: string;
  environments: LabEnvironment[];
  canRun: boolean;
  canRecordBaseline: boolean;
}>();

const method = ref('GET');
const url = ref('');
const environmentId = ref<string>(props.environments[0]?.id ?? '');
const tab = ref<'query' | 'headers' | 'body' | 'auth'>('query');
const query = ref<KeyValueRow[]>([{ name: '', value: '', enabled: true }]);
const headers = ref<KeyValueRow[]>([{ name: '', value: '', enabled: true }]);
const bodyKind = ref('Json');
const body = ref('');

const known = ref<VariableNames>({ environment: [], variables: [], secrets: [] });
const result = ref<SendRequestResult | null>(null);
const pending = ref(false);

const environment = computed(() => props.environments.find((e) => e.id === environmentId.value));
const supportsBody = computed(() => METHODS_WITH_BODY.has(method.value));
const methodTone = computed(() => METHODS.find((m) => m.name === method.value)?.tone ?? 'idle');

/**
 * Every reference in the request, marked resolvable or not.
 *
 * Checked in the browser against the *names* the server published, never by asking the server per
 * keystroke. Names are safe to hold here; values are not, and a secret's value never arrives.
 */
const references = computed(() => {
  const text = [url.value, body.value,
    ...query.value.flatMap((r) => [r.name, r.value]),
    ...headers.value.flatMap((r) => [r.name, r.value])].join('\n');

  const found = new Map<string, boolean>();

  for (const match of text.matchAll(/\{\{\s*([^{}]+?)\s*\}\}/g)) {
    const inside = match[1]!;
    const [scope, ...rest] = inside.split(/[.[]/);
    const name = rest[0]?.replace(/['"\]]/g, '') ?? '';

    let ok = false;
    switch (scope) {
      case 'environment': ok = known.value.environment.includes(name); break;
      case 'vars': ok = known.value.variables.includes(name); break;
      case 'secrets': ok = known.value.secrets.includes(name); break;
      // Only a run has steps, a dataset or a run id. Inside the lab they cannot resolve, and
      // saying so is more useful than leaving them unmarked.
      case 'steps': case 'dataset': case 'run': ok = false; break;
      default: ok = false;
    }

    found.set(match[0], ok);
  }

  return [...found].map(([reference, resolvable]) => ({ reference, resolvable }));
});

const unresolvedCount = computed(() => references.value.filter((r) => !r.resolvable).length);

const STORAGE_KEY = computed(() => `proofflow-request-${props.projectId}`);

onMounted(() => {
  restore();
  void loadNames();
});

watch(environmentId, () => void loadNames());
watch([method, url, environmentId, query, headers, body, bodyKind], remember, { deep: true });

async function loadNames(): Promise<void> {
  try {
    known.value = await api.get<VariableNames>(
      `/projects/${props.projectId}/request/variables?environmentId=${environmentId.value}`);
  } catch {
    // Only the live marking degrades: the request still sends, and the server resolves for real.
    known.value = { environment: [], variables: [], secrets: [] };
  }
}

/**
 * Keeps the last request per project in the browser.
 *
 * Not on the server: a scratch request belongs to the person typing it, and saving every
 * experiment would fill the project with things nobody meant to keep. But losing it to a refresh
 * would be its own small cruelty.
 */
function remember(): void {
  try {
    localStorage.setItem(STORAGE_KEY.value, JSON.stringify({
      method: method.value, url: url.value, environmentId: environmentId.value,
      query: query.value, headers: headers.value, bodyKind: bodyKind.value, body: body.value,
    }));
  } catch {
    // A full or disabled storage is not worth interrupting anyone over.
  }
}

function restore(): void {
  try {
    const saved = localStorage.getItem(STORAGE_KEY.value);
    if (!saved) return;

    const state = JSON.parse(saved);
    method.value = state.method ?? 'GET';
    url.value = state.url ?? '';
    if (props.environments.some((e) => e.id === state.environmentId)) environmentId.value = state.environmentId;
    query.value = state.query?.length ? state.query : query.value;
    headers.value = state.headers?.length ? state.headers : headers.value;
    bodyKind.value = state.bodyKind ?? 'Json';
    body.value = state.body ?? '';
  } catch {
    // A malformed blob from an older shape is discarded rather than allowed to break the page.
  }
}

async function send(): Promise<void> {
  if (pending.value) return;

  // Production is a different act from staging, and the moment to say so is before the press.
  if (environment.value?.isProduction
      && !window.confirm(t('request.productionConfirm', environment.value.name))) {
    return;
  }

  pending.value = true;
  result.value = null;

  try {
    result.value = await api.post<SendRequestResult>(`/projects/${props.projectId}/request/send`, {
      environmentId: environmentId.value || null,
      method: method.value,
      url: url.value,
      query: query.value.filter((r) => r.name),
      headers: headers.value.filter((r) => r.name),
      bodyKind: supportsBody.value ? bodyKind.value : null,
      body: supportsBody.value ? body.value : null,
    });
  } catch (error) {
    toast(error instanceof ApiError ? error.message : t('error.body'), 'error');
  } finally {
    pending.value = false;
  }
}

/**
 * Recording this response as a baseline.
 *
 * The request definition travels with it, not just the body — a baseline that remembers what
 * correct looked like but not how to ask for it again can never be re-checked, which is the only
 * thing a baseline is for.
 */
const capture = ref<{ name: string; description: string } | null>(null);
const capturing = ref(false);

function openCapture(): void {
  // Named after the URL's last segment, which is nearly always the resource: /api/v1/studies →
  // "studies". A name people then edit is better than an empty box they have to invent one in.
  const guess = url.value.split('?')[0]?.split('/').filter(Boolean).pop() ?? '';
  capture.value = { name: guess, description: '' };
}

async function saveBaseline(): Promise<void> {
  if (!capture.value || !result.value) return;
  capturing.value = true;

  try {
    const created = await api.post<{ url: string }>(
      `/projects/${props.projectId}/baselines/capture`,
      {
        name: capture.value.name,
        description: capture.value.description || null,
        environmentId: environmentId.value || null,
        body: result.value.body,
        contentType: result.value.contentType,
        statusCode: result.value.statusCode,
        headers: Object.fromEntries(result.value.responseHeaders.map((h) => [h.name, h.value])),
        // The request as sent, so the baseline can be replayed. The *unresolved* form: a baseline
        // that froze today's resolved token would stop working the moment the token rotated.
        requestJson: JSON.stringify({
          method: method.value,
          url: url.value,
          headers: headers.value.filter((r) => r.name && r.enabled)
            .map((r) => ({ name: r.name, value: r.value, enabled: true })),
          body: supportsBody.value && body.value
            ? { kind: bodyKind.value, content: body.value }
            : null,
        }),
      });

    toast(t('baseline.captured'), 'success');
    location.assign(created.url);
  } catch (error) {
    toast(error instanceof ApiError ? error.message : t('error.body'), 'error');
    capturing.value = false;
  }
}

/** Turns a picked response field into a header, which is what people do with a token. */
function useValue(path: string, value: unknown): void {
  const text = typeof value === 'string' ? value : JSON.stringify(value);
  headers.value.splice(headers.value.length - 1, 0, {
    name: 'X-From-Response', value: text, enabled: true,
  });
  tab.value = 'headers';
  toast(t('response.addedAsHeader', path), 'success');
}
</script>

<template>
  <div class="request-lab stack">
    <div class="card">
      <div class="request-line">
        <select v-model="method" class="select method-chip" :data-tone="methodTone" :aria-label="t('request.method')">
          <option v-for="m in METHODS" :key="m.name" :value="m.name">{{ m.name }}</option>
        </select>

        <div class="grow" style="min-inline-size: 0;">
          <input
            v-model="url"
            class="input input-mono"
            :placeholder="environment?.baseUrl ? '/fake/categories' : 'https://api.example.com/orders'"
            :aria-label="t('request.url')"
            @keydown.enter="send"
          />
        </div>

        <select v-model="environmentId" class="select" :aria-label="t('nav.environments')" style="max-inline-size: 200px;">
          <option value="">{{ t('request.noEnvironment') }}</option>
          <option v-for="e in environments" :key="e.id" :value="e.id">
            {{ e.name }}{{ e.isProduction ? ' ⚠' : '' }}
          </option>
        </select>

        <button type="button" class="btn btn-primary" :disabled="pending || !canRun || !url" @click="send">
          <Icon :name="pending ? 'loader' : 'send'" />
          {{ pending ? t('request.sending') : t('request.send') }}
        </button>
      </div>

      <!--
        The references, checked as they are typed. Shown as chips under the line rather than
        painted onto the input: an overlay that has to track a text field's scroll position and
        font metrics is fragile in one direction and worse in two, and this application is
        right-to-left half the time. Chips also have room to say *why* one does not resolve.
      -->
      <div v-if="references.length" class="reference-strip">
        <span
          v-for="reference in references"
          :key="reference.reference"
          class="reference-chip mono"
          :class="reference.resolvable ? 'is-known' : 'is-unknown'"
        >
          <Icon :name="reference.resolvable ? 'check' : 'circle-alert'" />
          {{ reference.reference }}
        </span>
        <span v-if="unresolvedCount" class="text-xs subtle">{{ t('request.unresolvedHint') }}</span>
      </div>

      <div class="tabs" role="tablist">
        <button type="button" class="tab" role="tab" :class="{ 'is-active': tab === 'query' }"
                :aria-selected="tab === 'query'" @click="tab = 'query'">
          {{ t('request.query') }}
          <span v-if="query.filter((r) => r.name).length" class="tab-count tabular">
            {{ query.filter((r) => r.name).length }}
          </span>
        </button>
        <button type="button" class="tab" role="tab" :class="{ 'is-active': tab === 'headers' }"
                :aria-selected="tab === 'headers'" @click="tab = 'headers'">
          {{ t('request.headers') }}
          <span v-if="headers.filter((r) => r.name).length" class="tab-count tabular">
            {{ headers.filter((r) => r.name).length }}
          </span>
        </button>
        <button v-if="supportsBody" type="button" class="tab" role="tab" :class="{ 'is-active': tab === 'body' }"
                :aria-selected="tab === 'body'" @click="tab = 'body'">
          {{ t('request.body') }}
        </button>
      </div>

      <div class="card-body">
        <KeyValueTable
          v-if="tab === 'query'"
          v-model="query"
          :label="t('request.query')"
          name-placeholder="page"
          value-placeholder="1"
        />

        <KeyValueTable
          v-else-if="tab === 'headers'"
          v-model="headers"
          :label="t('request.headers')"
          name-placeholder="Authorization"
          value-placeholder="Bearer {{secrets.apiToken}}"
        />

        <div v-else-if="tab === 'body'" class="stack-2">
          <div class="segmented" role="group" :aria-label="t('request.bodyKind')">
            <button v-for="kind in ['Json', 'Text', 'Xml']" :key="kind" type="button"
                    :aria-pressed="bodyKind === kind" @click="bodyKind = kind">
              {{ kind }}
            </button>
          </div>
          <textarea v-model="body" class="textarea input-mono" rows="10"
                    :aria-label="t('request.body')"
                    placeholder='{"username":"demo","password":"{{secrets.demoPassword}}"}'></textarea>
        </div>
      </div>
    </div>

    <ResponseViewer :result="result" :pending="pending" @use-value="useValue">
      <template #actions>
        <button
          v-if="canRecordBaseline"
          type="button"
          class="btn btn-secondary btn-sm"
          @click="openCapture"
        >
          <Icon name="camera" />{{ t('baseline.saveAs') }}
        </button>
      </template>
    </ResponseViewer>

    <div v-if="capture" class="overlay" @click.self="capture = null">
      <div class="dialog" role="dialog" aria-modal="true" aria-labelledby="pf-capture-title">
        <div class="card-header">
          <h2 class="card-title" id="pf-capture-title">{{ t('baseline.saveAs') }}</h2>
          <p class="card-subtitle">{{ t('baseline.saveAsHelp') }}</p>
        </div>

        <div class="card-body stack">
          <label class="field">
            <span class="field-label">{{ t('common.name') }}</span>
            <input v-model="capture.name" class="input" dir="auto" autofocus
                   :placeholder="t('baseline.namePlaceholder')" />
          </label>

          <label class="field">
            <span class="field-label">
              {{ t('common.description') }}
              <span class="field-optional">{{ t('common.optional') }}</span>
            </span>
            <input v-model="capture.description" class="input" dir="auto"
                   :placeholder="t('baseline.descriptionPlaceholder')" />
          </label>

          <p class="field-hint">{{ t('baseline.capturedFrom', environment?.name ?? t('common.none')) }}</p>
        </div>

        <div class="card-footer">
          <button type="button" class="btn btn-secondary" @click="capture = null">
            {{ t('action.cancel') }}
          </button>
          <button
            type="button"
            class="btn btn-primary"
            :disabled="!capture.name.trim() || capturing"
            @click="saveBaseline"
          >
            <Icon name="camera" />
            {{ capturing ? t('common.saving') : t('action.save') }}
          </button>
        </div>
      </div>
    </div>
  </div>
</template>
