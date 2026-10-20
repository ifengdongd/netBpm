import react from '@vitejs/plugin-react';
import { defineConfig } from 'vite';

// 开发期把 /api 代理到 NetBpm.Admin 宿主（引擎 REST + 管理端端点都在同一个宿主）。
// 生产构建直接输出到 src/NetBpm.Admin/wwwroot，由该宿主的 UseStaticFiles 托管。
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5274,
    proxy: {
      '/api': {
        target: 'http://localhost:5081',
        changeOrigin: true,
      },
    },
  },
  build: {
    // 与设计器共同落在管理宿主 wwwroot：设计器先构建到 wwwroot/designer，
    // 本构建 emptyOutDir 关闭，避免清掉设计器产物；
    // 旧哈希文件由 scripts/clean-dist.mjs 在构建前清理（仅清 assets 与 index.html）。
    outDir: '../../src/NetBpm.Admin/wwwroot',
    emptyOutDir: false,
    sourcemap: true,
  },
});
