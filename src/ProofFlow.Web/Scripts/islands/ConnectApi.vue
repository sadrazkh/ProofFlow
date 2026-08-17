<script setup lang="ts">
import { Icon } from '../lib/Icon';
import { computed, ref } from 'vue';
import { api, ApiError } from '../lib/api';
import { t } from '../lib/i18n';
import { toast } from '../lib/toast';

/**
 * Four questions, one at a time.
 *
 * Everything here could already be done across seven screens, in an order nobody stated, using
 * words that mean nothing until you already know the product. This asks: where is it, how do you
 * sign in, does that work, and what shall I call it — and does the rest.
 *
 * The third step is the one that matters. It signs in and makes one real call and shows both halves
 * separately, because «it didn't work» is four different problems: the address is wrong, the
 * credentials are wrong, the token is somewhere else in the answer, or the call itself is refused.
 * Nothing is saved until that step is green, so nobody ends up with a stored configuration that has
 * never worked.
 *
 * The rail is the one the nine-step capture wizard used before it was folded into the endpoint
 * page. Its styles outlived it; this is the same idea at a quarter of the length.
 */

const STEPS = ['where', 'auth', 'prove', 'keep'] as const;
type Step = (typeof STEPS)[number];

const KINDS = ['signIn', 'header', 'oauth2', 'none'] as const;
type Kind = (typeof KINDS)[number];

type StepResult = {
  ok: boolean;
  skipped: boolean;
  problem: string | null;
  detail: string | null;
  url: string | null;
  statusCode: number;
};

type TryResult = { signIn: StepResult; call: StepResult | null };

/** An environment already configured, as the four steps would have collected it. */
type Existing = {
  environmentId: string;
  name: string | null;
  baseUrl: string | null;
  allowPrivateNetwork: boolean;
  kind: Kind;
  headerName: string | null;
  headerValue: string | null;
  tokenUrl: string | null;
  tokenMethod: string | null;
  bodyKind: 'json' | 'form' | null;
  userField: string | null;
  userValue: string | null;
  passwordField: string | null;
  passwordValue: string | null;
  tokenPath: string | null;
  useHeaderName: string | null;
  useHeaderTemplate: string | null;
  grant: string | null;
  clientId: string | null;
  clientSecret: string | null;
  scope: string | null;
  credentialsInHeader: boolean;
};

const props = defineProps<{
  projectId: string;

  /** The fake API this application serves — something to try when there is nothing of one's own. */
  sampleBaseUrl: string;

  /**
   * Null for a first connection; otherwise this is the environment's authentication editor.
   *
   * Sealed values arrive as the `{{secrets.…}}` reference they are stored as, never as plaintext —
   * so the third step can prove them again without a password crossing to a browser.
   */
  existing?: Existing | null;
}>();

const was = props.existing ?? null;

/** The stored value where there is one, and the shape most APIs use where there is not. */
function had<T>(value: T | null | undefined, fallback: T): T {
  return value === null || value === undefined || value === '' ? fallback : value;
}

const step = ref<Step>('where');
const at = computed(() => STEPS.indexOf(step.value));

// ---- what the four steps collect ---------------------------------------------------------------

const baseUrl = ref(had(was?.baseUrl, ''));
const allowPrivateNetwork = ref(was?.allowPrivateNetwork ?? false);

const kind = ref<Kind>(had(was?.kind, 'signIn'));

const tokenUrl = ref(had(was?.tokenUrl, '/auth/login'));
const tokenMethod = ref(had(was?.tokenMethod, 'POST'));
const bodyKind = ref<'json' | 'form'>(had(was?.bodyKind, 'json'));
const userField = ref(had(was?.userField, 'username'));
const userValue = ref(had(was?.userValue, ''));
const passwordField = ref(had(was?.passwordField, 'password'));
const passwordValue = ref(had(was?.passwordValue, ''));
const tokenPath = ref(had(was?.tokenPath, ''));
const useHeaderName = ref(had(was?.useHeaderName, 'Authorization'));
const useHeaderTemplate = ref(had(was?.useHeaderTemplate, 'Bearer {token}'));

