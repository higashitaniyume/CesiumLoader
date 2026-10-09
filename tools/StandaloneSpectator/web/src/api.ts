export type Id = number | string
export interface HandCard { cardUid: Id; cardId: Id; purifyNum?: number; isTemp?: boolean; battleCost?: number; name?: string; description?: string; iconUrl?: string }
export interface Player { playerId: Id; slot: number; nick: string; heroId?: Id; heroName?: string; hp?: number; maxHp?: number; gold?: number; attack?: number; defense?: number; handKnown: boolean; handCount?: number; cards: HandCard[] }
export interface Snapshot { mode?: string; officialSpectating: boolean; status?: string; roomId?: Id; players: Player[] }
export interface ServiceStatus { authenticated?: boolean; status?: string; spectator?: { roomId?: Id }; roomId?: Id; loginAttempted?: boolean }

export class ApiError extends Error { constructor(public code: number, message: string) { super(message) } }
let token: string | undefined
let configRequest: Promise<void> | undefined
export async function configure() {
  if (token) return
  if (!configRequest) configRequest = (async () => {
    const response = await fetch('/ui/config', { headers: { 'X-Spectator-UI': '1' }, cache: 'no-store', signal: AbortSignal.timeout(8000) })
    if (!response.ok) throw new ApiError(response.status, '无法初始化观战台，请确认本地服务已启动。')
    const config: { token?: string } = await response.json()
    if (!config.token) throw new Error('本地服务没有返回访问令牌。')
    token = config.token
  })().finally(() => { configRequest = undefined })
  return configRequest
}
export async function api<T>(path: string, body?: object): Promise<T> {
  await configure()
  const response = await fetch(path, { method: body ? 'POST' : 'GET', headers: { Authorization: `Bearer ${token}`, ...(body ? { 'Content-Type': 'application/json' } : {}) }, body: body ? JSON.stringify(body) : undefined, cache: 'no-store', signal: AbortSignal.timeout(body ? 35000 : 8000) })
  if (!response.ok) {
    const detail = await response.json().catch(() => ({})) as { error?: string; serverError?: number }
    if (response.status === 401) token = undefined
    const messages: Record<string, string> = { state_unavailable: '正在等待房间快照。', authentication_required: '本地服务尚未载入登录信息。', operation_timeout: '操作超时，请查看服务状态后重试。', unauthorized: '访问令牌失效，请重试。' }
    throw new ApiError(response.status, `${messages[detail.error ?? ''] ?? `请求失败（HTTP ${response.status}）`}${detail.serverError != null ? `，官方服务错误 ${detail.serverError}` : ''}`)
  }
  return response.json() as Promise<T>
}
export const hasRoom = (status: ServiceStatus | null) => {
  const roomId = status?.spectator?.roomId ?? status?.roomId
  return roomId != null && String(roomId) !== '0' && String(roomId) !== ''
}
