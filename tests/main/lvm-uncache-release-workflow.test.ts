import { readFile } from 'node:fs/promises';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';

describe('LVM 应用发布工作流', () => {
  it('使用应用仓库专用 Token，并拒绝覆盖内容不一致的既有 Release', async () => {
    const workflow = await readFile(resolve(process.cwd(), '.github/workflows/lvm-uncache-tool-release.yml'), 'utf8');

    expect(workflow).toContain('GH_TOKEN: ${{ secrets.APPS_RELEASES_TOKEN }}');
    expect(workflow).not.toContain('secrets.RELEASES_TOKEN');
    expect(workflow).toContain('Get-FileHash');
    expect(workflow).toContain('应用 Release 已存在但内容不一致');
  });
});
