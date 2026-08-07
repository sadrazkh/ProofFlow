import {
  createIcons,
  Activity, ArrowLeft, ArrowRight, ArrowUpRight, Ban, Bell, Boxes, Braces, Bug,
  CalendarClock, Check, CheckCheck, ChevronDown, ChevronLeft, ChevronRight, ChevronsUpDown,
  CircleAlert, CircleCheck, CircleDot, CircleHelp, CirclePlay, CircleSlash, Clock, Code,
  Copy, Database, Diff, Download, Ellipsis, ExternalLink, Eye, EyeOff, FileJson, FilePlus2,
  Filter, FlaskConical, FolderOpen, GitBranch, GitCompareArrows, Globe, Hash, History,
  Info, KeyRound, Languages, LayoutDashboard, LayoutGrid, Link2, ListChecks, Loader,
  LogOut, Menu, Monitor, Moon, PanelLeftClose, PanelLeftOpen, Pause, Pencil, Play, Plus,
  RefreshCw, Repeat, RotateCcw, Rows3, Save, Search, Send, Settings, Shield, ShieldCheck,
  SlidersHorizontal, Sparkles, Square, SquareStack, Sun, Table2, Tag, Target, Timer,
  TrendingUp, Trash2, TriangleAlert, Upload, User, Users, Variable, Waypoints, Workflow, X, Zap,
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
  CalendarClock, Check, CheckCheck, ChevronDown, ChevronLeft, ChevronRight, ChevronsUpDown,
  CircleAlert, CircleCheck, CircleDot, CircleHelp, CirclePlay, CircleSlash, Clock, Code,
  Copy, Database, Diff, Download, Ellipsis, ExternalLink, Eye, EyeOff, FileJson, FilePlus2,
  Filter, FlaskConical, FolderOpen, GitBranch, GitCompareArrows, Globe, Hash, History,
  Info, KeyRound, Languages, LayoutDashboard, LayoutGrid, Link2, ListChecks, Loader,
  LogOut, Menu, Monitor, Moon, PanelLeftClose, PanelLeftOpen, Pause, Pencil, Play, Plus,
  RefreshCw, Repeat, RotateCcw, Rows3, Save, Search, Send, Settings, Shield, ShieldCheck,
  SlidersHorizontal, Sparkles, Square, SquareStack, Sun, Table2, Tag, Target, Timer,
  TrendingUp, Trash2, TriangleAlert, Upload, User, Users, Variable, Waypoints, Workflow, X, Zap,
};

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
