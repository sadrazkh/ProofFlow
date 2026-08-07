import { h, type FunctionalComponent } from 'vue';
import { iconNode } from './icons';

/**
 * An icon inside a Vue island.
 *
 * It exists because the alternative broke reactivity in a way that took a long time to see.
 * `<i data-lucide="…">` works in Razor, where the markup is static: lucide finds the placeholder
 * and swaps in an `<svg>`. Inside a component it is poison — Vue created that `<i>` and holds a
 * reference to it, lucide replaces the node, and the next patch tries to update or remove an
 * element that is no longer in the document.
 *
 * The symptom is not a missing icon. It is Vue throwing mid-patch, which abandons the rest of that
 * render: a side-by-side toggle whose button turns on while the rows it controls never change,
 * because the class on the root element is applied after the children and the children threw.
 *
 * So the icon is built as real vnodes from lucide's data and Vue owns every node in it. Nothing
 * mutates the DOM behind its back, and no `v-html` is involved either.
 */
export const Icon: FunctionalComponent<{ name: string; size?: number }> = (props) => {
  const node = iconNode(props.name);

  if (!node) {
    // Loud rather than blank: an unregistered name should be visible in review, not swallowed.
    console.warn(`Icon "${props.name}" is not registered in lib/icons.ts.`);
    return null;
  }

  const size = props.size ?? 24;

  return h(
    'svg',
    {
      xmlns: 'http://www.w3.org/2000/svg',
      width: size,
      height: size,
      viewBox: '0 0 24 24',
      fill: 'none',
      stroke: 'currentColor',
      'stroke-width': 1.75,
      'stroke-linecap': 'round',
      'stroke-linejoin': 'round',
      'aria-hidden': 'true',
      class: 'lucide',
    },
    node.map(([tag, attrs]) => h(tag, attrs)),
  );
};

Icon.props = { name: { type: String, required: true }, size: { type: Number, required: false } };
