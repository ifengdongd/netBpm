import { existsSync, rmSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

// 构建前清理上一次的前端产物。
// 为什么必须清：vite.config.ts 里 outDir 指到管理宿主的 wwwroot/designer，
// 且在项目根之外，vite 不会自动清空（emptyOutDir: false）—— 不清就会把
// 每次构建的 hash 文件名都留着，发行包装箱时体积按版本累积（每个 6MB 的 .js.map）。
// 该目录完全由本工程生成，整目录删除是安全的。
const here = dirname(fileURLToPath(import.meta.url));
const outDir = resolve(here, '../../../src/NetBpm.Admin/wwwroot/designer');

if (existsSync(outDir)) {
  rmSync(outDir, { recursive: true, force: true });
  console.log(`[clean-dist] removed ${outDir}`);
}
