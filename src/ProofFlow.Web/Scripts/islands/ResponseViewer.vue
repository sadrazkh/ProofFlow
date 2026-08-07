<script setup lang="ts">
import { computed, ref } from 'vue';
import JsonTree from './JsonTree.vue';
import { formatDuration, t } from '../lib/i18n';
import { toast } from '../lib/toast';
import type { SendRequestResult } from './requestTypes';

/**
 * What came back.
 *
 * The failure states get as much design as the success one, because they are what a person
 * actually meets: a timeout, a refused connection, an address the environment is not allowed to
 * reach. Each says what happened and where the setting that fixes it lives — a raw exception
 * message is a dead end for the reader this product is for.
 */

const props = defineProps<{ result: SendRequestResult | null; pending: boolean }>();
const emit = defineEmits<{ useValue: [path: string, value: unknown] }>();

const view = ref<'tree' | 'raw'>('tree');
const menu = ref<{ path: string; value: unknown; x: number; y: number } | null>(null);

/**
 * Above this, the tree is not offered.
 *
 * Rendering a hundred thousand rows locks the tab, and no amount of collapsing helps because the
 * nodes are built before they are hidden. Virtual scrolling arrives with the diff viewer, which
 * needs it for the same reason; until then the honest thing is to say why rather than to freeze.
 */
const TREE_LIMIT_BYTES = 512 * 1024;

const parsed = computed<unknown | undefined>(() => {
  const body = props.result?.body;
  if (!body || !props.result?.contentType?.includes('json')) return undefined;
  if (body.length > TREE_LIMIT_BYTES) return undefined;

  try {
    return JSON.parse(body);
  } catch {
    // A content type of json and a body that is not json is worth seeing raw rather than as an
    // error — it is usually an HTML error page from a proxy, which is the answer.
    return undefined;
  }
});

const tooLargeForTree = computed(() =>
  (props.result?.body?.length ?? 0) > TREE_LIMIT_BYTES);

const statusTone = computed(() => {
  const code = props.result?.statusCode ?? 0;
  if (code >= 200 && code < 300) return 'pass';
  if (code >= 300 && code < 400) return 'warn';
  if (code >= 400) return 'fail';
  return 'idle';
});

const size = computed(() => {
  const bytes = props.result?.bodyBytes ?? 0;
  if (bytes < 1024) return t('response.bytes', bytes);
  if (bytes < 1024 * 1024) return t('response.kilobytes', (bytes / 1024).toFixed(1));
  return t('response.megabytes', (bytes / 1024 / 1024).toFixed(1));
});

/**
 * Advice per failure, keyed by the engine's own HttpFailureKind.
 *
 * The message from the server already says what happened; this says where to go. Keeping them
 * apart means the server does not have to know what the settings page is called.
 */
const failureHelp = computed(() => {
  const kind = props.result?.failureKind;
  return kind ? t(`response.failure.${kind}`) : '';
});

function pick(path: string, value: unknown, event: MouseEvent): void {
  menu.value = { path, value, x: event.clientX, y: event.clientY };
}

async function copy(text: string, message: string): Promise<void> {
  try {
    await navigator.clipboard.writeText(text);
    toast(message, 'success');
  } catch {
    // Clipboard access is refused in insecure contexts and by some policies. Saying so beats a
    // button that appears to do nothing.
    toast(t('response.copyRefused'), 'warn');
  }
  menu.value = null;
}
</script>

