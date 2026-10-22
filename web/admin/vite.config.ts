import react from '@vitejs/plugin-react';
import { defineConfig } from 'vitest/config';

// 开发期把 /api 代理到 NetBpm.Admin 宿主（引擎 REST + 管理端端点都在同一个宿主）。
//
// 端口别指错：Rest=5080 只有 designer / runtime / identity，管理端点（/api/admin/**，
// 如定时作业 /api/admin/jobs）只在 Admin=5081 提供。指到 5080 时管理页会 404。
//
// 目标地址默认 http://localhost:5081，可用环境变量 NETBPM_API_TARGET 覆盖，例如：
//   NETBPM_API_TARGET=http://localhost:6001 npm run dev
// 生产构建直接输出到 src/NetBpm.Admin/wwwroot，由该宿主的 UseStaticFiles 托管。
const apiTarget = process.env.NETBPM_API_TARGET ?? 'http://localhost:5081';

// 部署前缀三档：
//   · 独立部署（NetBpm.Admin 自己监听 5081）→ `/`（npm run build）
//   · 由 OPMS 反代到 /netbpm/            → `/netbpm/`（npm run build:opms）
//   · 特殊环境                          → NETBPM_WEB_BASE 显式覆盖（优先级最高）
// 路由 basename、资源路径、接口前缀都由它派生（见 src/api/base.ts），三者不会走散。
export default defineConfig(({ mode }) => {
  const webBase = process.env.NETBPM_WEB_BASE ?? (mode === 'opms' ? '/netbpm/' : '/');

  return {
    base: webBase,
    plugins: [react()],
    server: {
      port: 5274,
      proxy: {
        '/api': {
          target: apiTarget,
          changeOrigin: true,
        },
      },
    },
    // vitest 单元测试：jsdom 环境（auth.ts 依赖 localStorage；usePagedList 用 renderHook）
    test: {
      environment: 'jsdom',
      include: ['src/**/*.test.{ts,tsx}'],
    },
    build: {
      // 与设计器共同落在管理宿主 wwwroot：设计器先构建到 wwwroot/designer，
      // 本构建 emptyOutDir 关闭，避免清掉设计器产物；
      // 旧哈希文件由 scripts/clean-dist.mjs 在构建前清理（仅清 assets 与 index.html）。
      outDir: '../../src/NetBpm.Admin/wwwroot',
      emptyOutDir: false,
      sourcemap: true,
      rollupOptions: {
        output: {
          // 大依赖拆为独立 chunk：框架与 UI 库变更少，利于浏览器长缓存
          manualChunks: {
            react: ['react', 'react-dom', 'react-router-dom'],
            antd: ['antd'],
            icons: ['@ant-design/icons'],
          },
        },
      },
    },
  };
});
