import {
  createIcons,
  Activity, ArrowLeft, ArrowRight, ArrowUpRight, Ban, Bell, Boxes, Braces, Bug,
  ArrowDownUp, Binary, BrushCleaning, Cookie, FileCheck, Fingerprint, Flag, Gauge, KeySquare,
  ListCheck, ListOrdered, LogIn, MessageSquare, MousePointerClick, OctagonX, Paperclip, Quote,
  Redo2, Regex, RotateCw, Shuffle, Sigma, SkipForward, Split, Type, Undo2,
  ArrowDownToLine, ChartNoAxesGantt, CircleX, UserPlus, UserMinus, UserCheck,
  CalendarClock, Camera, Check, CheckCheck, ChevronDown, ChevronLeft, ChevronRight, ChevronUp,
  ClipboardPaste,
  ChevronsUpDown,
  CircleAlert, CircleCheck, CircleDot, CircleHelp, CirclePlay, CircleSlash, Clock, Code,
  Copy, Database, Diff, Download, Ellipsis, ExternalLink, Eye, EyeOff, FileJson, FilePlus2,
  Filter, FlaskConical, FolderOpen, GitBranch, GitCompareArrows, Globe, Hash, History,
  Inbox, Info, KeyRound, Languages, LayoutDashboard, LayoutGrid, Lightbulb, Link2, ListChecks, Loader,
  Lock, LogOut, MailCheck, Maximize, Menu, Minus, Monitor, Moon, PanelLeftClose, PanelLeftOpen, Pause, Pencil, Play, Plus,
  RefreshCw, Repeat, RotateCcw, Rows3, Save, Search, Send, Settings, Shield, ShieldAlert, ShieldCheck,
  SlidersHorizontal, Sparkles, Square, SquareStack, Sun, Table2, Tag, Target, Timer,
  TrendingUp, Trash2, TriangleAlert, Upload, User, Users, Variable, WandSparkles, Waypoints,
  Workflow, X, Zap,
} from 'lucide';

/**
 * Icons, imported one by one.
 *
 * Deliberately not the barrel. Pulling in lucide's whole `icons` export takes the bundle from
 * about 140 kB to over 800 kB — several times the weight of the entire application, for a set of
 * glyphs, on every page load. The cost of this approach is that adding an icon to a view means
 * adding it here too, which is a reasonable trade and a visible one.
 */
const used = {
  Activity, ArrowLeft, ArrowRight, ArrowUpRight, Ban, Bell, Boxes, Braces, Bug,
  ArrowDownUp, Binary, BrushCleaning, Cookie, FileCheck, Fingerprint, Flag, Gauge, KeySquare,
  ListCheck, ListOrdered, LogIn, MessageSquare, MousePointerClick, OctagonX, Paperclip, Quote,
  Redo2, Regex, RotateCw, Shuffle, Sigma, SkipForward, Split, Type, Undo2,
  ArrowDownToLine, ChartNoAxesGantt, CircleX, UserPlus, UserMinus, UserCheck,
  CalendarClock, Camera, Check, CheckCheck, ChevronDown, ChevronLeft, ChevronRight, ChevronUp,
  ClipboardPaste,
  ChevronsUpDown,
  CircleAlert, CircleCheck, CircleDot, CircleHelp, CirclePlay, CircleSlash, Clock, Code,
  Copy, Database, Diff, Download, Ellipsis, ExternalLink, Eye, EyeOff, FileJson, FilePlus2,
  Filter, FlaskConical, FolderOpen, GitBranch, GitCompareArrows, Globe, Hash, History,
  Inbox, Info, KeyRound, Languages, LayoutDashboard, LayoutGrid, Lightbulb, Link2, ListChecks, Loader,
  Lock, LogOut, MailCheck, Maximize, Menu, Minus, Monitor, Moon, PanelLeftClose, PanelLeftOpen, Pause, Pencil, Play, Plus,
  RefreshCw, Repeat, RotateCcw, Rows3, Save, Search, Send, Settings, Shield, ShieldAlert, ShieldCheck,
  SlidersHorizontal, Sparkles, Square, SquareStack, Sun, Table2, Tag, Target, Timer,
  TrendingUp, Trash2, TriangleAlert, Upload, User, Users, Variable, WandSparkles, Waypoints,
  Workflow, X, Zap,
};

/** One icon's shape: a list of SVG child elements, as lucide ships them. */
export type IconNode = [string, Record<string, string | number>][];

/**
 * Looks up an icon by the kebab-case name the markup uses.
 *
 * The registry is keyed in PascalCase because that is how lucide exports them, and the markup is
 * kebab because that is how `data-lucide` reads. One conversion, in one place.
 */
export function iconNode(name: string): IconNode | undefined {
  const pascal = name
    .split('-')
    .map((part) => part.charAt(0).toUpperCase() + part.slice(1))
    .join('');

  return (used as Record<string, IconNode>)[pascal];
}

export function renderIcons(): void {
  createIcons({ icons: used, attrs: { 'stroke-width': '1.75' } });
}

/**
 * Keeps icons rendered as markup appears after first paint.
 *
 * The event alone was not enough. It fires when a toast or an island is inserted, but a Vue
 * component that re-renders — a response arriving, a tab changing — produces new `<i data-lucide>`
 * elements with nothing to announce them, and they stay invisible. That is how the failure state
 * of the response viewer shipped with an empty circle where its icon should have been.
 *
 * So: an observer, batched to one pass per frame. A render is cheap; a render per mutation during
 * a list update is not, and Vue produces a great many mutations at once.
 */
export function watchForNewContent(): void {
  renderIcons();
  document.addEventListener('proofflow:content-changed', renderIcons);

  let queued = false;

  const observer = new MutationObserver((records) => {
    if (queued) return;

    const hasPending = records.some((record) =>
      [...record.addedNodes].some((node) =>
        node instanceof Element
        && (node.hasAttribute?.('data-lucide') || node.querySelector?.('[data-lucide]'))));

    if (!hasPending) return;

    queued = true;
    requestAnimationFrame(() => {
      queued = false;
      renderIcons();
    });
  });

  observer.observe(document.body, { childList: true, subtree: true });
}
