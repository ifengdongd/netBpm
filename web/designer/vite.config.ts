import { defineConfig } from 'vite';

// 开发期把 /api 代理到后端，避免跨域：
//   POST /api/designer/deploy            提交 BPMN XML
//   GET  /api/designer/definition/{id}/xml  回显 BPMN XML
//   GET  /api/admin/models               供「从已有模型导入」下拉（管理端点）
//
// 端口别指错：Rest=5080 只有 designer / runtime / identity，管理端点（/api/admin/**）
// 只在 Admin=5081 提供；需要 /api/admin/models 时目标必须是 5081。
export default defineConfig(({ mode }) => ({
  // 默认部署在管理宿主的 /designer/ 子路径下，base 必须是 /designer/，否则资源会请求到 /assets/（管理端）。
  // 被 OPMS 反代到 /netbpm/ 时用 build:opms（base=/netbpm/designer/）；特殊环境用 NETBPM_DESIGNER_BASE 覆盖。
  base: process.env.NETBPM_DESIGNER_BASE ?? (mode === 'opms' ? '/netbpm/designer/' : '/designer/'),
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
}));
