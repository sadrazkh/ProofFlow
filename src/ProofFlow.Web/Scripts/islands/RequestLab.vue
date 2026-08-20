<script setup lang="ts">
import { Icon } from '../lib/Icon';
import { computed, nextTick, onMounted, ref, watch } from 'vue';
import KeyValueTable, { type KeyValueRow } from './KeyValueTable.vue';
import ReferencePicker from './ReferencePicker.vue';
import ResponseViewer from './ResponseViewer.vue';
import { api, ApiError } from '../lib/api';
import { insertAtCaret } from '../lib/caret';
import { t } from '../lib/i18n';
import { toast } from '../lib/toast';
import {
  METHODS, METHODS_WITH_BODY,
  type LabEnvironment, type SendRequestResult, type TokenResult, type VariableNames,
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

  /** An address whoever linked here had in mind. Beats what the browser remembered. */
  url?: string | null;
}>();

const method = ref('GET');
const url = ref('');
const environmentId = ref<string>(props.environments[0]?.id ?? '');
const tab = ref<'query' | 'headers' | 'body' | 'auth'>('query');
const query = ref<KeyValueRow[]>([{ name: '', value: '', enabled: true }]);
const headers = ref<KeyValueRow[]>([{ name: '', value: '', enabled: true }]);
const bodyKind = ref('Json');
const body = ref('');

/**
 * How this API lets somebody in, and what it takes to get there.
 *
 * Beside the request rather than behind a settings page, because it is part of the request:
 * choosing a kind adds one header and nothing else, and that header appears under «what was sent»
 * like any other. Nothing here is hidden machinery, which is the point — somebody debugging a 401
 * has to be able to see exactly what went.
 */
const authKind = ref<'none' | 'bearer' | 'basic' | 'apiKey' | 'oauthClient' | 'oauthPassword'>('none');
const authToken = ref('');
const authUser = ref('');
const authPassword = ref('');
const authHeaderName = ref('X-API-Key');
const tokenUrl = ref('');
const clientId = ref('');
const clientSecret = ref('');
const scope = ref('');
const credentialsInHeader = ref(false);
const gettingToken = ref(false);
const tokenNote = ref('');
const tokenProblem = ref('');

const needsToken = computed(
  () => authKind.value === 'oauthClient' || authKind.value === 'oauthPassword');

/**
 * The one header this authorisation produces.
 *
 * Computed in one place and used both to send and to show. A panel that described what it would do
 * and a sender that did something slightly different is the shape of a bug nobody finds for a week.
 */
const authHeader = computed<{ name: string; value: string } | null>(() => {
  if (authKind.value === 'none') return null;

  if (authKind.value === 'basic') {
    if (!authUser.value && !authPassword.value) return null;
    return { name: 'Authorization', value: `Basic ${btoa(`${authUser.value}:${authPassword.value}`)}` };
  }

  if (authKind.value === 'apiKey') {
    if (!authToken.value || !authHeaderName.value) return null;
    return { name: authHeaderName.value, value: authToken.value };
  }

  if (!authToken.value) return null;
  return { name: 'Authorization', value: `Bearer ${authToken.value}` };
});

/**
 * Asks the authorisation server, through the same guard as every other address here.
 *
 * The credentials can be references — «{{secrets.clientSecret}}» — because the server resolves them
 * before it sends. Which means the panel can be filled in on a screen somebody is sharing.
 */
async function getToken(): Promise<void> {
  if (gettingToken.value) return;

  gettingToken.value = true;
  tokenNote.value = '';
  tokenProblem.value = '';

  try {
    const result = await api.post<TokenResult>(`/projects/${props.projectId}/request/token`, {
      environmentId: environmentId.value || null,
      grant: authKind.value === 'oauthPassword' ? 'password' : 'client_credentials',
      tokenUrl: tokenUrl.value,
      clientId: clientId.value,
      clientSecret: clientSecret.value,
      scope: scope.value,
      username: authUser.value,
      password: authPassword.value,
      credentialsInHeader: credentialsInHeader.value,
    });

    if (!result.succeeded) {
      tokenProblem.value = [result.problem, result.detail].filter(Boolean).join(' — ');
      return;
    }

    authToken.value = result.accessToken ?? '';

    tokenNote.value = result.expiresIn
      ? t('auth.expiresIn', howLong(result.expiresIn))
      : t('auth.expiresUnknown');

    toast(t('auth.got'), 'success');
  } catch (error) {
    tokenProblem.value = error instanceof ApiError ? error.message : t('error.body');
  } finally {
    gettingToken.value = false;
  }
}