const headerName = ref(had(was?.headerName, 'Authorization'));
const headerValue = ref(had(was?.headerValue, ''));

const grant = ref<'client_credentials' | 'password'>(
  was?.grant === 'password' ? 'password' : 'client_credentials');
const clientId = ref(had(was?.clientId, ''));
const clientSecret = ref(had(was?.clientSecret, ''));
const scope = ref(had(was?.scope, ''));
const credentialsInHeader = ref(was?.credentialsInHeader ?? false);

// Never prefilled, even when editing. The path is what step three proves, and carrying over a path
// that worked last month would let somebody save a changed password without ever exercising it.
const method = ref('GET');
const path = ref('');
const name = ref(had(was?.name, ''));

// ---- state -------------------------------------------------------------------------------------

const trying = ref(false);
const saving = ref(false);
const result = ref<TryResult | null>(null);

/**
 * Whether the address looks like something only this machine can reach.
 *
 * Asked here rather than discovered later: the URL guard refuses private addresses by default, and
 * a refusal two steps further on is the worst moment to learn that a checkbox governs it.
 */
const looksPrivate = computed(() =>
  /^https?:\/\/(localhost|127\.|0\.0\.0\.0|\[::1\]|10\.|192\.168\.|172\.(1[6-9]|2\d|3[01])\.)/i
    .test(baseUrl.value.trim()));

const addressed = computed(() => /^https?:\/\/.+/i.test(baseUrl.value.trim()));

const canProve = computed(() => {
  if (path.value.trim().length === 0) return false;
  if (kind.value === 'none') return true;
  if (kind.value === 'header') return headerName.value.trim() !== '' && headerValue.value.trim() !== '';
  if (kind.value === 'oauth2') return tokenUrl.value.trim() !== '' && clientId.value.trim() !== '';
  return tokenUrl.value.trim() !== '' && passwordValue.value.trim() !== '';
});

const proved = computed(() =>
  result.value !== null
  && (result.value.signIn.ok || result.value.signIn.skipped)
  && result.value.call?.ok === true);

/** Which steps you may jump to: the ones behind you, and never one whose gate is still shut. */
function reachable(index: number): boolean {
  if (index <= at.value) return true;
  if (index >= 1 && !addressed.value) return false;
  if (index >= 3 && !proved.value) return false;
  return index === at.value + 1;
}

function attempt() {
  return {
    environmentId: was?.environmentId ?? null,
    name: name.value.trim(),
    baseUrl: baseUrl.value.trim(),
    allowPrivateNetwork: allowPrivateNetwork.value,
    kind: kind.value,
    headerName: headerName.value,
    headerValue: headerValue.value,
    tokenUrl: tokenUrl.value,
    tokenMethod: tokenMethod.value,
    bodyKind: bodyKind.value,
    userField: userField.value,
    userValue: userValue.value,
    passwordField: passwordField.value,
    passwordValue: passwordValue.value,
    tokenPath: tokenPath.value.trim() || null,
    useHeaderName: useHeaderName.value,
    useHeaderTemplate: useHeaderTemplate.value,
    grant: grant.value,
    clientId: clientId.value,
    clientSecret: clientSecret.value,
    scope: scope.value,
    credentialsInHeader: credentialsInHeader.value,
    method: method.value,
    path: path.value.trim(),
  };
}

function go(index: number): void {
  if (index >= 0 && index < STEPS.length && reachable(index)) step.value = STEPS[index]!;
}

async function prove(): Promise<void> {
  if (trying.value) return;

  trying.value = true;
  result.value = null;

  try {
    result.value = await api.post<TryResult>(`/projects/${props.projectId}/connect/try`, attempt());

    // A name it can guess, so the last step is a confirmation rather than a blank field.
    if (proved.value && name.value.trim() === '') name.value = hostOf(baseUrl.value);
  } catch (error) {
    toast(error instanceof ApiError ? error.message : t('error.body'), 'error');
  } finally {
    trying.value = false;
  }
}

