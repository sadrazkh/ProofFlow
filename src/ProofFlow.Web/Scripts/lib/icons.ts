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
 * Re-rendered whenever markup appears after first paint — a toast, a Vue island, a fetched
 * fragment. Without it those elements keep their `<i data-lucide>` placeholder and show nothing.
 */
export function watchForNewContent(): void {
  renderIcons();
  document.addEventListener('proofflow:content-changed', renderIcons);
}
