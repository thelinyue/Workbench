import { describe, expect, it, vi } from 'vitest';

const protocolMock = vi.hoisted(() => ({ registerSchemesAsPrivileged: vi.fn() }));

vi.mock('electron', () => ({ net: {}, protocol: protocolMock }));

import { registerAppProtocolScheme, resolveInstalledAppFile } from '../../src/main/services/app-resource-protocol';

describe('内嵌应用资源协议', () => {
  it('注册标准且安全的受控协议权限', () => {
    registerAppProtocolScheme();

    expect(protocolMock.registerSchemesAsPrivileged).toHaveBeenCalledWith([
      {
        scheme: 'workbench-app',
        privileges: { standard: true, secure: true, supportFetchAPI: true, corsEnabled: true }
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
});
