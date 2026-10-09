// Isolated browser test. Mock fixtures never enter the live application bundle.
import assert from 'node:assert/strict'
import { createServer } from 'node:http'
import { readFile, mkdtemp, rm } from 'node:fs/promises'
import { resolve, extname, join } from 'node:path'
import { spawn } from 'node:child_process'
import { createRequire } from 'node:module'

const require = createRequire(import.meta.url)
const { chromium } = require(process.env.PLAYWRIGHT_MODULE || 'C:/Users/shimikoi/.dsh/profiles/web/node_modules/playwright')
const root = resolve(import.meta.dirname, '..')
const profile = await mkdtemp(join(root, '.smoke-profile-'))
const server = createServer(async (req, res) => {
  try {
    const url = new URL(req.url, 'http://localhost')
    const file = resolve(root, 'dist', url.pathname === '/' ? 'index.html' : `.${url.pathname}`)
    assert.ok(file.startsWith(resolve(root, 'dist') + '/').valueOf() || file.startsWith(resolve(root, 'dist') + '\\'))
    res.setHeader('Content-Type', ({ '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css' })[extname(file)] || 'application/octet-stream')
    res.end(await readFile(file))
  } catch { res.writeHead(404); res.end() }
})
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve))
const origin = `http://127.0.0.1:${server.address().port}`
const debugPort = server.address().port + 1
const child = spawn(process.env.BROWSER_EXECUTABLE || 'C:/Program Files/Google/Chrome/Application/chrome.exe', ['--headless=new', '--disable-gpu', '--no-first-run', '--no-default-browser-check', `--remote-debugging-port=${debugPort}`, `--user-data-dir=${profile}`, 'about:blank'], { stdio: 'ignore' })
let browser
try {
  for (let attempt = 0; attempt < 40; attempt++) {
    try { browser = await chromium.connectOverCDP(`http://127.0.0.1:${debugPort}`); break } catch { await new Promise(resolve => setTimeout(resolve, 200)) }
  }
  assert.ok(browser, 'Browser must start')
  const page = await browser.contexts()[0].newPage()
  const errors = []
  page.on('pageerror', error => errors.push(error.message))
  let authenticated = false, roomId = '111', stateMode = 'ok'
  const actions = []
  const players = Array.from({ length: 4 }, (_, index) => ({ playerId: String(index + 1), slot: index, nick: `测试玩家${index + 1}`, heroId: 100 + index, hp: 8, maxHp: 10, gold: 12, handKnown: index !== 1, handCount: 1, cards: [{ cardUid: index + 1, cardId: index === 1 ? 999999 : 12345 + index, name: index === 0 ? '测试名称' : undefined, description: index === 0 ? '测试描述' : undefined }] }))
  await page.route('**/*', async route => {
    const pathname = new URL(route.request().url()).pathname
    const reply = (body, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) })
    if (pathname === '/ui/config') { assert.equal(route.request().headers()['x-spectator-ui'], '1'); return reply({ token: 'test-token' }) }
    if (['/status', '/state', '/login', '/leave', '/watch'].includes(pathname)) assert.equal(route.request().headers().authorization, 'Bearer test-token')
    if (pathname === '/status') return reply({ authenticated, spectator: { roomId } })
    if (pathname === '/state') {
      if (stateMode === 'waiting') return reply({ error: 'state_unavailable' }, 503)
      if (stateMode === 'failed') return route.abort()
      return reply({ mode: 'pve', officialSpectating: true, roomId, players: [...players].reverse() })
    }
    if (pathname === '/login') { actions.push('login'); authenticated = true; return reply({ status: 'logged_in' }) }
    if (pathname === '/leave') { actions.push('leave'); roomId = '0'; return reply({ status: 'done' }) }
    if (pathname === '/watch') { actions.push('watch'); assert.equal(route.request().postDataJSON().watchCode, '123456'); roomId = '222'; return reply({ status: 'done' }) }
    return route.continue()
  })
  await page.goto(origin)
  await page.getByText('测试名称', { exact: true }).waitFor()
  assert.equal(await page.locator('.player-panel').count(), 4)
  assert.equal(await page.locator('.player-panel h2').first().textContent(), '测试玩家1')
  assert.equal(await page.getByText('卡牌 #12347', { exact: true }).count(), 1)
  assert.equal(await page.getByText('ID 999999', { exact: false }).count(), 0, 'Masked hands must not be revealed')
  await page.getByLabel('官方观战码').fill('123456')
  await page.getByRole('button', { name: '连接观战', exact: true }).click()
  await page.getByText('房间：222', { exact: true }).waitFor()
  assert.deepEqual(actions, ['login', 'leave', 'watch'])
  stateMode = 'waiting'
  await page.getByText('正在等待房间快照，手牌暂不可用', { exact: true }).waitFor()
  assert.equal(await page.locator('.hand-card').count(), 0)
  assert.equal(await page.getByText('登录：已认证', { exact: true }).count(), 1)
  stateMode = 'ok'
  await page.getByText('测试名称', { exact: true }).waitFor()
  stateMode = 'failed'
  await page.getByText('同步中断：', { exact: false }).waitFor()
  await page.getByText('快照已过期，等待重新同步', { exact: true }).first().waitFor({ timeout: 7000 })
  assert.equal(await page.locator('.hand-card').count(), 0)
  stateMode = 'ok'
  await page.getByText('测试名称', { exact: true }).waitFor()
  await page.setViewportSize({ width: 390, height: 844 })
  assert.equal(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), true, 'Mobile layout must not overflow')
  assert.deepEqual(errors, [])
  console.log('PASS: four players, slot ordering, localization/ID fallback, masked hands, login→leave→watch, 503 clearing, five-second expiry, recovery, mobile layout, no browser errors.')
} finally {
  await browser?.close()
  child.kill()
  server.closeAllConnections()
  await new Promise(resolve => server.close(resolve))
  if (profile.startsWith(join(root, '.smoke-profile-'))) await rm(profile, { recursive: true, force: true, maxRetries: 10, retryDelay: 200 })
}
