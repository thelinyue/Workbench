import { describe, expect, it, vi } from 'vitest';
import { importAnalysisCenterFiles } from '../../src/renderer/src/analysis-center-file-import';

describe('桌面诊断包拖放导入', () => {
  it('经受控路径桥接逐个导入，失败不阻断后续文件且不自动分析', async () => {
    const getDroppedFilePaths = vi.fn(() => ['D:/inbox/broken.tgz', 'D:/inbox/valid.tgz']);
    const invoke = vi.fn(async (_appId: string, method: string, payload: unknown) => {
      if (method === 'packages.import' && (payload as { sourcePath: string }).sourcePath === 'D:/inbox/broken.tgz') throw new Error('损坏的诊断包');
      return { id: 'valid-package' };
    });

    const result = await importAnalysisCenterFiles(
      [{ name: 'broken.tgz' }, { name: 'valid.tgz' }] as File[],
      { getDroppedFilePaths, invoke },
      false
    );

    expect(getDroppedFilePaths).toHaveBeenCalledWith([{ name: 'broken.tgz' }, { name: 'valid.tgz' }]);
    expect(invoke).toHaveBeenNthCalledWith(1, 'analysis-center', 'packages.import', { sourcePath: 'D:/inbox/broken.tgz' });
    expect(invoke).toHaveBeenNthCalledWith(2, 'analysis-center', 'packages.import', { sourcePath: 'D:/inbox/valid.tgz' });
    expect(invoke).not.toHaveBeenCalledWith('analysis-center', 'analysis.start', expect.anything());
    expect(result).toEqual({ importedCount: 1, failures: ['broken.tgz：损坏的诊断包'] });
  });
});
