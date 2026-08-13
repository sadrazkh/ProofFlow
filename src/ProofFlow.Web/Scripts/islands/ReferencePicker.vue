<script setup lang="ts">
import { Icon } from '../lib/Icon';
import { computed, nextTick, onBeforeUnmount, onMounted, ref } from 'vue';
import { t } from '../lib/i18n';
import { referenceOptions, type ReferenceCatalogue, type ReferenceOption } from './referenceTypes';

/**
 * Offers what can go between braces, and puts the chosen one where the cursor was.
 *
 * The alternative is what this replaces: knowing that the scope for a variable is «vars» and not
 * «variables», that a step is referred to by the name somebody typed on its card, and that a
 * response has a «body» under a «response». None of that is discoverable by looking at a text box,
 * and getting it wrong produces a request that fails somewhere else.
 *
 * It writes into the field rather than replacing it. A URL is usually a reference and then a path,
 * and a picker that overwrote the line would be one nobody could use twice.
 */

const props = defineProps<{
  catalogue: ReferenceCatalogue;

  /** Narrows the button's label to the field it belongs to, for a screen reader. */
  field: string;

  disabled?: boolean;

  /**
   * The text box to watch for somebody typing two braces.
   *
   * Given as a getter rather than an element because the inspector renders its fields from a list
   * and the element does not exist when this is created.
   */
  watching?: () => HTMLInputElement | HTMLTextAreaElement | null;
}>();

const emit = defineEmits<{
  pick: [insert: string];

  /** A typed «{{orde» replaced whole, rather than something added at the cursor. */
  complete: [insert: string, from: number, to: number];
}>();

const open = ref(false);
const search = ref('');
const active = ref(0);
const root = ref<HTMLElement | null>(null);
const menu = ref<HTMLElement | null>(null);
const box = ref<HTMLInputElement | null>(null);

/**
 * Where the menu goes, in window coordinates.
 *
 * Teleported to the body and placed by hand, which is worth the arithmetic: the inspector is a
 * column that scrolls, so a menu positioned inside it is clipped by it — a list of twenty
 * references in a 300px column showed four of them and cut the rest off at the panel's edge.
 */
const place = ref({ top: 0, left: 0 });

const WIDTH = 320;
const MARGIN = 8;

/**
 * Whether the list was opened by typing rather than by pressing the button.
 *
 * The difference is where the keys come from and what happens on pick. Typed, the person is still
 * in their own field — so focus stays there, the arrows are read from it, and choosing replaces the
 * half-written reference instead of adding a second one after it.
 */
const typing = ref(false);
const opened = ref({ from: 0, to: 0 });

function position(): void {
  const anchor = typing.value ? props.watching?.() ?? null : root.value?.querySelector('button');
  if (!anchor) return;

  const rect = anchor.getBoundingClientRect();
  const room = window.innerWidth - WIDTH - MARGIN;

  place.value = {
    top: Math.min(rect.bottom + 4, window.innerHeight - 40),

    // Hung from the button's end and pulled back onto the screen if that would leave it. Which
    // edge «end» is depends on the reading direction, and clamping settles both.
    left: Math.max(MARGIN, Math.min(rect.right - WIDTH, room)),
  };
}

const all = computed(() => referenceOptions(props.catalogue));

const shown = computed<ReferenceOption[]>(() => {
  const needle = search.value.trim().toLowerCase();
  if (!needle) return all.value;

  // Every word has to appear somewhere, in any order: "sign token" finds the access token of the
  // step called "Sign in" without anybody having to remember which comes first.
  const words = needle.split(/\s+/);
  return all.value.filter((option) => words.every((word) => option.haystack.includes(word)));
});

/** Grouped for the list, in the order somebody meets them: their own, then the run's. */
const groups = computed(() => {
  const order: ReferenceOption['group'][] = ['environment', 'vars', 'secrets', 'inputs', 'steps', 'run'];
  return order
    .map((group) => ({ group, options: shown.value.filter((option) => option.group === group) }))
    .filter((entry) => entry.options.length > 0);
});