<template>
  <section class="card response" @click="menu = null">
    <div v-if="pending" class="response-pending">
      <div class="skeleton skeleton-title" style="inline-size: 30%;"></div>
      <div class="skeleton skeleton-text"></div>
      <div class="skeleton skeleton-text" style="inline-size: 80%;"></div>
      <div class="skeleton skeleton-text" style="inline-size: 60%;"></div>
    </div>

    <div v-else-if="!result" class="empty empty-inline">
      <div class="empty-art"><i data-lucide="send" aria-hidden="true"></i></div>
      <p class="empty-body">{{ t('response.empty') }}</p>
    </div>

    <template v-else-if="!result.succeeded">
      <div class="empty empty-inline">
        <div class="empty-art response-failed"><i data-lucide="circle-alert" aria-hidden="true"></i></div>
        <h3 class="empty-title">{{ result.failureMessage }}</h3>
        <p v-if="failureHelp" class="empty-body">{{ failureHelp }}</p>

        <ul v-if="result.unresolved.length" class="unresolved-list">
          <li v-for="item in result.unresolved" :key="item.reference">
            <code class="mono">{{ item.reference }}</code>
            <span class="subtle">{{ item.explanation }}</span>
          </li>
        </ul>

        <details v-if="result.failureDetail" class="response-detail">
          <summary>{{ t('response.technicalDetail') }}</summary>
          <pre class="mono">{{ result.failureDetail }}</pre>
        </details>
      </div>
    </template>

    <template v-else>
      <div class="response-head">
        <span class="badge" :class="`badge-${statusTone}`">
          <span class="tabular">{{ result.statusCode }}</span>
          <span v-if="result.reasonPhrase">{{ result.reasonPhrase }}</span>
        </span>
        <span class="response-meta tabular">
          <i data-lucide="timer" aria-hidden="true"></i>{{ formatDuration(result.durationMs) }}
        </span>
        <span class="response-meta tabular">
          <i data-lucide="database" aria-hidden="true"></i>{{ size }}
        </span>
        <span v-if="result.attempts > 1" class="badge badge-warn">
          {{ t('response.attempts', result.attempts) }}
        </span>
        <span v-if="result.redirectChain.length" class="badge badge-idle">
          {{ t('response.redirects', result.redirectChain.length) }}
        </span>

        <div class="segmented" role="group" :aria-label="t('response.view')">
          <button type="button" :aria-pressed="view === 'tree'" :disabled="!parsed" @click="view = 'tree'">
            {{ t('response.tree') }}
          </button>
          <button type="button" :aria-pressed="view === 'raw'" @click="view = 'raw'">
            {{ t('response.raw') }}
          </button>
        </div>
      </div>

      <div class="response-body">
        <p v-if="tooLargeForTree" class="response-notice">
          <i data-lucide="info" aria-hidden="true"></i>{{ t('response.tooLarge') }}
        </p>

        <div v-if="view === 'tree' && parsed !== undefined" class="json-sample json-viewer">
          <JsonTree :value="parsed" path="$" @pick="pick" />
        </div>
        <pre v-else class="json-sample response-raw">{{ result.body }}</pre>
      </div>
    </template>

    <!-- Only what this phase can actually do. An option that is present but disabled teaches
         people to stop reading the menu, so the ones without backing are absent. -->
    <div
      v-if="menu"
      class="menu"
      :style="{ insetBlockStart: `${menu.y + 6}px`, insetInlineStart: `${menu.x}px`, position: 'fixed' }"
      role="menu"
      @click.stop
    >
      <div class="menu-label mono">{{ menu.path }}</div>
      <button type="button" class="menu-item" role="menuitem" @click="copy(menu.path, t('response.pathCopied'))">
        <i data-lucide="copy"></i>{{ t('response.copyPath') }}
      </button>
      <button
        type="button"
        class="menu-item"
        role="menuitem"
        @click="copy(typeof menu.value === 'string' ? menu.value : JSON.stringify(menu.value), t('response.valueCopied'))"
      >
        <i data-lucide="braces"></i>{{ t('response.copyValue') }}
      </button>
      <button type="button" class="menu-item" role="menuitem" @click="emit('useValue', menu.path, menu.value); menu = null">
        <i data-lucide="variable"></i>{{ t('response.saveAsVariable') }}
      </button>
    </div>
  </section>
</template>
