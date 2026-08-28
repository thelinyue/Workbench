import { describe, expect, it, vi } from 'vitest';
import { chooseAppFiles, chooseAppSavePath } from '../../src/main/services/app-file-dialog';

describe('应用通用文件选择能力', () => {
  it('无参数调用保持分析中心原有的诊断包多选行为', async () => {
    const showOpenDialog = vi.fn(async () => ({ canceled: false, filePaths: ['D:/one.tgz'] }));

    await expect(chooseAppFiles({ showOpenDialog }, undefined)).resolves.toEqual(['D:/one.tgz']);
    expect(showOpenDialog).toHaveBeenCalledWith({
      properties: ['openFile', 'multiSelections'],
      filters: [{ name: '诊断包', extensions: ['tgz', 'temp', 'zip'] }]
    });
  });

  it('自定义调用校验多选与过滤器，并在取消时返回空数组', async () => {
    const showOpenDialog = vi.fn(async () => ({ canceled: true, filePaths: [] }));

    await expect(chooseAppFiles({ showOpenDialog }, {
      multiple: false,
      filters: [{ name: 'SSH 私钥', extensions: ['pem', 'key', 'ppk'] }]
    })).resolves.toEqual([]);
    expect(showOpenDialog).toHaveBeenCalledWith({
      properties: ['openFile'],
      filters: [{ name: 'SSH 私钥', extensions: ['pem', 'key', 'ppk'] }]
    });
    await expect(chooseAppFiles({ showOpenDialog }, { filters: [{ name: '非法', extensions: ['../exe'] }] })).rejects.toThrow();
  });

  it('保存路径不写文件，取消返回 null 并拒绝目录穿越文件名', async () => {
    const showSaveDialog = vi.fn(async () => ({ canceled: false, filePath: 'D:/downloads/system.log' }));
    await expect(chooseAppSavePath({ showSaveDialog }, { suggestedName: 'system.log' })).resolves.toEqual({ path: 'D:/downloads/system.log' });
    expect(showSaveDialog).toHaveBeenCalledWith({ defaultPath: 'system.log' });

    showSaveDialog.mockResolvedValueOnce({ canceled: true, filePath: '' });
    await expect(chooseAppSavePath({ showSaveDialog }, { suggestedName: 'system.log' })).resolves.toBeNull();
    await expect(chooseAppSavePath({ showSaveDialog }, { suggestedName: '../system.log' })).rejects.toThrow('建议文件名包含不安全字符');
  });
});