/** The flat order the arrow keys walk, which has to match what the list renders. */
const walk = computed(() => groups.value.flatMap((entry) => entry.options));

async function toggle(): Promise<void> {
  if (props.disabled) return;

  open.value = !open.value;
  if (!open.value) return;

  search.value = '';
  active.value = 0;
  position();

  await nextTick();
  box.value?.focus();

  document.addEventListener('pointerdown', onOutside, true);
  document.addEventListener('keydown', onEscape, true);
  window.addEventListener('resize', position);

  // Captured: the panel this sits in scrolls, and a menu that stayed put while the field it
  // belongs to moved away would be pointing at nothing.
  window.addEventListener('scroll', position, true);
}

function close(): void {
  open.value = false;
  typing.value = false;
  document.removeEventListener('pointerdown', onOutside, true);
  document.removeEventListener('keydown', onEscape, true);
  window.removeEventListener('resize', position);
  window.removeEventListener('scroll', position, true);
}

function onOutside(event: PointerEvent): void {
  const target = event.target as Node;
  if (root.value?.contains(target) || menu.value?.contains(target)) return;
  close();
}

function onEscape(event: KeyboardEvent): void {
  // Captured, so it closes this before anything further out reads the same key.
  if (event.key !== 'Escape') return;
  event.stopPropagation();
  event.preventDefault();
  close();
}

function choose(option: ReferenceOption): void {
  if (typing.value) emit('complete', option.insert, opened.value.from, opened.value.to);
  else emit('pick', option.insert);

  close();
}

function onKey(event: KeyboardEvent): void {
  if (event.key === 'ArrowDown') {
    event.preventDefault();
    active.value = (active.value + 1) % Math.max(walk.value.length, 1);
  }
  else if (event.key === 'ArrowUp') {
    event.preventDefault();
    active.value = (active.value - 1 + walk.value.length) % Math.max(walk.value.length, 1);
  }
  else if (event.key === 'Enter' || event.key === 'Tab') {
    event.preventDefault();
    const option = walk.value[active.value];
    if (option) choose(option);
  }
}

/**
 * Two braces, and the list appears — the thing an editor does.
 *
 * The alternative was a button, which is discoverable and still an interruption: somebody who knows
 * exactly what they want has to stop writing, reach for the mouse, and come back. This is the same
 * list, offered where the words already are.
 */
