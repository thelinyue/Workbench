import { defineConfig, externalizeDepsPlugin } from 'electron-vite';
import react from '@vitejs/plugin-react';
import { resolve } from 'node:path';

export default defineConfig({
  main: {
    plugins: [externalizeDepsPlugin()],
    build: {
      rollupOptions: {
        input: { index: resolve('src/main/index.ts'), 'analysis-worker': resolve('src/main/analysis/analysis-worker.ts'), 'app-backend-worker': resolve('src/main/services/app-backend-worker.ts') },
        output: { entryFileNames: '[name].js' }
      }
    }
  },
  preload: { plugins: [externalizeDepsPlugin()] },
  renderer: { plugins: [react()] }
});
