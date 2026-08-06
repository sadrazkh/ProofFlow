import { defineConfig } from 'vite';
import vue from '@vitejs/plugin-vue';
import { resolve } from 'node:path';

// Vite compiles the Vue islands and the design system straight into wwwroot/build with a manifest.
// Razor reads that manifest (ViteManifest.cs) to emit the hashed asset URLs. That is the whole
// "embedded Vue" story: one origin, one deployable, no Node process in production.
export default defineConfig({
  plugins: [vue()],
  base: '/build/',
  resolve: {
    alias: { '@': resolve(import.meta.dirname, 'Scripts') },
  },
  build: {
    // The manifest goes to build/manifest.json, not Vite's default .vite/manifest.json: the .NET
    // SDK excludes dot-directories from `dotnet publish`, which drops the manifest out of the
    // published output and leaves the application with no CSS or JS links and no error to explain it.
    manifest: 'manifest.json',
    outDir: resolve(import.meta.dirname, 'wwwroot/build'),
    emptyOutDir: true,
    // Source maps in production too. The bundle is served from our own origin to our own users,
    // and a stack trace nobody can read is a bug report nobody can act on.
    sourcemap: true,
    rollupOptions: {
      // Relative, so the manifest key is "Scripts/main.ts" — what ViteManifest.Resolve asks for.
      input: 'Scripts/main.ts',
    },
  },
  server: {
    port: 5173,
    strictPort: true,
    cors: true,
  },
});
