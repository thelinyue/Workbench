import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import { resolve } from 'node:path';

export default defineConfig({
  root: resolve('apps/terminal'),
  // 终端由带版本段的 workbench-app:// 协议加载，资源必须相对入口页解析以保留该版本段。
  base: './',
  plugins: [react()],
  build: { outDir: resolve('apps/terminal/dist'), emptyOutDir: true, rollupOptions: { input: resolve('apps/terminal/renderer/index.html') } }
});
