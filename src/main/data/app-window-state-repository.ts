import { mkdirSync } from 'node:fs';
import { dirname } from 'node:path';
import { DatabaseSync } from 'node:sqlite';

export interface AppWindowState {
  appId: string;
  windowKey: string;
  x: number;
  y: number;
  width: number;
  height: number;
  maximized: boolean;
}

/**
 * 应用原生窗口的宿主展示状态仓储。
 *
 * 该表与桌面布局共用 workbench.db，但只记录窗口位置、普通尺寸和最大化状态；
 * 应用业务数据始终由应用自身 backend 管理，不能借此仓储跨越宿主与应用的数据边界。
 */
export class AppWindowStateRepository {
  private readonly database: DatabaseSync;

  public constructor(databasePath: string) {
    mkdirSync(dirname(databasePath), { recursive: true });
    this.database = new DatabaseSync(databasePath);
    this.database.exec(`
      CREATE TABLE IF NOT EXISTS app_window_state (
        app_id TEXT NOT NULL,
        window_key TEXT NOT NULL,
        x INTEGER NOT NULL,
        y INTEGER NOT NULL,
        width INTEGER NOT NULL,
        height INTEGER NOT NULL,
        maximized INTEGER NOT NULL,
        PRIMARY KEY (app_id, window_key)
      );
      CREATE TABLE IF NOT EXISTS app_window_state_migrations (
        migration_id TEXT PRIMARY KEY
      );
    `);
  }

  public load(appId: string, windowKey: string): AppWindowState | undefined {
    const row = this.database.prepare(`
      SELECT app_id AS appId, window_key AS windowKey, x, y, width, height, maximized
      FROM app_window_state WHERE app_id = ? AND window_key = ?
    `).get(appId, windowKey) as Omit<AppWindowState, 'maximized'> & { maximized: number } | undefined;
    return row ? { ...row, maximized: row.maximized === 1 } : undefined;
  }

  public upsert(state: AppWindowState): void {
    this.database.prepare(`
      INSERT INTO app_window_state (app_id, window_key, x, y, width, height, maximized)
      VALUES (?, ?, ?, ?, ?, ?, ?)
      ON CONFLICT(app_id, window_key) DO UPDATE SET
        x = excluded.x, y = excluded.y, width = excluded.width,
        height = excluded.height, maximized = excluded.maximized
    `).run(state.appId, state.windowKey, state.x, state.y, state.width, state.height, state.maximized ? 1 : 0);
  }

  /**
   * 只执行一次窗口状态迁移，并保证迁移标记和目标记录删除处于同一事务中。
   *
   * 迁移仅作用于宿主展示状态表；调用方必须传入明确的复合键，不能借此删除应用业务数据。
   */
  public resetStateOnce(migrationId: string, appId: string, windowKey: string): void {
    this.database.exec('BEGIN IMMEDIATE');
    try {
      const migrated = this.database.prepare(`
        SELECT migration_id FROM app_window_state_migrations WHERE migration_id = ?
      `).get(migrationId);
      if (!migrated) {
        this.database.prepare('DELETE FROM app_window_state WHERE app_id = ? AND window_key = ?').run(appId, windowKey);
        this.database.prepare('INSERT INTO app_window_state_migrations (migration_id) VALUES (?)').run(migrationId);
      }
      this.database.exec('COMMIT');
    } catch (error) {
      try {
        this.database.exec('ROLLBACK');
      } catch {
        // 原始异常更能说明迁移失败原因，回滚失败不覆盖它。
      }
      throw error;
    }
  }

  public close(): void { this.database.close(); }
}
