import { basename } from 'node:path';
import { z } from 'zod';

interface OpenDialogResult {
  canceled: boolean;
  filePaths: string[];
}

interface SaveDialogResult {
  canceled: boolean;
  filePath?: string;
}

interface FileDialogAdapter {
  showOpenDialog(options: { properties: string[]; filters?: Array<{ name: string; extensions: string[] }> }): Promise<OpenDialogResult>;
}

interface SaveDialogAdapter {
  showSaveDialog(options: { defaultPath: string }): Promise<SaveDialogResult>;
}

const filterSchema = z.object({
  name: z.string().trim().min(1).max(80),
  extensions: z.array(z.string().regex(/^[A-Za-z0-9][A-Za-z0-9_-]{0,15}$/, '文件扩展名格式无效')).min(1).max(20)
}).strict();

const chooseFilesSchema = z.object({
  multiple: z.boolean().optional().default(false),
  filters: z.array(filterSchema).min(1).max(20).optional()
}).strict();

const chooseSavePathSchema = z.object({
  suggestedName: z.string().trim().min(1).max(255)
    .refine((value) => basename(value) === value && !/[\\/:*?"<>|\0]/.test(value), '建议文件名包含不安全字符')
}).strict();

/**
 * 打开应用通用文件选择器。
 * 无参数分支保留分析中心旧版多选诊断包行为；新应用必须显式传入通用选项。
 */
export async function chooseAppFiles(dialog: FileDialogAdapter, payload: unknown): Promise<string[]> {
  const options = payload === undefined
    ? { multiple: true, filters: [{ name: '诊断包', extensions: ['tgz', 'temp', 'zip'] }] }
    : chooseFilesSchema.parse(payload);
  const properties = options.multiple ? ['openFile', 'multiSelections'] : ['openFile'];
  const result = await dialog.showOpenDialog({ properties, ...(options.filters ? { filters: options.filters } : {}) });
  return result.canceled ? [] : result.filePaths;
}

/** 保存选择器只返回路径，不创建或覆盖文件，实际写入由获授权的应用 backend 完成。 */
export async function chooseAppSavePath(dialog: SaveDialogAdapter, payload: unknown): Promise<{ path: string } | null> {
  const value = chooseSavePathSchema.parse(payload);
  const result = await dialog.showSaveDialog({ defaultPath: value.suggestedName });
  return result.canceled || !result.filePath ? null : { path: result.filePath };
}
