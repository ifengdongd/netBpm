import { existsSync, rmSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

// 构建前清理上一次的前端产物。
// 只清理本工程生成的 wwwroot/assets 与 wwwroot/index.html，
// 保留 wwwroot/designer（由 web/designer 单独构建后落盘）。
const here = dirname(fileURLToPath(import.meta.url));
const wwwroot = resolve(here, '../../../src/NetBpm.Admin/wwwroot');

const targets = [resolve(wwwroot, 'assets'), resolve(wwwroot, 'index.html')];

for (const target of targets) {
  if (existsSync(target)) {
    rmSync(target, { recursive: true, force: true });
    console.log(`[clean-dist] removed ${target}`);
  }
}
