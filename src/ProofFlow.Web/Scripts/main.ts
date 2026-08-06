import './app.css';

import { initTranslations } from './lib/i18n';
import { mountThemeControls } from './lib/theme';
import { watchForNewContent } from './lib/icons';
import { flushServerToasts } from './lib/toast';
import { mountIslands } from './lib/islands';
import {
  mountSidebar,
  mountMenus,
  mountCommandPalette,
  mountConfirmations,
  mountUnsavedGuard,
} from './lib/shell';

/**
 * The single entry point Vite builds.
 *
 * Order is not arbitrary. Translations come first because everything below can render text; icons
 * come last because toasts and islands both insert markup that needs glyphs, and the icon renderer
 * subscribes to the change event they raise.
 */
initTranslations();
mountThemeControls();
mountSidebar();
mountMenus();
mountCommandPalette();
mountConfirmations();
mountUnsavedGuard();
mountIslands();
flushServerToasts();
watchForNewContent();
