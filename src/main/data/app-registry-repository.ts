import { mkdirSync } from 'node:fs';
import { dirname } from 'node:path';
import { DatabaseSync } from 'node:sqlite';
import type { AppCatalogSnapshot, AppInstallRecord, AppInstallState } from '../../shared/app-contract';

/**
 * 应用中心自己的 SQLite 仓储。
 *
 * 它只保存应用安装状态和最后一次有效目录缓存，不读取工作台旧的诊断数据库，
 * 从存储层面保证分析中心可以独立维护自己的数据生命周期。
 */
export class AppRegistryRepository {
  private readonly database: DatabaseSync;

  public constructor(databasePath: string) {
    mkdirSync(dirname(databasePath), { recursive: true });
    this.database = new DatabaseSync(databasePath);
    this.createSchema();
  }

  public close(): void { this.database.close(); }

  public list(): AppInstallRecord[] {
    const rows = this.database.prepare(`
      SELECT id, name, description, publisher_id AS publisherId, installed_version AS installedVersion,
        available_version AS availableVersion, active_version AS activeVersion, install_path AS installPath,
        enabled, state, error_message AS errorMessage
      FROM installed_apps ORDER BY id
    `).all() as unknown as Array<Record<string, string | number | null>>;
    return rows.map(toRecord);
  }

  public get(id: string): AppInstallRecord | undefined {
    return this.list().find((item) => item.id === id);
  }

  public upsert(record: AppInstallRecord): void {
    this.database.prepare(`
      INSERT INTO installed_apps (
        id, name, description, publisher_id, installed_version, available_version,
        active_version, install_path, enabled, state, error_message
      ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
      ON CONFLICT(id) DO UPDATE SET
        name = excluded.name, description = excluded.description, publisher_id = excluded.publisher_id,
        installed_version = excluded.installed_version, available_version = excluded.available_version,
        active_version = excluded.active_version, install_path = excluded.install_path,
        enabled = excluded.enabled, state = excluded.state, error_message = excluded.error_message
    `).run(
      record.id,
      record.name,
      record.description,
      record.publisherId,
      record.installedVersion ?? null,
      record.availableVersion ?? null,
      record.activeVersion ?? null,
      record.installPath ?? null,
      record.enabled === false ? 0 : 1,
      record.state,
      record.errorMessage ?? null
    );
  }

  /** 更新应用启用状态，SQLite 中只允许写入严格的 0/1 整数。 */
  public setEnabled(id: string, enabled: boolean): void {
    this.database.prepare('UPDATE installed_apps SET enabled = ? WHERE id = ?').run(enabled ? 1 : 0, id);
  }

  /** 删除应用注册记录；应用文件由上层卸载流程按策略清理。 */
  public remove(id: string): void {
    this.database.prepare('DELETE FROM installed_apps WHERE id = ?').run(id);
  }

  public saveCatalogSnapshot(snapshot: AppCatalogSnapshot): void {
    this.database.prepare(`
      INSERT INTO catalog_cache (id, catalog_json, fetched_at, from_cache, warning)
      VALUES (1, ?, ?, ?, ?)
      ON CONFLICT(id) DO UPDATE SET catalog_json = excluded.catalog_json,
        fetched_at = excluded.fetched_at, from_cache = excluded.from_cache, warning = excluded.warning
    `).run(JSON.stringify(snapshot.catalog), snapshot.fetchedAt, snapshot.fromCache ? 1 : 0, snapshot.warning ?? null);
  }

  public loadCatalogSnapshot(): AppCatalogSnapshot | undefined {
    const row = this.database.prepare('SELECT catalog_json AS catalog, fetched_at AS fetchedAt, from_cache AS fromCache, warning FROM catalog_cache WHERE id = 1').get() as { catalog: string; fetchedAt: string; fromCache: number; warning?: string | null } | undefined;
    if (!row) return undefined;
    return { catalog: JSON.parse(row.catalog), fetchedAt: row.fetchedAt, fromCache: row.fromCache === 1, warning: row.warning ?? undefined };
  }

  private createSchema(): void {
    this.database.exec(`
      CREATE TABLE IF NOT EXISTS installed_apps (
        id TEXT PRIMARY KEY,
        name TEXT NOT NULL,
        description TEXT NOT NULL,
        publisher_id TEXT NOT NULL,
        installed_version TEXT,
        available_version TEXT,
        active_version TEXT,
        install_path TEXT,
        enabled INTEGER NOT NULL DEFAULT 1,
        state TEXT NOT NULL,
        error_message TEXT
      );
      CREATE TABLE IF NOT EXISTS catalog_cache (
        id INTEGER PRIMARY KEY CHECK (id = 1),
        catalog_json TEXT NOT NULL,
        fetched_at TEXT NOT NULL,
        from_cache INTEGER NOT NULL,
        warning TEXT
      );
    `);
    const columns = this.database.prepare('PRAGMA table_info(installed_apps)').all() as Array<{ name?: unknown }>;
    if (!columns.some((column) => column.name === 'enabled')) {
      this.database.exec('ALTER TABLE installed_apps ADD COLUMN enabled INTEGER NOT NULL DEFAULT 1');
    }
  }
}

function toRecord(row: Record<string, string | number | null>): AppInstallRecord {
  return {
    id: String(row.id),
    name: String(row.name),
    description: String(row.description),
    publisherId: String(row.publisherId),
    installedVersion: row.installedVersion == null ? undefined : String(row.installedVersion),
    availableVersion: row.availableVersion == null ? undefined : String(row.availableVersion),
    activeVersion: row.activeVersion == null ? undefined : String(row.activeVersion),
    installPath: row.installPath == null ? undefined : String(row.installPath),
    enabled: row.enabled === 1,
    state: String(row.state) as AppInstallState,
    errorMessage: row.errorMessage == null ? undefined : String(row.errorMessage)
  };
}
