import { defineConfig } from 'vite';

// 开发期把 /api 代理到 NetBpm.Rest，避免跨域：
//   POST /api/designer/deploy            提交 BPMN XML
//   GET  /api/designer/definition/{id}/xml  回显 BPMN XML
export default defineConfig({
  server: {
    port: 5273,
    proxy: {
      '/api': {
        target: 'http://localhost:5080',
        changeOrigin: true,
      },
    },
  },
  build: {
    outDir: 'dist',
    sourcemap: true,
  },
});
