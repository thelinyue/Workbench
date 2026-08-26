import { describe, expect, it, vi } from 'vitest';
import { AppHostClient } from '../../apps/terminal/renderer/host-api';

describe('SSH 终端宿主事件桥接', () => {
  it('把宿主完整事件对象传递给终端监听器', () => {
    let messageListener: ((event: MessageEvent) => void) | undefined;
    const parent = {};
    vi.stubGlobal('window', {
      parent,
      addEventListener: (_type: string, listener: (event: MessageEvent) => void) => { messageListener = listener; }
    });
    const client = new AppHostClient();
    const listener = vi.fn();
    const event = { appId: 'terminal', event: 'session.data', payload: { id: 'session-1', data: 'ready' } };

    client.onEvent(listener);
    messageListener?.({ source: parent, data: { type: 'workbench-app-event', event } } as MessageEvent);

    expect(listener).toHaveBeenCalledWith(event);
    vi.unstubAllGlobals();
  });
});
