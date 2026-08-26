import { describe, expect, it, vi } from 'vitest';

const electronMock = vi.hoisted(() => ({
  app: {
    whenReady: vi.fn(() => new Promise<void>(() => undefined)),
    on: vi.fn()
  },
  protocol: { registerSchemesAsPrivileged: vi.fn() },
  BrowserWindow: class {},
  Menu: { setApplicationMenu: vi.fn() }
}));

vi.mock('electron', () => electronMock);
vi.mock('../../src/main/ipc', () => ({ registerWorkbenchIpc: vi.fn() }));
vi.mock('../../src/main/services/app-resource-protocol', async (importOriginal) => ({
  ...await importOriginal(),
  registerAppResourceProtocol: vi.fn()
}));

describe('工作台应用协议权限', () => {
  it('在 app ready 前将 workbench-app 注册为标准且安全的协议', async () => {
    await import('../../src/main/index');

    expect(electronMock.protocol.registerSchemesAsPrivileged.mock.invocationCallOrder[0])
      .toBeLessThan(electronMock.app.whenReady.mock.invocationCallOrder[0]);
    expect(electronMock.protocol.registerSchemesAsPrivileged).toHaveBeenCalledWith(expect.arrayContaining([
      expect.objectContaining({
        scheme: 'workbench-app',
        privileges: expect.objectContaining({ standard: true, secure: true })
      })
    ]));
  });
});
