import { readFileSync } from 'node:fs';
import { createPublicKey, type KeyObject } from 'node:crypto';
import { join } from 'node:path';

/**
 * 读取应用发布者公钥。
 *
 * 公钥只允许来自部署明确配置的 JSON 文件或环境变量，安装器没有“开发模式跳过签名”的后门；
 * 未配置受信公钥时，目录仍可浏览，但任何应用包都会被明确拒绝安装。
 */
export function loadTrustedAppKeys(): Record<string, KeyObject> {
  const configuredFilePath = process.env.HEPHAESTUS_APP_TRUSTED_KEYS_FILE;
  const inline = process.env.HEPHAESTUS_APP_TRUSTED_KEYS_JSON;
  const defaultFilePath = join(process.resourcesPath, 'app-trusted-keys.json');
  const developmentFilePath = join(process.cwd(), 'src', 'main', 'config', 'app-trusted-keys.json');
  const filePath = configuredFilePath ?? (!inline ? (tryRead(defaultFilePath) ? defaultFilePath : tryRead(developmentFilePath) ? developmentFilePath : undefined) : undefined);
  if (!filePath && !inline) return {};
  let value: unknown;
  try {
    value = JSON.parse(filePath ? readFileSync(filePath, 'utf8') : inline!);
  } catch (error) {
    throw new Error(`应用信任公钥配置无法读取：${error instanceof Error ? error.message : String(error)}`);
  }
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new Error('应用信任公钥配置必须是 keyId 到 PEM 公钥的 JSON 对象');
  const keys: Record<string, KeyObject> = {};
  for (const [keyId, pem] of Object.entries(value)) {
    if (typeof pem !== 'string' || !pem.trim()) throw new Error(`应用信任公钥无效：${keyId}`);
    try { keys[keyId] = createPublicKey(pem); }
    catch (error) { throw new Error(`应用信任公钥无效：${keyId}，${error instanceof Error ? error.message : String(error)}`); }
  }
  return keys;
}

function tryRead(path: string): boolean {
  try { readFileSync(path); return true; } catch { return false; }
}
