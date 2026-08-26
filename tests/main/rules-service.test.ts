import { mkdtemp, readFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';
import { RulesService } from '../../src/main/services/rules-service';

const official = {
  tgz: { version: 'official-1', files: [{ name: 'syslog', category: '系统日志', keywords: [{ term: 'ERROR', result: '系统错误' }] }] },
  zip: { version: 'official-1', files: [] }
};

describe('RulesService', () => {
  it('保存用户规则后生成可供分析中心使用的 active 规则快照', async () => {
    const root = await mkdtemp(join(tmpdir(), 'workbench-rules-'));
    const service = new RulesService({ rootDirectory: root, officialRules: official });

    const result = await service.invoke('rules.saveUserRules', {
      user: { schemaVersion: 1, baseVersion: 'official-1', categories: ['网络'], rules: [{ localId: 'r1', file: 'syslog', category: '网络', status: 'draft', rule: { term: 'timeout', result: '连接超时', regex: false, context_lines: 1, context_direction: 'down', search_direction: 'down' } }] }
    }) as { type: string; data: unknown };

    expect(result.type).toBe('saveSucceeded');
    const active = JSON.parse(await readFile(join(root, 'Active', 'active.json'), 'utf8')) as typeof official;
    expect(active.tgz.files[0]?.keywords).toHaveLength(2);
  });

  it('拒绝无效正则并返回中文校验问题', async () => {
    const root = await mkdtemp(join(tmpdir(), 'workbench-rules-'));
    const service = new RulesService({ rootDirectory: root, officialRules: official });
    const result = await service.invoke('rules.validateUserRules', { user: { schemaVersion: 1, categories: [], rules: [{ localId: 'r1', file: 'syslog', category: '网络', status: 'draft', rule: { term: '[', result: '无效', regex: true } }] } }) as { type: string; data: { issues: Array<{ severity: string }> } };

    expect(result.type).toBe('validationResult');
    expect(result.data.issues[0]?.severity).toBe('error');
  });
});