async function keep(): Promise<void> {
  if (saving.value || !proved.value) return;

  saving.value = true;

  try {
    const saved = await api.post<{ url: string }>(
      `/projects/${props.projectId}/connect/save`, attempt());

    location.assign(saved.url);
  } catch (error) {
    toast(error instanceof ApiError ? error.message : t('error.body'), 'error');
    saving.value = false;
  }
}

/** Fills all four steps with this application's own fake API — something to try in one click. */
function useSample(): void {
  baseUrl.value = props.sampleBaseUrl;
  allowPrivateNetwork.value = true;
  kind.value = 'signIn';
  tokenUrl.value = '/auth/login';
  userField.value = 'username';
  userValue.value = 'demo';
  passwordField.value = 'password';
  passwordValue.value = 'demo-password';
  method.value = 'GET';
  path.value = '/categories';
}

function hostOf(url: string): string {
  try {
    return new URL(url).host;
  } catch {
    return t('connect.keep.fallbackName');
  }
}
</script>

<template>
  <div class="wizard">
    <!-- Where you are, and how much is left. Four is few enough to name every one of them. -->
    <nav class="wizard-rail" :aria-label="t('connect.steps')">
      <ol>
        <li
          v-for="(one, index) in STEPS"
          :key="one"
          :class="{ 'is-current': one === step, 'is-done': index < at }"
        >
          <button
            type="button"
            :disabled="!reachable(index)"
            :aria-current="one === step ? 'step' : undefined"
            @click="go(index)"
          >
            <span class="wizard-mark" aria-hidden="true">
              <Icon v-if="index < at" name="check" :size="13" />
              <span v-else>{{ index + 1 }}</span>
            </span>
            <span class="wizard-name">{{ t(`connect.step.${one}`) }}</span>
          </button>
        </li>
      </ol>
    </nav>

    <section class="card wizard-panel">
      <!-- 1 — where is it -->
      <div v-if="step === 'where'" class="card-body stack">
        <div class="section-head">
          <h2 class="card-title">{{ t('connect.where.title') }}</h2>
          <p class="card-subtitle">{{ t('connect.where.help') }}</p>
        </div>

        <div class="field">
          <label class="field-label" for="c-base">{{ t('environment.baseUrl') }}</label>
          <input
            id="c-base"
            v-model="baseUrl"
            class="input input-mono"
            dir="ltr"
            placeholder="https://api.example.com"
            autocomplete="off"
            spellcheck="false"
          />
          <p class="field-hint">{{ t('connect.where.hint') }}</p>
        </div>

        <label v-if="looksPrivate" class="check-row">
          <input v-model="allowPrivateNetwork" class="checkbox" type="checkbox" />
          <span class="check-row-text">
            <span class="check-row-title">{{ t('environment.privateNetwork') }}</span>
            <span class="check-row-hint">{{ t('connect.where.private') }}</span>
          </span>
        </label>

        <p v-if="!was" class="row wrap text-xs subtle" style="gap: var(--space-2);">
          {{ t('connect.where.sample') }}
          <button type="button" class="btn btn-ghost btn-sm" @click="useSample">
            <Icon name="flask-conical" />{{ t('connect.where.useSample') }}
          </button>
        </p>
      </div>

      <!-- 2 — how do you sign in -->
      <div v-else-if="step === 'auth'" class="card-body stack">
        <div class="section-head">
          <h2 class="card-title">{{ t('connect.auth.title') }}</h2>
          <p class="card-subtitle">{{ t('connect.auth.help') }}</p>
        </div>

        <div class="connect-kinds" role="radiogroup" :aria-label="t('connect.auth.title')">
          <label v-for="one in KINDS" :key="one" class="connect-kind"
                 :class="{ 'is-chosen': kind === one }">
            <input v-model="kind" class="radio" type="radio" :value="one" name="connect-kind" />
            <span class="check-row-text">
              <span class="check-row-title">{{ t(`connect.kind.${one}`) }}</span>
              <span class="check-row-hint">{{ t(`connect.kind.${one}.help`) }}</span>
            </span>
          </label>
        </div>

        <!-- Only the chosen one's fields. Four sets at once is the form nobody reads. -->
        <div v-if="kind === 'signIn'" class="stack">
          <div class="field">
            <label class="field-label" for="c-token-url">{{ t('connect.signIn.url') }}</label>
            <input id="c-token-url" v-model="tokenUrl" class="input input-mono" dir="ltr"
                   placeholder="/auth/login" spellcheck="false" />
            <p class="field-hint">{{ t('connect.signIn.urlHint') }}</p>
          </div>

          <div class="connect-pair">
            <div class="field">
              <label class="field-label" for="c-user-field">{{ t('connect.signIn.userField') }}</label>
              <input id="c-user-field" v-model="userField" class="input input-mono" dir="ltr"
                     spellcheck="false" />
            </div>
            <div class="field grow">
              <label class="field-label" for="c-user">{{ t('connect.signIn.userValue') }}</label>
              <input id="c-user" v-model="userValue" class="input" dir="ltr" autocomplete="off"
                     spellcheck="false" />
            </div>
          </div>

          <div class="connect-pair">
            <div class="field">
              <label class="field-label" for="c-pass-field">{{ t('connect.signIn.passwordField') }}</label>
              <input id="c-pass-field" v-model="passwordField" class="input input-mono" dir="ltr"
                     spellcheck="false" />
            </div>
            <div class="field grow">
              <label class="field-label" for="c-pass">{{ t('connect.signIn.passwordValue') }}</label>
              <input id="c-pass" v-model="passwordValue" class="input" type="password"
                     autocomplete="off" />
            </div>
          </div>

          <p class="field-hint"><Icon name="lock" :size="13" />&nbsp;{{ t('connect.sealed') }}</p>

          <details class="connect-more">
            <summary>{{ t('connect.more') }}</summary>
            <div class="stack">
              <div class="connect-pair">
                <div class="field">
                  <label class="field-label" for="c-body-kind">{{ t('connect.signIn.bodyKind') }}</label>
                  <select id="c-body-kind" v-model="bodyKind" class="select">
                    <option value="json">JSON</option>
                    <option value="form">form-urlencoded</option>
                  </select>
                </div>
                <div class="field grow">
                  <label class="field-label" for="c-token-path">{{ t('connect.signIn.tokenPath') }}</label>
                  <input id="c-token-path" v-model="tokenPath" class="input input-mono" dir="ltr"
                         :placeholder="t('connect.signIn.tokenPathAuto')" spellcheck="false" />
                  <p class="field-hint">{{ t('connect.signIn.tokenPathHint') }}</p>
                </div>
              </div>

              <div class="connect-pair">
                <div class="field">
                  <label class="field-label" for="c-use-name">{{ t('connect.use.header') }}</label>
                  <input id="c-use-name" v-model="useHeaderName" class="input input-mono" dir="ltr"
                         spellcheck="false" />
                </div>
                <div class="field grow">
                  <label class="field-label" for="c-use-template">{{ t('connect.use.template') }}</label>
                  <input id="c-use-template" v-model="useHeaderTemplate" class="input input-mono"
                         dir="ltr" spellcheck="false" />
                  <p class="field-hint">{{ t('connect.use.templateHint') }}</p>
                </div>
              </div>
            </div>
          </details>
        </div>

        <div v-else-if="kind === 'header'" class="stack">
          <div class="connect-pair">
            <div class="field">
              <label class="field-label" for="c-h-name">{{ t('connect.header.name') }}</label>
              <input id="c-h-name" v-model="headerName" class="input input-mono" dir="ltr"
                     spellcheck="false" />
            </div>
            <div class="field grow">
              <label class="field-label" for="c-h-value">{{ t('connect.header.value') }}</label>
              <input id="c-h-value" v-model="headerValue" class="input input-mono" type="password"
                     dir="ltr" autocomplete="off" placeholder="Bearer eyJ…" />
              <p class="field-hint">{{ t('connect.header.hint') }}</p>
            </div>
          </div>

          <p class="field-hint"><Icon name="lock" :size="13" />&nbsp;{{ t('connect.sealed') }}</p>
        </div>

        <div v-else-if="kind === 'oauth2'" class="stack">
          <div class="field">
            <label class="field-label" for="c-o-url">{{ t('connect.signIn.url') }}</label>
            <input id="c-o-url" v-model="tokenUrl" class="input input-mono" dir="ltr"
                   placeholder="/connect/token" spellcheck="false" />
          </div>

          <div class="connect-pair">
            <div class="field">
              <label class="field-label" for="c-grant">{{ t('connect.oauth.grant') }}</label>
              <select id="c-grant" v-model="grant" class="select">
                <option value="client_credentials">client_credentials</option>
                <option value="password">password</option>
              </select>
            </div>
            <div class="field grow">
              <label class="field-label" for="c-scope">
                {{ t('connect.oauth.scope') }}
                <span class="field-optional">· {{ t('common.optional') }}</span>
              </label>
              <input id="c-scope" v-model="scope" class="input input-mono" dir="ltr"
                     spellcheck="false" />
            </div>
          </div>

          <div class="connect-pair">
            <div class="field grow">
              <label class="field-label" for="c-client">{{ t('connect.oauth.clientId') }}</label>
              <input id="c-client" v-model="clientId" class="input input-mono" dir="ltr"
                     spellcheck="false" />
            </div>
            <div class="field grow">
              <label class="field-label" for="c-client-secret">{{ t('connect.oauth.clientSecret') }}</label>
              <input id="c-client-secret" v-model="clientSecret" class="input input-mono"
                     type="password" dir="ltr" autocomplete="off" />
            </div>
          </div>

          <div v-if="grant === 'password'" class="connect-pair">
            <div class="field grow">
              <label class="field-label" for="c-o-user">{{ t('connect.signIn.userValue') }}</label>
              <input id="c-o-user" v-model="userValue" class="input" dir="ltr" autocomplete="off" />
            </div>
            <div class="field grow">
              <label class="field-label" for="c-o-pass">{{ t('connect.signIn.passwordValue') }}</label>
              <input id="c-o-pass" v-model="passwordValue" class="input" type="password"
                     autocomplete="off" />
            </div>
          </div>

          <label class="check-row">
            <input v-model="credentialsInHeader" class="checkbox" type="checkbox" />
            <span class="check-row-text">
              <span class="check-row-title">{{ t('connect.oauth.basic') }}</span>
              <span class="check-row-hint">{{ t('connect.oauth.basicHelp') }}</span>
            </span>
          </label>
        </div>
      </div>

      <!-- 3 — prove it -->
      <div v-else-if="step === 'prove'" class="card-body stack">
        <div class="section-head">
          <h2 class="card-title">{{ t('connect.prove.title') }}</h2>
          <p class="card-subtitle">{{ t('connect.prove.help') }}</p>
        </div>

        <div class="connect-pair">
          <div class="field">
            <label class="field-label" for="c-method">{{ t('request.method') }}</label>
            <select id="c-method" v-model="method" class="select">
              <option v-for="verb in ['GET', 'POST', 'PUT', 'PATCH', 'DELETE']" :key="verb"
                      :value="verb">{{ verb }}</option>
            </select>
          </div>
          <div class="field grow">
            <label class="field-label" for="c-path">{{ t('connect.prove.path') }}</label>
            <input id="c-path" v-model="path" class="input input-mono" dir="ltr"
                   placeholder="/products" spellcheck="false" />
            <p class="field-hint">{{ t('connect.prove.pathHint') }}</p>
          </div>
        </div>

        <div class="row wrap">
          <button type="button" class="btn btn-primary" :disabled="!canProve || trying"
                  @click="prove">
            <Icon :name="trying ? 'loader-circle' : 'play'" :class="trying ? 'is-spinning' : ''" />
            {{ trying ? t('connect.prove.trying') : t('connect.prove.try') }}
          </button>
          <p v-if="!canProve" class="field-hint" style="margin: 0;">{{ t('connect.prove.needs') }}</p>
        </div>

        <!-- Both halves, separately. «It didn't work» is four different problems. -->
        <div v-if="result" class="stack-2" aria-live="polite">
          <div v-if="!result.signIn.skipped" class="connect-line"
               :class="result.signIn.ok ? 'is-ok' : 'is-bad'">
            <Icon :name="result.signIn.ok ? 'circle-check' : 'circle-alert'" :size="18" />
            <div class="stack-2">
              <p class="connect-line-title">
                {{ result.signIn.ok ? t('connect.prove.signedIn') : t('connect.prove.signInFailed') }}
              </p>
              <p v-if="result.signIn.detail" class="connect-line-detail mono" dir="ltr">
                {{ result.signIn.detail }}
              </p>
              <p v-if="result.signIn.problem" class="connect-line-detail" dir="auto">
                {{ result.signIn.problem }}
              </p>
            </div>
          </div>

          <div v-if="result.call" class="connect-line" :class="result.call.ok ? 'is-ok' : 'is-bad'">
            <Icon :name="result.call.ok ? 'circle-check' : 'circle-alert'" :size="18" />
            <div class="stack-2">
              <p class="connect-line-title">
                {{ result.call.ok
                  ? t('connect.prove.answered', result.call.statusCode)
                  : (result.call.statusCode > 0
                    ? t('connect.prove.refused', result.call.statusCode)
                    : t('connect.prove.noAnswer')) }}
              </p>
              <p v-if="result.call.url" class="connect-line-detail mono" dir="ltr">
                {{ result.call.url }}
              </p>
              <p v-if="result.call.problem" class="connect-line-detail" dir="auto">
                {{ result.call.problem }}
              </p>
              <pre v-if="result.call.detail" class="connect-body" dir="ltr">{{ result.call.detail }}</pre>
            </div>
          </div>

          <p v-if="proved" class="field-hint">{{ t('connect.prove.now') }}</p>
        </div>
      </div>

      <!-- 4 — keep it -->
      <div v-else class="card-body stack">
        <div class="section-head">
          <h2 class="card-title">{{ t('connect.keep.title') }}</h2>
          <p class="card-subtitle">{{ t('connect.keep.help') }}</p>
        </div>

        <div class="field">
          <label class="field-label" for="c-name">{{ t('connect.keep.name') }}</label>
          <input id="c-name" v-model="name" class="input" dir="auto" />
        </div>

        <!-- What pressing the button will make. Named before it exists, not after. -->
        <ul class="connect-summary">
          <li>
            <Icon name="globe" :size="15" />
            {{ t('connect.keep.environment', name.trim() || hostOf(baseUrl)) }}
          </li>
          <li v-if="kind !== 'none'">
            <Icon name="key-round" :size="15" />{{ t(`connect.keep.auth.${kind}`) }}
          </li>
          <li v-if="kind !== 'none'">
            <Icon name="lock" :size="15" />{{ t('connect.keep.secrets') }}
          </li>
          <li v-if="!was && path.trim()">
            <Icon name="target" :size="15" />{{ t('connect.keep.endpoint', method, path.trim()) }}
          </li>
        </ul>
      </div>

      <footer class="card-footer">
        <button v-if="at > 0" type="button" class="btn btn-ghost" @click="go(at - 1)">
          {{ t('action.back') }}
        </button>

        <span class="grow"></span>

        <button
          v-if="step !== 'keep'"
          type="button"
          class="btn btn-primary"
          :disabled="!reachable(at + 1)"
          @click="go(at + 1)"
        >
          {{ t('action.next') }}
        </button>

        <button v-else type="button" class="btn btn-primary" :disabled="saving" @click="keep">
          <Icon name="check" />
          {{ saving ? t('common.saving') : (was ? t('action.save') : t('connect.keep.action')) }}
        </button>
      </footer>
    </section>
  </div>
</template>