function onFieldInput(): void {
  const field = props.watching?.();
  if (!field || props.disabled) return;

  const caret = field.selectionStart ?? 0;
  const before = (field.value ?? '').slice(0, caret);

  // The last unclosed «{{» before the cursor, and whatever has been typed since. A closing brace
  // or a second «{{» ends it, because at that point the reference is somebody else's business.
  const match = /\{\{([^{}]*)$/.exec(before);

  if (!match) {
    if (typing.value) close();
    return;
  }

  search.value = match[1] ?? '';
  active.value = 0;
  opened.value = { from: caret - match[0].length, to: caret };

  if (!typing.value) {
    typing.value = true;
    open.value = true;

    document.addEventListener('pointerdown', onOutside, true);
    window.addEventListener('resize', position);
    window.addEventListener('scroll', position, true);
  }

  position();
}

/**
 * The keys, while the list is open and the cursor is still in the field.
 *
 * Read from the field rather than from a search box, because there is no search box in this mode —
 * taking focus away to offer a list would be worse than not offering one.
 */
function onFieldKey(raw: Event): void {
  const event = raw as KeyboardEvent;

  if (!typing.value || !open.value) return;

  if (event.key === 'Escape') {
    event.stopPropagation();
    event.preventDefault();
    close();
    return;
  }

  if (event.key === 'ArrowDown' || event.key === 'ArrowUp'
      || (event.key === 'Enter' && walk.value.length > 0)
      || (event.key === 'Tab' && walk.value.length > 0)) {
    onKey(event);
  }
}

/*
  Listened for at the document, not on the field.

  Attaching to the element on mount looked simpler and did not work anywhere it mattered. In the
  inspector the picker sits inside the label, above the input it belongs to, so when this component
  mounts the parent has not set its ref yet and there is nothing to attach to — the button worked and
  typing did nothing, which is the half that was reported. The same is true of anything that renders
  its fields from a list, and of every re-render that replaces an element.

  Delegation has none of that ordering. It costs two listeners and one identity check per keystroke.
*/
onMounted(() => {
  document.addEventListener('input', onDocumentInput, true);
  document.addEventListener('keydown', onDocumentKey, true);
  document.addEventListener('focusout', onDocumentBlur, true);
});

function isMine(target: EventTarget | null): boolean {
  const field = props.watching?.();
  return field !== null && field !== undefined && target === field;
}

function onDocumentInput(event: Event): void {
  if (isMine(event.target)) onFieldInput();
}

function onDocumentKey(event: Event): void {
  if (isMine(event.target)) onFieldKey(event);
}

function onDocumentBlur(event: Event): void {
  if (isMine(event.target)) onFieldBlur();
}

/**
 * Closes on the way out, but not before a click on the list has been read.
 *
 * A pointer press on an option blurs the field first; closing immediately would remove the option
 * before the click landed on it.
 */
function onFieldBlur(): void {
  window.setTimeout(() => { if (typing.value && !menu.value?.matches(':hover')) close(); }, 150);
}

onBeforeUnmount(() => {
  document.removeEventListener('input', onDocumentInput, true);
  document.removeEventListener('keydown', onDocumentKey, true);
  document.removeEventListener('focusout', onDocumentBlur, true);

  close();
});
</script>

<template>
  <span ref="root" class="reference-picker">
    <button
      type="button"
      class="btn btn-ghost btn-icon btn-sm reference-picker-button"
      :disabled="disabled"
      :aria-expanded="open"
      aria-haspopup="listbox"
      :title="t('reference.insert')"
      :aria-label="t('reference.insertInto', field)"
      @click="toggle"
    >
      <Icon name="braces" />
    </button>

    <Teleport to="body">
      <div
        v-if="open"
        ref="menu"
        class="reference-menu"
        role="dialog"
        :aria-label="t('reference.insert')"
        :style="{ top: `${place.top}px`, left: `${place.left}px` }"
      >
      <input
        v-if="!typing"
        ref="box"
        v-model="search"
        class="input input-sm"
        type="search"
        dir="auto"
        :placeholder="t('reference.search')"
        :aria-label="t('reference.search')"
        @keydown="onKey"
      />

      <p v-else class="reference-typed mono" dir="ltr">{{ `{{${search}` }}</p>

      <p v-if="walk.length === 0" class="reference-empty">{{ t('reference.none') }}</p>

      <ul v-else class="reference-groups" role="listbox" :aria-label="t('reference.insert')">
        <template v-for="entry in groups" :key="entry.group">
          <li class="reference-group" role="presentation">{{ t(`reference.group.${entry.group}`) }}</li>

          <li
            v-for="option in entry.options"
            :key="option.insert"
            class="reference-option"
            :class="{ 'is-active': walk[active]?.insert === option.insert }"
            role="option"
            :aria-selected="walk[active]?.insert === option.insert"
            @mouseenter="active = walk.indexOf(option)"
            @click="choose(option)"
          >
            <span class="reference-option-label" dir="ltr">{{ option.label }}</span>
            <code class="reference-option-insert" dir="ltr">{{ option.insert }}</code>
          </li>
        </template>
      </ul>

          <p class="reference-hint">{{ typing ? t('reference.typedHint') : t('reference.hint') }}</p>
      </div>
    </Teleport>
  </span>
</template>
