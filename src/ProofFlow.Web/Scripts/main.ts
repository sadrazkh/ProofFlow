import './app.css';

import { initTranslations } from './lib/i18n';
import { reportTimeZone } from './lib/timezone';
import { mountThemeControls } from './lib/theme';
import { watchForNewContent } from './lib/icons';
import { flushServerToasts, mountToastDemos } from './lib/toast';
import { island, mountIslands } from './lib/islands';
import ConnectApi from './islands/ConnectApi.vue';
import RequestLab from './islands/RequestLab.vue';
import BaselineWorkbench from './islands/BaselineWorkbench.vue';
import DataSetEditor from './islands/DataSetEditor.vue';
import EndpointInputsPaste from './islands/EndpointInputsPaste.vue';
import EndpointTest from './islands/EndpointTest.vue';
import ScenarioCanvas from './islands/ScenarioCanvas.vue';
import RunConsole from './islands/RunConsole.vue';
import EnvironmentMatrix from './islands/EnvironmentMatrix.vue';
import CheckBatch from './islands/CheckBatch.vue';
import VersionDiff from './islands/VersionDiff.vue';
import { mountSecretReveal } from './lib/secrets';
import {
  mountSidebar,
  mountMenus,
  mountCommandPalette,
  mountConfirmations,
  mountUnsavedGuard,
  mountCronPresets,
  mountCopyButtons,
  mountCountdowns,
  mountDemoFill,
  mountNavGroups,
  mountBusyForms,
  mountUploadProgress,
  mountRenames,
  mountBulkSelect,
} from './lib/shell';

/**
 * The single entry point Vite builds.
 *
 * Order is not arbitrary. Translations come first because everything below can render text; icons
 * come last because toasts and islands both insert markup that needs glyphs, and the icon renderer
 * subscribes to the change event they raise.
 */
initTranslations();
reportTimeZone();
mountThemeControls();
mountSidebar();
mountMenus();
mountCommandPalette();
mountConfirmations();
mountUnsavedGuard();
mountCronPresets();
mountCopyButtons();
mountCountdowns();
mountRenames();
mountDemoFill();
mountNavGroups();
mountUploadProgress();
mountBusyForms();
mountBulkSelect();
// Registered before mounting, which is the only ordering that matters here.
island('connect-api', ConnectApi);
island('request-lab', RequestLab);
island('baseline-workbench', BaselineWorkbench);
island('dataset-editor', DataSetEditor);
island('endpoint-inputs-paste', EndpointInputsPaste);
// The review queue is no longer an island of its own: it is what the test section renders once a
// test has run, and reaching it meant knowing which capture session to open.
island('endpoint-test', EndpointTest);
island('scenario-canvas', ScenarioCanvas);
island('run-console', RunConsole);
island('environment-matrix', EnvironmentMatrix);
island('check-batch', CheckBatch);
// One per waiting row on the approval inbox, rather than one island owning that table: the table
// and its forms have to keep working when the bundle does not, and the diff is the only part of it
// that is fetched rather than rendered. The reasoning is written out in the component.
island('version-diff', VersionDiff);

mountIslands();
mountSecretReveal();
mountToastDemos();
flushServerToasts();
watchForNewContent();
