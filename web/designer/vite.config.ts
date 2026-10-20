import { defineConfig } from 'vite';

// 开发期把 /api 代理到 NetBpm.Rest，避免跨域：
//   POST /api/designer/deploy            提交 BPMN XML
//   GET  /api/designer/definition/{id}/xml  回显 BPMN XML
export default defineConfig({
  // 部署在管理宿主的 /designer/ 子路径下，base 必须是 /designer/，否则资源会请求到 /assets/（管理端）
  base: '/designer/',
  server: {
    port: 5273,
    proxy: {
      '/api': {
        // 独立开发时代理到 NetBpm.Admin（引擎 REST + 管理端 /api/admin/models 都在同一宿主）
        target: 'http://localhost:5081',
        changeOrigin: true,
      },
    },
  },
  build: {
    // 由管理宿主统一托管：构建到 wwwroot/designer，供管理控制台 iframe 内嵌
    outDir: '../../src/NetBpm.Admin/wwwroot/designer',
    emptyOutDir: false,
    sourcemap: true,
  },
});
