/**
 * Putting text where somebody left the cursor.
 *
 * Used by the reference picker, and the reason it exists as its own function is that «where the
 * cursor was» is not obvious once a menu has been opened: the field lost focus the moment somebody
 * clicked the button. Browsers keep selectionStart on a blurred field, so the position is still
 * there to be read — as long as nothing reassigns the value in between.
 */
export type TextField = HTMLInputElement | HTMLTextAreaElement;

/**
 * Splices text in at the selection and returns the result, leaving the caret after what was added.
 *
 * The element's own value is set as well as returned. Vue owns these fields through :value bindings
 * that only update on change, so the field would otherwise show the old text until it lost focus —
 * and somebody would insert a second reference on top of a line that had not caught up.
 */
export function insertAtCaret(field: TextField, text: string): string {
  const value = field.value ?? '';
  const start = field.selectionStart ?? value.length;
  const end = field.selectionEnd ?? start;

  const next = value.slice(0, start) + text + value.slice(end);

  field.value = next;
  field.focus();

  const caret = start + text.length;
  field.setSelectionRange(caret, caret);

  return next;
}

/**
 * The text field a picker belongs to: the first one inside the same wrapper.
 *
 * A lookup rather than a ref per field, because the inspector renders its fields from a list and
 * the alternative is an array of refs kept in step with it by hand.
 */
export function fieldWithin(root: HTMLElement | null): TextField | null {
  return root?.querySelector<TextField>('input[type="text"], input:not([type]), textarea') ?? null;
}
