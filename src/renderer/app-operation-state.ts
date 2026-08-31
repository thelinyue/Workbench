/**
 * 应用中心操作状态按 appId 隔离：同一应用只允许一个操作进行，
 * 不同应用可以并行，避免一个应用的请求影响其他应用卡片。
 */
export interface AppOperationState {
  activeAppIds: readonly string[];
}

export function beginAppOperation(state: AppOperationState, appId: string): AppOperationState {
  return state.activeAppIds.includes(appId) ? state : { activeAppIds: [...state.activeAppIds, appId] };
}

export function completeAppOperation(state: AppOperationState, appId: string): AppOperationState {
  return state.activeAppIds.includes(appId)
    ? { activeAppIds: state.activeAppIds.filter((activeAppId) => activeAppId !== appId) }
    : state;
}

export function isAppOperationBusy(state: AppOperationState, appId: string): boolean {
  return state.activeAppIds.includes(appId);
}
