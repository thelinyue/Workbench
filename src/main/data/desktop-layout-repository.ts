import { mkdirSync } from 'node:fs';
import { dirname } from 'node:path';
import { DatabaseSync } from 'node:sqlite';

export interface DesktopIconLayout {
  appId: string;
  x: number;
  y: number;
}

const VERTICAL_DEFAULT_LAYOUT_MIGRATION = 'vertical-default-layout-v1';
const VERTICAL_AUTO_ALIGNMENT_MIGRATION = 'vertical-auto-alignment-v2';

/**
 * 该仓储只持有桌面图标布局；原生窗口状态由 AppWindowStateRepository 独立管理，
 * 两者都不保存应用诊断包、任务或报告等业务数据。
 *
 * 应用的诊断包、任务和规则数据由各自应用的 backend 管理；宿主数据库只保留
 * 桌面布局表，继续复用旧数据库文件以避免升级时丢失用户图标位置。
 * 布局迁移记录与坐标放在同一事务中，保证首次升级重排后才标记迁移完成。
 */
export class DesktopLayoutRepository {
  private readonly database: DatabaseSync;

  public constructor(databasePath: string) {
    mkdirSync(dirname(databasePath), { recursive: true });
    this.database = new DatabaseSync(databasePath);
    this.database.exec(`
      CREATE TABLE IF NOT EXISTS desktop_layout (app_id TEXT PRIMARY KEY, x INTEGER NOT NULL, y INTEGER NOT NULL);
      CREATE TABLE IF NOT EXISTS desktop_layout_migration (migration_id TEXT PRIMARY KEY);
    `);
  }

  public close(): void {
    this.database.close();
  }

  public save(layout: DesktopIconLayout[]): void {
    this.database.exec('BEGIN;');
    try {
      this.replaceLayout(layout);
      this.database.exec('COMMIT;');
    } catch (error) {
      this.database.exec('ROLLBACK;');
      throw error;
    }
  }

  public list(): DesktopIconLayout[] {
    return this.database.prepare('SELECT app_id AS appId, x, y FROM desktop_layout ORDER BY app_id').all() as unknown as DesktopIconLayout[];
  }

  /**
   * 首次初始化只在空布局时写入默认入口；v2 迁移再按旧布局视觉顺序压缩为首列。
   * 两个迁移标记和坐标更新必须原子提交，避免启动中断后丢失重排或覆盖后续排序。
   */
  public initializeDefaultLayout(defaultLayout: DesktopIconLayout[]): DesktopIconLayout[] {
    this.database.exec('BEGIN;');
    try {
      const migrated = this.database.prepare('SELECT 1 FROM desktop_layout_migration WHERE migration_id = ?').get(VERTICAL_DEFAULT_LAYOUT_MIGRATION);
      if (!migrated) {
        if (this.list().length === 0) this.replaceLayout(defaultLayout);
        this.database.prepare('INSERT INTO desktop_layout_migration (migration_id) VALUES (?)').run(VERTICAL_DEFAULT_LAYOUT_MIGRATION);
      }
      const autoAligned = this.database.prepare('SELECT 1 FROM desktop_layout_migration WHERE migration_id = ?').get(VERTICAL_AUTO_ALIGNMENT_MIGRATION);
      if (!autoAligned) {
        this.replaceLayout(reflowToVerticalLayout(this.list(), defaultLayout));
        this.database.prepare('INSERT INTO desktop_layout_migration (migration_id) VALUES (?)').run(VERTICAL_AUTO_ALIGNMENT_MIGRATION);
      }
      const layout = this.list();
      this.database.exec('COMMIT;');
      return layout;
    } catch (error) {
      this.database.exec('ROLLBACK;');
      throw error;
    }
  }

  private replaceLayout(layout: DesktopIconLayout[]): void {
    this.database.exec('DELETE FROM desktop_layout;');
    const insert = this.database.prepare('INSERT INTO desktop_layout (app_id, x, y) VALUES (?, ?, ?)');
    for (const item of layout) insert.run(item.appId, item.x, item.y);
  }
}

/** 使用默认槽位承载旧布局的视觉顺序，防止自由坐标在迁移后继续扩散。 */
function reflowToVerticalLayout(layout: readonly DesktopIconLayout[], defaultLayout: readonly DesktopIconLayout[]): DesktopIconLayout[] {
  const slots = [...defaultLayout].sort(compareDesktopIconLayout);
  const availableAppIds = new Set(slots.map((item) => item.appId));
  const seen = new Set<string>();
  const saved = [...layout]
    .filter((item) => availableAppIds.has(item.appId))
    .sort(compareDesktopIconLayout)
    .filter((item) => !seen.has(item.appId) && Boolean(seen.add(item.appId)));
  const ordered = [...saved, ...slots.filter((item) => !seen.has(item.appId))];
  return ordered.map((item, index) => ({ appId: item.appId, x: slots[index]!.x, y: slots[index]!.y }));
}

function compareDesktopIconLayout(left: DesktopIconLayout, right: DesktopIconLayout): number {
  return left.y - right.y || left.x - right.x || left.appId.localeCompare(right.appId);
}