/** Seconds, in the unit somebody would say out loud. */
function howLong(seconds: number): string {
  if (seconds < 90) return `${seconds}s`;
  if (seconds < 5400) return `${Math.round(seconds / 60)}m`;
  return `${Math.round(seconds / 3600)}h`;
}

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
/** The same names the chips are checked against, in the shape the picker wants. */
const catalogue = computed(() => ({
  environment: known.value.environment,
  variables: known.value.variables,
  secrets: known.value.secrets,

  // A lab request is not a scenario: there is no step before it and nothing was asked when it
  // started. Offering either would offer something that cannot resolve here.
  inputs: [],
  steps: [],
}));

const urlBox = ref<HTMLInputElement | null>(null);
const bodyBox = ref<HTMLTextAreaElement | null>(null);

function insertIntoUrl(text: string): void {
  if (urlBox.value) url.value = insertAtCaret(urlBox.value, text);
}

function insertIntoBody(text: string): void {
  if (bodyBox.value) body.value = insertAtCaret(bodyBox.value, text);
}

/** Replaces the half-typed reference, wherever it was typed. */
function completeIn(
  field: HTMLInputElement | HTMLTextAreaElement | null,
  set: (next: string) => void,
  text: string, from: number, to: number,
): void {
  if (!field) return;

  const value = field.value ?? '';
  const next = value.slice(0, from) + text + value.slice(to);

  set(next);

  void nextTick(() => {
    field.focus();
    field.setSelectionRange(from + text.length, from + text.length);
  });
}

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

  // After restore, deliberately: a link that names an address means that address, and the last one
  // typed here is the thing being replaced rather than the thing to keep.
  if (props.url) url.value = props.url;

  void loadNames();
});

watch(environmentId, () => void loadNames());
watch([method, url, environmentId, query, headers, body, bodyKind], remember, { deep: true });
watch([authKind, authHeaderName, tokenUrl, clientId, scope, credentialsInHeader], remember);

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

      // The shape of the authorisation, not its credentials. A client secret, a password and a
      // live bearer token are the three things that must not end up in local storage — where they
      // would outlive the tab, survive in a backup, and be readable by anything else on the page.
      auth: {
        kind: authKind.value,
        headerName: authHeaderName.value,
        tokenUrl: tokenUrl.value,
        clientId: clientId.value,
        scope: scope.value,
        credentialsInHeader: credentialsInHeader.value,
      },
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

    if (state.auth) {
      authKind.value = state.auth.kind ?? 'none';
      authHeaderName.value = state.auth.headerName ?? 'X-API-Key';
      tokenUrl.value = state.auth.tokenUrl ?? '';
      clientId.value = state.auth.clientId ?? '';
      scope.value = state.auth.scope ?? '';
      credentialsInHeader.value = state.auth.credentialsInHeader ?? false;
    }
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

      // Appended rather than merged: a header typed by hand wins, because somebody who wrote an
      // Authorization line themselves meant that one.
      headers: [
        ...(authHeader.value ? [{ ...authHeader.value, enabled: true }] : []),
        ...headers.value.filter((r) => r.name),
      ],
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
 * The request as a shell command, for the terminal or the bug report.
 *
 * Built from the same pieces send() posts — the enabled rows, and the auth-derived header with
 * whatever token it currently holds. The address is the template as typed, `{{…}}` and all, which
 * is honest about what has not been resolved; after a send, the resolved address the server
 * actually called is used instead.
 */
