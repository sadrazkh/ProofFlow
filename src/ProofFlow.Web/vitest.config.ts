import { defineConfig, mergeConfig } from 'vitest/config';
import viteConfig from './vite.config';

// Kept out of vite.config.ts on purpose: Vite's own `UserConfig` type has no `test` key, so
// declaring it there type-checks only by accident and fails outright under vue-tsc.
export default mergeConfig(
  viteConfig,
  defineConfig({
    test: {
      environment: 'happy-dom',
      include: ['Scripts/**/*.spec.ts'],
      // Installs the real English catalogue, so a component that asks for a key nobody wrote
      // renders the key and the assertion on its text fails.
      setupFiles: ['Scripts/testing/catalogue.ts'],
      globals: true,
    },
  }),
);
