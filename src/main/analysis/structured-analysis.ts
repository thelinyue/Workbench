import { readFile, readdir, stat } from 'node:fs/promises';
import { basename, join, relative } from 'node:path';
import { gunzipSync } from 'node:zlib';
import type { DiagnosticPackageFormat } from '../domain/diagnostic-package';
import type { AnalysisResult } from './log-analyzer';

export type HealthLevel = 'critical' | 'attention' | 'normal' | 'unknown';
export interface SmartAttribute { id: number; name: string; value: string; raw: string; status: string; }
export interface StorageDisk { device: string; model: string; serial: string; health: HealthLevel; smart: SmartAttribute[]; evidence: string[]; }
export interface StructuredAnalysis { sysInfo: Record<string, unknown>; memory: string[]; blockDevicesRaw: string; blockDevices: string[]; networks: string[]; raids: string[]; volumes: string[]; disks: StorageDisk[]; evidence: string[]; overallHealth: HealthLevel; recommendations: string[]; customerReply: string; }

/** 完整引擎的结构化阶段：将 sysinfo、存储快照和共享日志归并为可报告的数据模型。 */
export async function analyzeStructuredExtract(root: string, rules: AnalysisResult, archiveFormat: DiagnosticPackageFormat = 'tgz'): Promise<StructuredAnalysis> {
  const paths = await listFiles(root);
  const sysinfoPath = paths.find((path) => basename(path).toLowerCase() === 'sysinfo.json');
  const sysInfo = sysinfoPath ? await parseJson(sysinfoPath) : {};
  const textFiles = await Promise.all(paths.filter((path) => !path.endsWith('.json')).map(async (path) => ({ path, content: await readText(path, archiveFormat) })));
  const byName = (name: string) => textFiles.filter((item) => basename(item.path).toLowerCase().startsWith(name)).map((item) => item.content).join('\n');
  const blockDevicesRaw = byName('lsblk');
  const blockDevices = blockDevicesRaw.split(/\r?\n/).filter((line) => /^(NAME|\S+\s)/.test(line)).slice(0, 500);
  const networks = [byName('ifconfig'), byName('ip_addr')].join('\n').split(/\r?\n/).filter((line) => /^(\w|\d+: )/.test(line) || /inet /.test(line)).slice(0, 500);
  const raids = [byName('mdstat'), byName('mdadm')].join('\n').split(/\r?\n/).filter((line) => /^(md\d+|.*\[.*\])/.test(line)).slice(0, 200);
  const volumes = [byName('lvs'), byName('blkid'), byName('storage_serv')].join('\n').split(/\r?\n/).filter((line) => /\/dev\/|vg|lv|volume/i.test(line)).slice(0, 300);
  const logs = textFiles.filter((item) => isDiagnosticLogFile(basename(item.path), archiveFormat)).map((item) => item.content).join('\n');
  const evidence = logs.split(/\r?\n/).filter((line) => /I\/O error|Device not ready; aborting reset|Removing after probe failure|medium error|uncorrectable|hard resetting|read-only|EXT4-fs.*error|BTRFS error|No space left|SMART.*critical/i.test(line)).slice(0, 300);
  const disks = extractDisks(sysInfo, [byName('smartctl'), byName('nvme'), byName('smart')].join('\n'), evidence);
  const memory = extractMemory(sysInfo, byName('dmidecode'));
  const critical = evidence.some((line) => /I\/O error|medium error|uncorrectable|read-only|No space left/i.test(line)) || disks.some((disk) => disk.health === 'critical');
  const attention = !critical && (evidence.length > 0 || disks.some((disk) => disk.health === 'attention'));
  const overallHealth: HealthLevel = critical ? 'critical' : attention ? 'attention' : disks.length || rules.files.length ? 'normal' : 'unknown';
  const recommendations = critical ? ['立即备份关键数据。', '检查故障磁盘、线缆与 RAID 状态。', '请勿在未确认前执行会写入磁盘的修复操作。'] : attention ? ['持续观察日志与 SMART 变化。', '建议安排现场检查存储连接。'] : ['未发现明确存储故障，请结合现场状态继续观察。'];
  return { sysInfo, memory, blockDevicesRaw, blockDevices, networks, raids, volumes, disks, evidence, overallHealth, recommendations, customerReply: critical ? '检测到存储相关异常，建议尽快备份数据并安排工程师检查。' : '当前未检测到需要立即处理的严重存储风险。' };
}

async function listFiles(directory: string): Promise<string[]> { const entries = await readdir(directory, { withFileTypes: true }); const result: string[] = []; for (const entry of entries) { const path = join(directory, entry.name); if (entry.isDirectory() && entry.name !== 'Report') result.push(...await listFiles(path)); else if (entry.isFile() && (await stat(path)).size < 64 * 1024 * 1024) result.push(path); } return result.sort((a,b) => a.localeCompare(b)); }
function isDiagnosticLogFile(fileName: string, archiveFormat: DiagnosticPackageFormat): boolean {
  return archiveFormat === 'zip'
    ? /(?:.+_syslog|.+_dmsg\.log\.gz|nas_storage\.log\.\d+)$/i.test(fileName)
    : /^(kern|syslog|journal|dmesg)/i.test(fileName);
}
async function readText(path: string, archiveFormat: DiagnosticPackageFormat): Promise<string> { try { const content = await readFile(path); return archiveFormat === 'zip' && path.toLowerCase().endsWith('.gz') ? gunzipSync(content).toString('utf8') : content.toString('utf8'); } catch { return ''; } }
async function parseJson(path: string): Promise<Record<string, unknown>> { try { return JSON.parse(await readFile(path, 'utf8')) as Record<string, unknown>; } catch { return {}; } }
function extractMemory(info: Record<string, unknown>, dmi: string): string[] { const nested = JSON.stringify(info); const fromJson = [...nested.matchAll(/"(size|memory_size)"\s*:\s*"?([^",}]+)/gi)].map((match) => match[2]); const fromDmi = dmi.split(/\r?\n/).filter((line) => /^\s*Size:/i.test(line)).map((line) => line.trim()); return [...new Set([...fromJson, ...fromDmi])].slice(0, 64); }
function extractDisks(info: Record<string, unknown>, smart: string, evidence: string[]): StorageDisk[] { const json = JSON.stringify(info); const devices = [...json.matchAll(/"(?:dev_name|device|name)"\s*:\s*"([^"/]*(?:\/dev\/)?[^",}]*)"/gi)].map((m) => m[1]).filter((value) => /sd|nvme|disk/i.test(value)); const unique = [...new Set(devices)].slice(0, 32); return unique.map((device) => { const related = evidence.filter((line) => line.includes(device.replace('/dev/', ''))); const raw = [...smart.matchAll(/(?:ID#|\bid\b)\s*[:#]?\s*(\d+)\s+([^\n]+)/gi)].slice(0, 64).map((m) => ({ id: Number(m[1]), name: m[2].trim().slice(0, 80), value: '', raw: m[2].trim().slice(-40), status: /fail|critical|warning/i.test(m[2]) ? '异常' : '正常' })); const critical = related.some((line) => /I\/O error|medium error|uncorrectable/i.test(line)); return { device, model: '', serial: '', health: critical ? 'critical' : related.length ? 'attention' : 'normal', smart: raw, evidence: related }; }); }