function asCurl(): string {
  const resolved = result.value?.resolvedUrl ?? null;
  const target = resolved ?? url.value;

  const pairs = query.value.filter((row) => row.name && row.enabled !== false);
  const full = resolved === null && pairs.length > 0
    ? target
      + (target.includes('?') ? '&' : '?')
      + pairs
        .map((row) => `${encodeURIComponent(row.name)}=${encodeURIComponent(row.value ?? '')}`)
        .join('&')
    : target;

  const lines = [`curl -X ${method.value} ${shellQuote(full)}`];

  for (const header of [
    ...(authHeader.value ? [authHeader.value] : []),
    ...headers.value.filter((row) => row.name && row.enabled !== false),
  ]) {
    lines.push(`-H ${shellQuote(`${header.name}: ${header.value ?? ''}`)}`);
  }

  if (supportsBody.value && body.value) {
    if (bodyKind.value === 'Json'
        && !headers.value.some((row) => row.name.toLowerCase() === 'content-type')) {
      lines.push(`-H ${shellQuote('Content-Type: application/json')}`);
    }

    lines.push(`--data ${shellQuote(body.value)}`);
  }

  return lines.join(' \\\n  ');
}

/** Single quotes, with embedded ones closed-escaped-reopened — the one POSIX-safe quoting. */
function shellQuote(text: string): string {
  return `'${text.replace(/'/g, `'\\''`)}'`;
}

async function copyCurl(): Promise<void> {
  try {
    await navigator.clipboard.writeText(asCurl());
    toast(t('request.curlCopied'), 'success');
  } catch {
    toast(t('response.copyRefused'), 'warn');
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
      `/projects/${props.projectId}/endpoints/capture`,
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

        <div class="grow request-url" style="min-inline-size: 0;">
          <input
            ref="urlBox"
            v-model="url"
            class="input input-mono"
            :placeholder="environment?.baseUrl ? '/fake/categories' : 'https://api.example.com/orders'"
            :aria-label="t('request.url')"
            @keydown.enter="send"
          />

          <ReferencePicker
            :catalogue="catalogue"
            :field="t('request.url')"
            :watching="() => urlBox"
            @pick="insertIntoUrl"
            @complete="(text, from, to) => completeIn(urlBox, (next) => { url = next; }, text, from, to)"
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

        <button
          type="button"
          class="btn btn-ghost has-tip"
          :disabled="!url"
          :aria-label="t('request.copyCurl')"
          :data-tip="t('request.copyCurl')"
          @click="copyCurl"
        >
          <Icon name="clipboard-paste" />
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

        <button type="button" class="tab" role="tab" :class="{ 'is-active': tab === 'auth' }"
                :aria-selected="tab === 'auth'" @click="tab = 'auth'">
          {{ t('auth.title') }}
          <span v-if="authHeader" class="tab-count"><Icon name="lock" /></span>
        </button>
      </div>

      <div class="card-body">
        <KeyValueTable
          v-if="tab === 'query'"
          v-model="query"
          :label="t('request.query')"
          :catalogue="catalogue"
          name-placeholder="page"
          value-placeholder="1"
        />

        <KeyValueTable
          v-else-if="tab === 'headers'"
          v-model="headers"
          :label="t('request.headers')"
          :catalogue="catalogue"
          name-placeholder="Authorization"
          value-placeholder="Bearer {{secrets.apiToken}}"
        />

        <div v-else-if="tab === 'auth'" class="stack-2 auth-panel">
          <p class="section-help">{{ t('auth.help') }}</p>

          <label class="field">
            <span class="field-label">{{ t('auth.kind') }}</span>
            <select v-model="authKind" class="select">
              <option value="none">{{ t('auth.kind.none') }}</option>
              <option value="bearer">{{ t('auth.kind.bearer') }}</option>
              <option value="basic">{{ t('auth.kind.basic') }}</option>
              <option value="apiKey">{{ t('auth.kind.apiKey') }}</option>
              <option value="oauthClient">{{ t('auth.kind.oauthClient') }}</option>
              <option value="oauthPassword">{{ t('auth.kind.oauthPassword') }}</option>
            </select>
          </label>

          <div v-if="needsToken" class="auth-grid">
            <label class="field auth-wide">
              <span class="field-label">{{ t('auth.tokenUrl') }}</span>
              <input v-model="tokenUrl" class="input input-mono" dir="ltr"
                     placeholder="/connect/token" />
              <span class="field-hint">{{ t('auth.tokenUrl.help') }}</span>
            </label>

            <label class="field">
              <span class="field-label">{{ t('auth.clientId') }}</span>
              <input v-model="clientId" class="input input-mono" dir="ltr" />
            </label>

            <label class="field">
              <span class="field-label">{{ t('auth.clientSecret') }}</span>
              <input v-model="clientSecret" class="input input-mono" type="password" dir="ltr"
                     placeholder="{{secrets.clientSecret}}" />
            </label>

            <label v-if="authKind === 'oauthPassword'" class="field">
              <span class="field-label">{{ t('auth.username') }}</span>
              <input v-model="authUser" class="input" dir="auto" />
            </label>

            <label v-if="authKind === 'oauthPassword'" class="field">
              <span class="field-label">{{ t('auth.password') }}</span>
              <input v-model="authPassword" class="input" type="password" dir="ltr" />
            </label>

            <label class="field">
              <span class="field-label">{{ t('auth.scope') }}</span>
              <input v-model="scope" class="input input-mono" dir="ltr" placeholder="api.read" />
            </label>

            <label class="check-row auth-wide">
              <input v-model="credentialsInHeader" class="checkbox" type="checkbox" />
              <span>
                {{ t('auth.credentialsInHeader') }}
                <span class="field-hint">{{ t('auth.credentialsInHeader.help') }}</span>
              </span>
            </label>

            <div class="auth-wide row">
              <button type="button" class="btn btn-secondary" :disabled="gettingToken || !tokenUrl"
                      @click="getToken">
                <Icon :name="gettingToken ? 'loader' : 'key-round'" />
                {{ gettingToken ? t('auth.getting') : t('auth.get') }}
              </button>
              <span v-if="tokenNote" class="text-xs subtle">{{ tokenNote }}</span>
            </div>

            <p v-if="tokenProblem" class="auth-wide field-error" dir="auto">
              <Icon name="circle-alert" :size="13" />{{ tokenProblem }}
            </p>
          </div>

          <div v-if="authKind === 'basic' || authKind === 'oauthPassword'" class="auth-grid">
            <label v-if="authKind === 'basic'" class="field">
              <span class="field-label">{{ t('auth.username') }}</span>
              <input v-model="authUser" class="input" dir="auto" />
            </label>

            <label v-if="authKind === 'basic'" class="field">
              <span class="field-label">{{ t('auth.password') }}</span>
              <input v-model="authPassword" class="input" type="password" dir="ltr" />
            </label>
          </div>

          <label v-if="authKind === 'apiKey'" class="field">
            <span class="field-label">{{ t('auth.headerName') }}</span>
            <input v-model="authHeaderName" class="input input-mono" dir="ltr" />
          </label>

          <label v-if="authKind !== 'none' && authKind !== 'basic'" class="field">
            <span class="field-label">{{ t('auth.token') }}</span>
            <textarea v-model="authToken" class="textarea input-mono" rows="3" dir="ltr"
                      :placeholder="'{{secrets.apiToken}}'"></textarea>
          </label>

          <p v-if="authHeader" class="auth-applied mono" dir="ltr">
            <Icon name="check" :size="13" />{{ authHeader.name }}: {{ authHeader.value.slice(0, 24) }}…
          </p>
        </div>

        <div v-else-if="tab === 'body'" class="stack-2">
          <div class="segmented" role="group" :aria-label="t('request.bodyKind')">
            <button v-for="kind in ['Json', 'Text', 'Xml']" :key="kind" type="button"
                    :aria-pressed="bodyKind === kind" @click="bodyKind = kind">
              {{ kind }}
            </button>
          </div>
          <div class="request-body-line">
            <span class="grow"></span>
            <ReferencePicker
              :catalogue="catalogue"
              :field="t('request.body')"
              :watching="() => bodyBox"
              @pick="insertIntoBody"
              @complete="(text, from, to) => completeIn(bodyBox, (next) => { body = next; }, text, from, to)"
            />
          </div>

          <textarea ref="bodyBox" v-model="body" class="textarea input-mono" rows="10"
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
