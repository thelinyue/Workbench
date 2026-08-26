import { readFile } from 'node:fs/promises';
import { describe, expect, it } from 'vitest';

const ipcSource = await readFile(new URL('../../src/main/ipc.ts', import.meta.url), 'utf8');

describe('SSH 终端凭据桥接', () => {
  it('仅向声明 ssh.credentials 能力的应用开放 Windows 凭据库操作', () => {
    expect(ipcSource).toContain("'ssh.credentials': 'ssh.credentials'");
    expect(ipcSource).toContain("method === 'ssh.credentials.read'");
    expect(ipcSource).toContain("method === 'ssh.credentials.write'");
    expect(ipcSource).toContain("method === 'ssh.credentials.delete'");
    expect(ipcSource).toContain("if (appId !== 'ssh-terminal')");
  });
});
