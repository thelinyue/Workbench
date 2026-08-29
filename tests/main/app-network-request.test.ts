import { describe, expect, it, vi } from 'vitest';

const electronFetch = vi.hoisted(() => vi.fn());
vi.mock('electron', () => ({ net: { fetch: electronFetch } }));

import { requestAppResource } from '../../src/main/services/app-network-request';

describe('应用中心 Electron 网络请求适配器', () => {
  it('使用 Electron 网络栈并透传请求选项和响应内容', async () => {
    const bytes = new Uint8Array([1, 2, 3]);
    electronFetch.mockResolvedValue({
      ok: true,
      status: 200,
      text: async () => 'response',
      arrayBuffer: async () => bytes.buffer
    });

    const response = await requestAppResource('https://example.test/app.zip', { redirect: 'follow' });

    expect(electronFetch).toHaveBeenCalledWith('https://example.test/app.zip', { redirect: 'follow' });
    expect(response.ok).toBe(true);
    expect(response.status).toBe(200);
    await expect(response.text()).resolves.toBe('response');
    await expect(response.arrayBuffer()).resolves.toEqual(bytes.buffer);
  });
});
