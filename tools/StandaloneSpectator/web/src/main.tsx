import React, { useEffect, useRef, useState } from 'react'
import { createRoot } from 'react-dom/client'
import { api, ApiError, hasRoom, type Player, type ServiceStatus, type Snapshot } from './api'
import './style.css'

const readableError = (error: unknown) => error instanceof Error ? error.message : '连接失败，请重试。'

function PlayerPanel({ player, position, unavailable }: { player?: Player; position: number; unavailable: boolean }) {
  const handKnown = !unavailable && player?.handKnown === true
  const stats = [
    ['生命', player?.hp != null ? `${player.hp}${player.maxHp != null ? ` / ${player.maxHp}` : ''}` : '—'],
    ['星币', player?.gold ?? '—'], ['攻击', player?.attack ?? '—'], ['防御', player?.defense ?? '—'],
  ]
  return <article className="player-panel" aria-label={`第 ${position} 位玩家`}>
    <div className="flex items-start gap-4">
      <div className="seat-badge" aria-hidden="true">{String(position).padStart(2, '0')}</div>
      <div className="min-w-0 flex-1">
        <p className="eyebrow">玩家席位 {player?.slot ?? position}</p>
        <h2 className="mt-1 truncate text-xl font-bold" title={player?.nick}>{player?.nick || (player ? `玩家 ${player.playerId}` : '等待玩家')}</h2>
        <p className="mt-1 text-sm text-muted">{player ? `${player.heroName || (player.heroId != null ? `角色 #${player.heroId}` : '角色信息未提供')} · 玩家 ${player.playerId}` : '房间同步后显示角色与公开属性'}</p>
      </div>
      <span className={`tag ${handKnown ? 'tag-teal' : ''}`}>{unavailable ? '数据过期' : player ? handKnown ? '手牌已同步' : '手牌不可见' : '等待同步'}</span>
    </div>
    <dl className="mt-6 grid grid-cols-4 gap-2 rounded-xl bg-paper px-3 py-4">
      {stats.map(([label, value]) => <div key={label} className="text-center"><dt className="text-xs text-muted">{label}</dt><dd className="mt-1 text-lg font-semibold tabular-nums">{unavailable ? '—' : value}</dd></div>)}
    </dl>
    <div className="mb-3 mt-6 flex items-center justify-between"><h3 className="text-sm font-semibold">手牌</h3><span className="text-xs text-muted">{!unavailable && player?.handCount != null ? `${player.handCount} 张` : '数量未知'}</span></div>
    {!handKnown ? <div className="empty-hand"><span aria-hidden="true" className="empty-symbol">◇</span><p>{unavailable ? '快照已过期，等待重新同步' : player ? '服务器未提供这位玩家的手牌' : '尚未收到玩家数据'}</p><p className="mt-1 text-xs">{player && !unavailable ? '手牌被遮蔽或暂时不可用' : '不会用示例卡牌填充'}</p></div>
      : player.cards.length === 0 ? <div className="empty-hand"><p>{player.handCount === 0 ? '当前没有手牌' : '等待手牌明细'}</p></div>
      : <ul className="grid gap-2 sm:grid-cols-2">{player.cards.map((card, index) => <li key={`${card.cardUid}-${index}`} className="hand-card">
        <div className="flex items-start gap-3">
          {card.iconUrl ? <img className="card-icon" src={card.iconUrl} alt="" loading="lazy" onError={event => { event.currentTarget.style.display = 'none' }} /> : <span className="card-id" aria-hidden="true">牌</span>}
          <div className="min-w-0 flex-1"><p className="font-semibold text-sm">{card.name || `卡牌 #${card.cardId}`}</p><p className="mt-1 text-xs text-muted">ID {card.cardId}{card.battleCost != null ? ` · 战斗消耗 ${card.battleCost}` : ''}</p></div>
        </div>
        {card.description && <p className="mt-3 whitespace-pre-line text-xs leading-relaxed text-muted">{card.description}</p>}
        {(card.isTemp || (card.purifyNum ?? 0) > 0) && <div className="mt-2 flex gap-2 text-xs text-teal">{card.isTemp && <span>临时卡</span>}{(card.purifyNum ?? 0) > 0 && <span>净化 {card.purifyNum}</span>}</div>}
      </li>)}</ul>}
  </article>
}

function App() {
  const [watchCode, setWatchCode] = useState('')
  const [service, setService] = useState<ServiceStatus | null>(null)
  const [snapshot, setSnapshot] = useState<Snapshot | null>(null)
  const [busy, setBusy] = useState(false)
  const [ready, setReady] = useState(false)
  const [error, setError] = useState('')
  const [notice, setNotice] = useState('正在连接本地观战服务…')
  const [stale, setStale] = useState(false)
  const [updatedAt, setUpdatedAt] = useState(0)
  const [retry, setRetry] = useState(0)
  const actionBusy = useRef(false)
  const generation = useRef(0)
  const lastSuccess = useRef(0)
  const networkFailed = useRef(false)

  useEffect(() => {
    let disposed = false
    let timer: ReturnType<typeof setTimeout>
    async function poll() {
      if (!actionBusy.current) {
        const version = generation.current
        const results = await Promise.allSettled([api<ServiceStatus>('/status'), api<Snapshot>('/state')])
        if (disposed || version !== generation.current) return schedule()
        const [statusResult, stateResult] = results
        if (statusResult.status === 'fulfilled') { setService(statusResult.value); setReady(true) }
        if (stateResult.status === 'fulfilled') {
          lastSuccess.current = Date.now(); networkFailed.current = false
          setSnapshot(stateResult.value); setUpdatedAt(lastSuccess.current); setStale(false)
          setNotice(stateResult.value.officialSpectating ? '正在接收官方观战数据' : '等待官方观战确认')
        } else if (stateResult.reason instanceof ApiError && stateResult.reason.code === 503) {
          networkFailed.current = false; lastSuccess.current = 0
          setSnapshot(null); setStale(false); setNotice('正在等待房间快照，手牌暂不可用')
        } else {
          networkFailed.current = true
          setNotice(`同步中断：${readableError(stateResult.reason)}`)
          if (Date.now() - lastSuccess.current >= 5000) { setSnapshot(null); setStale(true) }
        }
        if (statusResult.status === 'rejected' && stateResult.status === 'rejected') setReady(false)
      }
      schedule()
    }
    function schedule() { if (!disposed) timer = setTimeout(poll, 1000) }
    void poll()
    const watchdog = setInterval(() => {
      if (!disposed && lastSuccess.current > 0 && Date.now() - lastSuccess.current >= 5000) { setSnapshot(null); setStale(true) }
    }, 250)
    return () => { disposed = true; clearTimeout(timer); clearInterval(watchdog) }
  }, [retry])

  async function runAction(action: 'connect' | 'leave') {
    if (actionBusy.current) return
    actionBusy.current = true; generation.current++; setBusy(true); setError('')
    // A new room must never retain the previous room's handcards.
    setSnapshot(null); setStale(false); lastSuccess.current = 0; networkFailed.current = false
    try {
      let current = await api<ServiceStatus>('/status'); setService(current); setReady(true)
      if (action === 'connect') {
        setNotice('正在建立观战连接…')
        if (!current.authenticated) {
          await api('/login', {})
          current = await api<ServiceStatus>('/status'); setService(current)
          if (!current.authenticated) throw new Error('登录尚未确认，请查看本地服务状态。')
        }
        if (hasRoom(current)) await api('/leave', {})
        await api('/watch', { watchCode: watchCode.trim() })
        setNotice('观战请求已发送，正在等待房间快照')
      } else {
        await api('/leave', {}); setNotice('已离开观战房间')
      }
      setService(await api<ServiceStatus>('/status'))
    } catch (failure) {
      setError(readableError(failure)); setNotice('操作未完成；可在下方查看登录状态后重试')
      try { setService(await api<ServiceStatus>('/status')) } catch { setReady(false) }
    } finally { actionBusy.current = false; setBusy(false); setRetry(value => value + 1) }
  }

  const players = [...(snapshot?.players ?? [])].sort((a, b) => a.slot - b.slot).slice(0, 4)
  const roomId = snapshot?.roomId ?? service?.spectator?.roomId ?? service?.roomId
  return <main className="mx-auto max-w-7xl px-5 py-8 sm:px-8 sm:py-12">
    <header className="mb-9 flex flex-wrap items-end justify-between gap-5">
      <div><p className="eyebrow mb-3 text-teal">ASTRAL PARTY / LIVE SPECTATOR</p><h1 className="text-3xl font-bold tracking-tight sm:text-4xl">吉星派对 <span className="text-teal">·</span> 观战台</h1><p className="mt-3 text-sm text-muted">一桌四人，实时查看手牌与公开属性。</p></div>
      <div className="flex items-center gap-2 text-xs text-muted"><span className="status-dot" />官方 PVE 观战</div>
    </header>
    <section className="control-panel" aria-label="连接观战房间">
      <form className="flex flex-wrap items-end gap-3" onSubmit={event => { event.preventDefault(); if (watchCode.trim()) void runAction('connect') }}>
        <div className="min-w-48 flex-1"><label className="mb-2 block text-sm font-semibold" htmlFor="watch-code">官方观战码</label><input id="watch-code" name="watchCode" autoComplete="off" placeholder="输入房间的观战码" value={watchCode} onChange={event => setWatchCode(event.target.value)} maxLength={128} disabled={busy} aria-describedby="watch-help" /></div>
        <button className="button-primary" disabled={busy || !watchCode.trim()} type="submit">{busy ? '处理中…' : error ? '重试连接' : '连接观战'}</button>
        <button className="button-secondary" disabled={busy || !hasRoom(service)} onClick={() => void runAction('leave')} type="button">离开房间</button>
      </form>
      <p id="watch-help" className="mt-3 text-xs text-muted">连接时会先登录本地服务；已有观战房间会先离开，再加入输入的房间。</p>
      <div className="mt-5 flex flex-wrap items-center gap-x-6 gap-y-2 border-t border-line pt-4 text-xs">
        <span className="flex items-center gap-2"><span className={`status-dot ${ready ? 'is-online' : ''}`} />本地服务{ready ? '已连接' : '连接中'}</span>
        <span>登录：{service?.authenticated ? '已认证' : service ? '未认证' : '检查中'}</span>
        <span>房间：{roomId != null && String(roomId) !== '0' ? String(roomId) : '未加入'}</span>
        {updatedAt > 0 && snapshot && <span className="text-muted">更新于 {new Date(updatedAt).toLocaleTimeString('zh-CN', { hour12: false })}</span>}
      </div>
      {error && <p role="alert" className="error-message mt-4">{error}{service?.loginAttempted && !service.authenticated ? ' 本地服务已尝试登录；若服务拒绝再次登录，请重启服务后重试。' : ''}</p>}
    </section>
    <div className="mb-5 mt-8 flex flex-wrap items-center justify-between gap-3"><h2 className="text-sm font-semibold">全员手牌 <span className="ml-2 text-xs font-normal text-muted">按席位排列 · 每秒同步</span></h2><p role="status" aria-live="polite" className={`text-xs ${stale ? 'text-warning' : 'text-muted'}`}>{notice}{stale ? ' · 数据已过期' : ''}</p></div>
    <section className="grid items-start gap-5 lg:grid-cols-2" aria-label="四位玩家">
      {Array.from({ length: 4 }, (_, index) => <PlayerPanel key={index} player={players[index]} position={index + 1} unavailable={stale} />)}
    </section>
    <footer className="mt-7 flex flex-wrap justify-between gap-2 text-xs leading-relaxed text-muted"><p>只展示官方观战服务实际提供的数据。缺失卡牌名称时显示真实 ID。</p><p>连接中断超过 5 秒后清空过期数据。</p></footer>
  </main>
}

createRoot(document.getElementById('root')!).render(<React.StrictMode><App /></React.StrictMode>)
