/** 卸载确认框只在首次挂载时聚焦取消按钮，避免状态更新抢回用户当前焦点。 */
export function shouldFocusUninstallCancel(hasFocused: boolean): boolean {
  return !hasFocused;
}
