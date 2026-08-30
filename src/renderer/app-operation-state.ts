/**
 * 应用中心操作锁的最小不可变状态：同一时间只允许首个应用操作持有锁，
 * 后续应用不能覆盖 activeAppId，避免前一个请求尚未结束时提前恢复控件。
 */
export interface AppOperationState {
  activeAppId: string | null;
}

export function beginAppOperation(state: AppOperationState, appId: string): AppOperationState {
  return state.activeAppId === null ? { activeAppId: appId } : state;
}

export function completeAppOperation(state: AppOperationState, appId: string): AppOperationState {
  return state.activeAppId === appId ? { activeAppId: null } : state;
}

export function isAppOperationBusy(state: AppOperationState): boolean {
  return state.activeAppId !== null;
}
