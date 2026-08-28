import { describe, expect, it, vi } from 'vitest';

const protocolMock = vi.hoisted(() => ({ registerSchemesAsPrivileged: vi.fn(), handle: vi.fn(), unhandle: vi.fn() }));

vi.mock('electron', () => ({ net: { fetch: vi.fn() }, protocol: protocolMock }));

import { registerAppProtocolScheme, registerAppResourceProtocol, resolveAppResourceFile, resolveAppResourceFileForRead, resolveInstalledAppFile } from '../../src/main/services/app-resource-protocol';

describe('内嵌应用资源协议', () => {
  it('注册标准且安全的受控协议权限', () => {
    registerAppProtocolScheme();

    expect(protocolMock.registerSchemesAsPrivileged).toHaveBeenCalledWith([
      {
        scheme: 'workbench-app',
        privileges: { standard: true, secure: true }
      }
    ]);
  });

  it('把合法 renderer 资源限制在指定应用版本目录内', () => {
    expect(resolveInstalledAppFile('D:/Workbench/apps', 'analysis-center', '1.0.0', 'renderer/index.html')).toBe('D:\\Workbench\\apps\\analysis-center\\1.0.0\\renderer\\index.html');
    expect(resolveInstalledAppFile('D:/Workbench/apps', 'analysis-center', '1.0.0+build.1', 'renderer/index.html')).toContain('1.0.0+build.1');
  });

  it('拒绝资源路径穿越和绝对路径', () => {
    expect(() => resolveInstalledAppFile('D:/Workbench/apps', 'analysis-center', '1.0.0', '../manifest.json')).toThrow('路径');
    expect(() => resolveInstalledAppFile('D:/Workbench/apps', 'analysis-center', '1.0.0', 'C:/Windows/System32/app.dll')).toThrow('路径');
    expect(() => resolveInstalledAppFile('D:/Workbench/apps', 'analysis-center', '1.0.0', 'renderer/../../secret.txt')).toThrow('路径');
  });

  it('仅让显式开发覆盖应用使用 dev 资源路径', () => {
    const override = { appId: 'analysis-center', installPath: 'D:/dev/analysis-center/dist' };

    expect(resolveAppResourceFile('D:/Workbench/apps', override, 'analysis-center', 'dev', 'renderer/index.html')).toBe('D:\\dev\\analysis-center\\dist\\renderer\\index.html');
    expect(() => resolveAppResourceFile('D:/Workbench/apps', override, 'terminal', 'dev', 'renderer/index.html')).toThrow('本地开发应用资源地址无效');
    expect(() => resolveAppResourceFile('D:/Workbench/apps', override, 'analysis-center', 'dev', '../secret.txt')).toThrow('路径');
  });

  it('读取开发资源时拒绝指向 dist 目录外的符号链接', async () => {
    const dist = 'D:/dev/analysis-center/dist';
    const leak = 'D:/dev/analysis-center/dist/renderer/leak.txt';
    const realPaths = new Map([
      [dist, 'D:/real/analysis-center/dist'],
      [leak, 'D:/real/secret.txt']
    ]);

    await expect(resolveAppResourceFileForRead('D:/Workbench/apps', { appId: 'analysis-center', installPath: dist }, 'analysis-center', 'dev', 'renderer/leak.txt', async (path) => realPaths.get(path.replaceAll('\\', '/')) ?? path)).rejects.toThrow('应用资源路径不安全');
  });

  it('协议处理器拒绝未确认安装的开发资源请求', async () => {
    registerAppResourceProtocol('D:/Workbench/apps', { appId: 'analysis-center', installPath: 'D:/dev/analysis-center/dist' }, () => false);
    const handler = protocolMock.handle.mock.calls.at(-1)?.[1] as (request: { url: string }) => Promise<Response>;

    const response = await handler({ url: 'workbench-app://analysis-center/dev/renderer/index.html' });

    expect(response.status).toBe(404);
    await expect(response.text()).resolves.toContain('本地开发应用资源地址无效');
  });
});
