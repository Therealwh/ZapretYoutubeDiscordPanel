'use strict';
(function () {
const { t, setLang, detectLang, DICT } = window.I18N;

const $ = (sel) => document.querySelector(sel);
const $$ = (sel) => [...document.querySelectorAll(sel)];

let cfg = {};
let appInfo = {};
let currentList = 'general';
let listsEditable = ['general', 'exclude', 'ipsetExclude'];
let testing = false;
let lastBanner = { active: false };

// ---------- i18n binding ----------
function applyI18n() {
  document.querySelectorAll('[data-i18n]').forEach((el) => {
    el.textContent = t(el.getAttribute('data-i18n'));
  });
  document.documentElement.lang = currentLang;
  $('#lang-toggle').textContent = currentLang.toUpperCase();
}
let currentLang = 'ru';

// ---------- theme ----------
const ICON_SUN = '\uE706';
const ICON_MOON = '\uE708';
function applyTheme(theme) {
  document.documentElement.classList.remove('dark', 'light');
  document.documentElement.classList.add(theme === 'light' ? 'light' : 'dark');
  const btn = $('#theme-toggle');
  if (btn) btn.textContent = theme === 'light' ? ICON_MOON : ICON_SUN;
}

// ---------- toasts ----------
function toast(msg, kind = '') {
  const box = document.createElement('div');
  box.className = `toast ${kind}`;
  box.textContent = msg;
  $('#toasts').appendChild(box);
  setTimeout(() => {
    box.classList.add('out');
    setTimeout(() => box.remove(), 300);
  }, 2600);
}

// Honest result toast for bypass actions (install service / launch process):
// explains WHY nothing happened instead of a generic "error".
function actionToast(r) {
  if (r && r.ok) {
    toast(t('toast_done'), 'ok');
    return;
  }
  const reason = r && r.reason;
  if (reason === 'elevate_cancelled' || reason === 'cancelled') toast(t('toast_uac_cancelled'), 'err');
  else if (reason === 'service_start_failed') toast(t('err_service_start'), 'err');
  else if (reason === 'admin_required') toast(t('test_need_admin'), 'err');
  else toast(`${t('toast_error')}${reason ? `: ${reason}` : ''}`, 'err');
}

// ---------- navigation ----------
function showPage(name) {
  $$('.nav-item').forEach((b) => b.classList.toggle('active', b.dataset.page === name));
  $$('.page').forEach((p) => p.classList.add('hidden'));
  $(`#page-${name}`).classList.remove('hidden');
  $(`#page-${name}`).style.animation = 'none';
  void $(`#page-${name}`).offsetWidth;
  $(`#page-${name}`).style.animation = '';
  if (name === 'strategies') renderStrategies();
  if (name === 'filters') renderFilters();
  if (name === 'lists') renderLists();
  if (name === 'updates') renderUpdates();
  if (name === 'diagnostics') renderDiagnostics();
  if (name === 'settings') renderSettings();
  if (name === 'dashboard') refreshStatus();
}

// ---------- status ----------
const stateWord = (st, runningKey, absentKey, stoppedKey) =>
  st === null ? t(absentKey) : st ? t(runningKey) : t(stoppedKey);

async function refreshStatus() {
  const root = cfg.zapretRoot;
  if (!root) return;
  const { status: st, banner } = await window.api.getStatus();
  lastBanner = banner;
  renderBanner(banner, st);
  const on = st.activeMode !== 'off';
  $('#power-switch').setAttribute('aria-checked', String(on));
  $('#power-state').textContent = on ? t('dash_on') : t('dash_off');
  $('#power-state').className = `power-state ${on ? 'ok' : ''}`;

  $('#stat-strategy').textContent = st.serviceStrategy || t('dash_no_strategy');
  $('#stat-mode').textContent =
    st.activeMode === 'service' ? t('dash_mode_service') : st.activeMode === 'process' ? t('dash_mode_process') : '—';
  const svc = st.service.exists ? stateWord(st.service.running, 'dash_state_running', 'dash_state_absent', 'dash_state_stopped') : t('dash_state_absent');
  $('#stat-zapret').textContent = svc;
  $('#stat-zapret').className = `stat-value ${st.service.running ? 'ok' : st.service.exists ? 'err' : 'dim'}`;
  const wd = st.winDivert.exists ? stateWord(st.winDivert.running, 'dash_state_running', 'dash_state_absent', 'dash_state_stopped') : t('dash_state_absent');
  $('#stat-windivert').textContent = wd;
  $('#stat-windivert').className = `stat-value ${st.winDivert.running ? 'ok' : st.winDivert.exists ? 'err' : 'dim'}`;
  $('#stat-winws').textContent = st.winwsProcess ? t('dash_state_running') : t('dash_state_stopped');
  $('#stat-winws').className = `stat-value ${st.winwsProcess ? 'ok' : 'dim'}`;
  const pathEl = $('#stat-winws-path');
  if (pathEl) pathEl.textContent = st.winwsProcessPath || '';
}

// ---------- banners ----------
function renderBanner(banner, st) {
  const el = $('#banner-active');
  if (banner.active && !sessionStorage.getItem('bannerDismissed')) {
    let text;
    if (banner.kind === 'service') text = t('banner_active_service');
    else if (banner.kind === 'process') text = t('banner_active_process');
    else text = t('banner_conflict', { name: banner.name });
    $('#banner-active-text').textContent = text;
    el.classList.remove('hidden');
  } else {
    el.classList.add('hidden');
  }
  $('#banner-admin').classList.toggle('hidden', !!appInfo.admin);
}

// ---------- setup ----------
async function setupInit() {
  const found = await window.api.discoverZapret();
  if (found) {
    $('#setup-found').classList.remove('hidden');
    $('#setup-found-path').textContent = found;
    $('#setup-use-existing').classList.remove('hidden');
    $('#setup-notfound').classList.add('hidden');
  } else {
    $('#setup-notfound').classList.remove('hidden');
    // Don't insist "not found" when a bypass is visibly running: offer to
    // adopt the install dir of the running winws.exe.
    try {
      const runInfo = await window.api.runningInfo();
      if (runInfo && runInfo.running && runInfo.winwsRoot) {
        $('#setup-running-path').textContent = runInfo.winwsRoot;
        $('#setup-running').classList.remove('hidden');
        $('#setup-running-use').addEventListener('click', async () => {
          const v = await window.api.validateRoot(runInfo.winwsRoot);
          if (v.ok) {
            cfg = await window.api.setConfig({ zapretRoot: runInfo.winwsRoot });
            enterApp();
          } else {
            toast(t('setup_not_found'), 'err');
          }
        });
        $('#setup-running-stop').addEventListener('click', async () => {
          await window.api.stopService();
          await window.api.killWinws();
          $('#setup-running').classList.add('hidden');
          toast(t('toast_cmd_sent'), 'ok');
        });
      }
    } catch {
      /* running info optional */
    }
  }
}

// ---- release security: render results after download ----
function renderSecurity(res) {
  const box = $('#setup-security');
  const list = $('#setup-sec-list');
  const verdict = $('#setup-sec-verdict');
  list.textContent = '';
  if (!res || !res.security || !Array.isArray(res.security.checks) || !res.security.checks.length) {
    box.classList.add('hidden');
    return;
  }
  box.classList.remove('hidden');
  const tMap = {
    hash: (c) => (c.verdict === 'pass' ? t('setup_sec_sha') : t('setup_sec_hash_unknown')),
    windivert_sig: (c) => {
      if (c.verdict === 'fail' && c.detail === 'sys_missing') return t('setup_sec_sig_missing');
      return c.verdict === 'pass' ? t('setup_sec_sig_ok') : t('setup_sec_sig_bad');
    },
    virustotal: (c) =>
      c.verdict === 'skip'
        ? t('setup_sec_vt_skip', { detail: c.detail || '' })
        : c.verdict === 'fail'
          ? t('setup_sec_vt_bad', { malicious: c.malicious, total: c.total })
          : t('setup_sec_vt_ok', { malicious: c.malicious, total: c.total }),
  };
  for (const c of res.security.checks) {
    const li = document.createElement('li');
    li.className = c.verdict === 'pass' ? 'tl-ok' : c.verdict === 'fail' ? 'tl-err' : 'tl-warn';
    li.textContent = tMap[c.id] ? tMap[c.id](c) : c.id;
    list.appendChild(li);
  }
  const lvl = res.security.level;
  if (res.security.ok === true || lvl === 'green') {
    verdict.textContent = t('setup_sec_green');
    verdict.className = 'sec-verdict green';
  } else if (res.security.ok === false && lvl === 'red') {
    verdict.textContent = t('setup_sec_red');
    verdict.className = 'sec-verdict red';
  } else {
    verdict.textContent = t('setup_sec_yellow');
    verdict.className = 'sec-verdict yellow';
  }
}

function validateSetupPath(p) {
  const warns = [];
  if (/[\u0430-\u044F\u0410-\u042F\u0451\u0401]/.test(p)) warns.push(t('setup_warn_cyrillic'));
  if (/\s/.test(p)) warns.push(t('setup_warn_spaces'));
  const warn = $('#setup-warn');
  if (warns.length) {
    warn.textContent = warns.join(' ');
    warn.classList.remove('hidden');
  } else {
    warn.classList.add('hidden');
  }
}

function bindSetup() {
  $('#setup-install-new').addEventListener('click', () => {
    $('#setup-install-form').classList.remove('hidden');
    validateSetupPath($('#setup-path').value);
  });
  $('#setup-path').addEventListener('input', (e) => validateSetupPath(e.target.value));
  $('#setup-browse').addEventListener('click', async () => {
    const dir = await window.api.chooseDir();
    if (dir) {
      $('#setup-path').value = dir;
      validateSetupPath(dir);
    }
  });
  $('#setup-use-existing').addEventListener('click', () => enterApp());
  $('#setup-download').addEventListener('click', async () => {
    let dir = $('#setup-path').value.trim().replace(/[\\/]+$/, '');
    if (!/^[a-zA-Z]:\\.+/i.test(dir)) {
      toast(t('setup_path_invalid'), 'err');
      return;
    }
    $('#setup-download').disabled = true;
    $('#setup-progress-wrap').classList.remove('hidden');
    $('#setup-stage').textContent = t('setup_stage_resolve');
    const offStage = window.api.on('install:stage', (s) => {
      $('#setup-stage').textContent = t(`setup_stage_${s}`) || s;
    });
    const offProg = window.api.on('install:progress', (frac) => {
      $('#setup-progress').style.width = `${Math.round(frac * 100)}%`;
    });
    let res;
    try {
      res = await window.api.installZapret(dir);
    } catch {
      res = { ok: false, reason: 'install_failed' };
    }
    offStage?.();
    offProg?.();
    renderSecurity(res);
    if (res.ok) {
      cfg = await window.api.setConfig({ zapretRoot: dir });
      $('#setup-done').classList.remove('hidden');
      toast(t('setup_done'), 'ok');
    } else {
      toast(`${t('toast_error')}: ${res.reason || ''}`, 'err');
      if (res.reason === 'security_rejected') {
        $('#setup-stage').textContent = t('setup_sec_red');
      }
      $('#setup-download').disabled = false;
    }
  });
  $('#setup-done').addEventListener('click', () => enterApp());
}

function enterApp() {
  $('#setup-view').classList.add('hidden');
  $('#main-view').classList.remove('hidden');
  if (cfg.zapretRoot) window.api.ensureLists();
  refreshStatus();
  if (!enterApp._timer) {
    enterApp._timer = setInterval(() => {
      if (!$('#main-view').classList.contains('hidden')) refreshStatus();
    }, 4000);
  }
}

// ---------- strategies ----------
// Verdict from test results: red = nothing works, orange = only one of
// Discord/YouTube works (or YouTube throttled), green = both work.
function ytStateOf(groups) {
  const y = (groups && groups.youtube) || {};
  if (y.state) return y.state;
  return y.works ? 'ok' : 'dead';
}

function verdictOf(res) {
  if (!res || res.failed) return 'red';
  const g = res.groups || {};
  const d = !!g.discord?.works;
  const y = ytStateOf(g);
  if (d && y === 'ok') return 'green';
  if (d || y === 'ok' || y === 'throttled') return 'orange';
  return 'red';
}

function verdictText(res) {
  if (!res || res.failed) return t('verdict_dead');
  const g = res.groups || {};
  const d = !!g.discord?.works;
  const y = ytStateOf(g);
  const spd = g.youtube?.speed;
  const speedPart = spd && spd.bytesPerSec ? ` (${fmtSpeed(spd.bytesPerSec)})` : '';
  if (d && y === 'ok') return t('verdict_both') + speedPart;
  if (d && y === 'throttled') return t('verdict_discord_yt_throttled') + speedPart;
  if (y === 'throttled') return t('verdict_yt_throttled') + speedPart;
  if (d) return t('verdict_discord_only');
  if (y === 'ok') return t('verdict_youtube_only') + speedPart;
  return t('verdict_dead') + (spd && spd.bytesPerSec ? speedPart : '');
}

function fmtSpeed(bps) {
  if (!bps || bps <= 0) return '';
  if (bps >= 1024 * 1024) return `${(bps / 1024 / 1024).toFixed(1)} ${t('speed_mbs')}`;
  return `${Math.round(bps / 1024)} ${t('speed_kbs')}`;
}

let lastTestResults = null;

async function renderStrategies() {
  const list = await window.api.listStrategies();
  const wrap = $('#strategy-list');
  wrap.textContent = '';
  if (!list.length) {
    wrap.innerHTML = `<p class="muted">${t('strat_none')}</p>`;
    return;
  }
  const { status: st } = await window.api.getStatus();
  const resByName = new Map((lastTestResults?.results || []).map((r) => [r.name, r]));
  const winnerName = lastTestResults?.winner;
  for (const s of list) {
    const res = resByName.get(s.name);
    const v = verdictOf(res);
    const card = document.createElement('div');
    card.className = 'strat-card'
      + (st.serviceStrategy === s.name ? ' active' : '')
      + (lastTestResults ? ` verdict-${v}` : '');
    card.dataset.strategy = s.name;

    // main row: name + badges + actions
    const main = document.createElement('div');
    main.className = 'strat-main';
    const left = document.createElement('div');
    const name = document.createElement('div');
    name.className = 'strat-name';
    name.textContent = s.name;
    left.appendChild(name);
    const badges = document.createElement('div');
    badges.className = 'strat-badges';
    if (st.serviceStrategy === s.name) {
      const b = document.createElement('span');
      b.className = 'badge';
      b.textContent = t('strat_active_badge');
      badges.appendChild(b);
    }
    if (winnerName === s.name) {
      const b = document.createElement('span');
      b.className = 'badge ok';
      b.textContent = t('strat_recommended');
      badges.appendChild(b);
    }
    if (res) {
      const vd = document.createElement('span');
      vd.className = `strat-verdict ${v}`;
      vd.textContent = verdictText(res);
      badges.appendChild(vd);
    }
    left.appendChild(badges);

    const actions = document.createElement('div');
    actions.className = 'strat-actions';
    const runBtn = mkBtn(t('strat_run'), 'btn btn-secondary btn-sm', async () => {
      await window.api.setConfig({ lastStrategyFile: s.file, preferServiceMode: false });
      const r = await window.api.launchProcess(s.file);
      actionToast(r);
      refreshStatus();
    });
    const svcBtn = mkBtn(t('strat_install'), 'btn btn-primary btn-sm', async () => {
      await window.api.setConfig({ lastStrategyFile: s.file, preferServiceMode: true });
      const r = await window.api.installService(s.file);
      actionToast(r);
      refreshStatus();
    });
    actions.append(runBtn, svcBtn);
    main.append(left, actions);
    card.appendChild(main);

    // spoiler with per-target detail
    const spoiler = document.createElement('div');
    spoiler.className = 'strat-spoiler';
    const inner = document.createElement('div');
    inner.className = 'strat-spoiler-inner';
    spoiler.appendChild(inner);
    card.appendChild(spoiler);
    main.addEventListener('click', (e) => {
      if (e.target.closest('button')) return;
      spoiler.classList.toggle('open');
      fillSpoiler(inner, s, res);
    });
    if (res) fillSpoiler(inner, s, res);

    wrap.appendChild(card);
  }
}

function fillSpoiler(inner, strat, res) {
  inner.textContent = '';
  if (!res) {
    const p = document.createElement('p');
    p.className = 'muted';
    p.style.fontSize = '12.5px';
    p.textContent = t('strat_not_tested');
    inner.appendChild(p);
    return;
  }
  if (res.failed) {
    const p = document.createElement('p');
    p.className = 'tl-err';
    p.style.fontSize = '12.5px';
    p.textContent = t('test_failed');
    inner.appendChild(p);
    return;
  }
  const vd = document.createElement('div');
  vd.className = `strat-verdict ${verdictOf(res)}`;
  vd.textContent = verdictText(res);
  inner.appendChild(vd);
  for (const tg of res.targets || []) {
    const row = document.createElement('div');
    row.className = 'tl-row';
    const name = document.createElement('span');
    name.className = 'tl-target';
    name.textContent = tg.name;
    row.appendChild(name);
    if (tg.kind === 'ping') {
      const s2 = document.createElement('span');
      s2.className = tg.result === 'Timeout' ? 'tl-err' : 'tl-ok';
      s2.textContent = tg.result;
      row.appendChild(s2);
    } else if (tg.kind === 'speed') {
      const r = tg.result || {};
      const s2 = document.createElement('span');
      s2.className = r.verdict === 'ok' ? 'tl-ok' : r.verdict === 'slow' ? 'tl-warn' : 'tl-err';
      s2.textContent = r.verdict === 'ok'
        ? `${fmtSpeed(r.bytesPerSec)} — ${t('yt_ok')}`
        : r.verdict === 'slow'
          ? `${fmtSpeed(r.bytesPerSec)} — ${t('yt_throttled')}`
          : r.verdict === 'unavailable' ? t('yt_unavailable') : t('yt_fail');
      row.appendChild(s2);
      if (r.host) {
        const s3 = document.createElement('span');
        s3.className = 'tl-dim';
        s3.textContent = r.host;
        row.appendChild(s3);
      }
    } else {
      for (const [k, v] of Object.entries(tg.results)) {
        const s2 = document.createElement('span');
        s2.className = v === 'OK' ? 'tl-ok' : v === 'UNSUP' ? 'tl-warn' : 'tl-err';
        s2.textContent = `${k}:${v}`;
        row.appendChild(s2);
      }
    }
    inner.appendChild(row);
  }
}

function mkBtn(label, cls, onClick) {
  const b = document.createElement('button');
  b.className = cls;
  b.textContent = label;
  b.addEventListener('click', onClick);
  return b;
}

// ---------- strategy testing ----------
function bindTester() {
  const setButtons = (running) => {
    $('#btn-find-working').disabled = running;
    $('#btn-test-stop').classList.toggle('hidden', !running);
  };
  $('#btn-find-working').addEventListener('click', async () => {
    if (testing) return;
    if (!appInfo.admin) {
      // One-UAC flow: remember intent, restart elevated, auto-run tests.
      await window.api.setConfig({ pendingAction: 'run-tests' });
      window.api.restartElevated();
      toast(t('banner_admin_fix'), 'ok');
      return;
    }
    testing = true;
    $('#testing-note').classList.remove('hidden');
    $('#test-progress').classList.remove('hidden');
    setButtons(true);
    // Live detail for the strategy under test: rows appended into its card
    // spoiler, which is opened automatically while that strategy runs.
    let liveCard = null;
    const liveRow = (card, text, cls) => {
      const inner = card.querySelector('.strat-spoiler-inner');
      if (!inner) return;
      const row = document.createElement('div');
      row.className = `tl-row ${cls || ''}`;
      row.textContent = text;
      inner.appendChild(row);
      inner.parentElement.scrollTop = inner.scrollHeight;
    };
    const openLiveSpoiler = (name) => {
      const card = document.querySelector(`[data-strategy="${CSS.escape(name)}"]`);
      if (!card) return null;
      card.classList.add('testing');
      const sp = card.querySelector('.strat-spoiler');
      if (sp) sp.classList.add('open');
      return card;
    };
    const off = window.api.on('tester:event', (ev) => {
      if (ev.type === 'strategy-start') {
        document.querySelectorAll('.strat-card.testing').forEach((c) => c.classList.remove('testing'));
        liveCard = openLiveSpoiler(ev.name);
      } else if (ev.type === 'strategy-fail') {
        if (liveCard) {
          liveCard.classList.remove('testing');
          liveCard.classList.add('verdict-red');
          const badges = liveCard.querySelector('.strat-badges');
          if (badges && !badges.querySelector('.strat-verdict')) {
            const vd = document.createElement('span');
            vd.className = 'strat-verdict red';
            vd.textContent = t('verdict_dead');
            badges.appendChild(vd);
          }
          liveCard = null;
        }
      } else if (ev.type === 'target') {
        if (!liveCard) return;
        const res = ev.result;
        if (typeof res === 'string') {
          liveRow(liveCard, `${ev.target}: ${res}`, res === 'Timeout' ? 'tl-err' : 'tl-ok');
        } else if (ev.target === 'YouTubeVideoSpeed') {
          const v = res.verdict;
          const txt = v === 'ok'
            ? `YouTubeVideoSpeed: ${fmtSpeed(res.bytesPerSec)} — OK`
            : v === 'slow'
              ? `YouTubeVideoSpeed: ${fmtSpeed(res.bytesPerSec)} — ${t('yt_throttled').toUpperCase()}`
              : v === 'unavailable' ? `YouTubeVideoSpeed: ${t('yt_unavailable')}` : `YouTubeVideoSpeed: ${t('yt_fail')} (HTTP ${res.http})`;
          liveRow(liveCard, txt, v === 'ok' ? 'tl-ok' : v === 'slow' ? 'tl-warn' : 'tl-err');
        } else {
          const parts = Object.entries(res).map(([k, v]) => `${k}:${v}`).join('  ');
          liveRow(liveCard, `${ev.target}  ${parts}`);
        }
      } else if (ev.type === 'stage') {
        if (liveCard) liveRow(liveCard, ev.stage === 'yt_resolve' ? t('stage_yt_resolve') : t('stage_yt_speed'), 'tl-dim');
      } else if (ev.type === 'strategy-done') {
        if (liveCard) {
          liveCard.classList.remove('testing');
          const v = verdictOf({ failed: false, groups: ev.groups });
          liveCard.classList.add(`verdict-${v}`);
          const badges = liveCard.querySelector('.strat-badges');
          if (badges && !badges.querySelector('.strat-verdict')) {
            const vd = document.createElement('span');
            vd.className = `strat-verdict ${v}`;
            vd.textContent = verdictText({ failed: false, groups: ev.groups });
            badges.appendChild(vd);
          }
          liveCard = null;
        }
      } else if (ev.type === 'aborted') {
        if (liveCard) liveCard.classList.remove('testing');
      } else if (ev.type === 'complete') {
        $('#testing-note').classList.add('hidden');
        $('#test-progress').classList.add('hidden');
      }
    });
    const r = await window.api.runTests();
    off?.();
    testing = false;
    setButtons(false);
    document.querySelectorAll('.strat-card.testing').forEach((c) => c.classList.remove('testing'));
    if (r && r.ok) {
      lastTestResults = { results: r.results, winner: r.winner };
      await renderStrategies();
    } else if (r && r.reason === 'admin_required') {
      toast(t('test_need_admin'), 'err');
    }
  });
  $('#btn-test-stop').addEventListener('click', () => window.api.abortTests());
}

function escapeHtml(s) {
  return String(s).replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
}

// ---------- filters ----------
async function renderFilters() {
  const f = await window.api.getFilters();
  if (!f) return;
  $$('.seg-btn[data-gf]').forEach((b) => b.classList.toggle('active', b.dataset.gf === f.game.mode));
  $('#gf-tcp').value = f.game.tcp;
  $('#gf-udp').value = f.game.udp;
  $$('.seg-btn[data-ipset]').forEach((b) => b.classList.toggle('active', b.dataset.ipset === f.ipset));
  $('#ipset-backup-note').classList.toggle('hidden', f.ipsetBackup || f.ipset !== 'loaded');
}

function bindFilters() {
  $$('.seg-btn[data-gf]').forEach((b) =>
    b.addEventListener('click', async () => {
      const gf = { mode: b.dataset.gf, tcp: $('#gf-tcp').value, udp: $('#gf-udp').value };
      const r = await window.api.setGameFilter(gf);
      toast(r.ok ? t('toast_saved') : t('toast_error'), r.ok ? 'ok' : 'err');
      renderFilters();
    })
  );
  $$('.seg-btn[data-ipset]').forEach((b) =>
    b.addEventListener('click', async () => {
      const r = await window.api.setIpsetMode(b.dataset.ipset);
      toast(r.ok ? t('toast_saved') : `${t('toast_error')}: ${r.reason || ''}`, r.ok ? 'ok' : 'err');
      renderFilters();
    })
  );
}

// ---------- lists ----------
async function renderLists() {
  await window.api.ensureLists();
  loadList(currentList);
}

async function loadList(key) {
  currentList = key;
  $$('.tab').forEach((b) => b.classList.toggle('active', b.dataset.list === key));
  const editor = $('#list-editor');
  const isEditable = listsEditable.includes(key);
  if (key === 'ipsetAll') {
    const r = await window.api.readIpsetAll();
    editor.value = r.ok ? r.content : '';
  } else {
    const r = await window.api.readList(key);
    editor.value = r.ok ? r.content : '';
  }
  editor.readOnly = !isEditable;
  $('#lists-hint').textContent = key === 'general' ? t('lists_hint_general') : '';
  $('#list-save').classList.toggle('hidden', !isEditable);
}

function bindLists() {
  $$('.tab').forEach((b) => b.addEventListener('click', () => loadList(b.dataset.list)));
  $('#list-save').addEventListener('click', async () => {
    const r = await window.api.writeList(currentList, $('#list-editor').value);
    toast(r.ok ? t('lists_saved') : t('toast_error'), r.ok ? 'ok' : 'err');
  });
}

// ---------- updates ----------
const PANEL_RELEASES_URL = 'https://github.com/Therealwh/ZapretYoutubeDiscordPanel/releases/latest';

// Panel self-update card: reflects updater state machine + actions.
async function renderPanelUpdater() {
  const st = await window.api.updaterState();
  $('#panel-current').textContent = st.current;
  $('#panel-latest').textContent = st.version || '—';
  const status = $('#panel-status');
  const btnCheck = $('#btn-panel-check');
  const btnDl = $('#btn-panel-download');
  const btnInst = $('#btn-panel-install');
  const btnOpen = $('#btn-panel-open');
  const progWrap = $('#panel-progress-wrap');
  btnDl.classList.add('hidden');
  btnInst.classList.add('hidden');
  progWrap.classList.add('hidden');
  switch (st.status) {
    case 'available':
      status.textContent = t('panel_available', { version: st.version });
      status.className = 'ver-status warn';
      btnDl.classList.remove('hidden');
      btnOpen.classList.remove('hidden');
      break;
    case 'progress':
      status.textContent = t('panel_downloading', { percent: st.percent || 0 });
      status.className = 'ver-status';
      progWrap.classList.remove('hidden');
      $('#panel-progress').style.width = `${st.percent || 0}%`;
      break;
    case 'downloaded':
      status.textContent = t('panel_downloaded');
      status.className = 'ver-status ok';
      btnInst.classList.remove('hidden');
      break;
    case 'checking':
      status.textContent = t('panel_checking');
      status.className = 'ver-status';
      break;
    case 'error':
      status.textContent = t('panel_error');
      status.className = 'ver-status err';
      break;
    default:
      status.textContent = t('panel_uptodate');
      status.className = 'ver-status ok';
  }
  btnCheck.disabled = st.status === 'progress' || st.status === 'checking';
  $('#panel-portable-note').classList.toggle('hidden', !st.portable);
}

function bindPanelUpdater() {
  $('#btn-panel-check').addEventListener('click', async () => {
    renderPanelUpdater();
    await window.api.updaterCheck();
    renderPanelUpdater();
  });
  $('#btn-panel-download').addEventListener('click', async () => {
    const r = await window.api.updaterDownload();
    if (!r.ok) toast(`${t('toast_error')}: ${r.reason || ''}`, 'err');
    renderPanelUpdater();
  });
  $('#btn-panel-install').addEventListener('click', () => window.api.updaterInstall());
  $('#btn-panel-open').addEventListener('click', () => window.api.openExternal(PANEL_RELEASES_URL));
  window.api.on('updater:event', () => {
    renderPanelUpdater().catch(() => {});
  });
}

async function renderUpdates() {
  const v = await window.api.checkVersion();
  $('#ver-local').textContent = v.local || t('updates_version_unknown');
  $('#ver-remote').textContent = v.remote || t('updates_version_unknown');
  const st = $('#ver-status');
  if (!v.ok) {
    st.textContent = t('toast_error');
    st.className = 'ver-status err';
  } else if (v.upToDate) {
    st.textContent = t('updates_uptodate');
    st.className = 'ver-status ok';
    $('#btn-ver-install').classList.add('hidden');
  } else {
    st.textContent = t('updates_new');
    st.className = 'ver-status warn';
    $('#btn-ver-install').classList.remove('hidden');
    $('#btn-ver-open').classList.remove('hidden');
  }
}

function bindUpdates() {
  $('#btn-ver-check').addEventListener('click', renderUpdates);
  $('#btn-ver-open').addEventListener('click', () => window.api.openExternal('https://github.com/Flowseal/zapret-discord-youtube/releases/latest'));
  $('#btn-ver-install').addEventListener('click', async () => {
    if (!confirm(t('updates_confirm_install'))) return;
    const dir = (cfg.zapretRoot || 'C:\\zapret').trim().replace(/[\\/]+$/, '');
    if (!/^[a-zA-Z]:\\.+/i.test(dir)) {
      toast(t('setup_path_invalid'), 'err');
      return;
    }
    const btn = $('#btn-ver-install');
    btn.disabled = true;
    $('#btn-ver-check').disabled = true;
    $('#install-progress-wrap').classList.remove('hidden');
    $('#install-progress').style.width = '0%';
    const setStage = (s) => {
      const el = $('#install-stage');
      el.textContent = t(`setup_stage_${s}`) || s;
      el.classList.remove('hidden');
    };
    setStage('resolve');
    const offStage = window.api.on('install:stage', setStage);
    const offP = window.api.on('install:progress', (frac) => {
      $('#install-progress').style.width = `${Math.round(frac * 100)}%`;
    });
    try {
      const res = await window.api.installZapret(dir);
      if (res.ok) {
        cfg = await window.api.setConfig({ zapretRoot: dir });
        toast(t('setup_done'), 'ok');
      } else if (res.reason === 'swap_failed_locked' || res.reason === 'swap_failed') {
        toast(t('err_swap_locked'), 'err');
      } else {
        actionToast(res);
      }
    } catch {
      toast(t('toast_error'), 'err');
    } finally {
      offStage?.();
      offP?.();
      $('#install-progress-wrap').classList.add('hidden');
      $('#install-stage').classList.add('hidden');
      btn.disabled = false;
      $('#btn-ver-check').disabled = false;
      renderUpdates();
    }
  });
  $('#btn-ipset-update').addEventListener('click', async () => {
    const r = await window.api.updateIpset();
    toast(r.ok ? t('lists_saved') : t('toast_error'), r.ok ? 'ok' : 'err');
  });
  $('#btn-hosts-check').addEventListener('click', async () => {
    const r = await window.api.updateHosts({ auto: false });
    const st = $('#hosts-status');
    if (r.manual) {
      st.textContent = t('updates_hosts_need');
      st.className = 'ver-status warn';
      $('#btn-hosts-auto').classList.remove('hidden');
      $('#btn-hosts-manual').classList.remove('hidden');
      window.api.showItemInFolder(r.tempFile);
    } else if (r.ok) {
      st.textContent = t('updates_hosts_ok');
      st.className = 'ver-status ok';
    } else {
      st.textContent = t('toast_error');
      st.className = 'ver-status err';
    }
  });
  $('#btn-hosts-auto').addEventListener('click', async () => {
    const r = await window.api.updateHosts({ auto: true });
    toast(r.ok ? t('lists_saved') : t('toast_error'), r.ok ? 'ok' : 'err');
  });
  $('#btn-hosts-manual').addEventListener('click', () => window.api.openExternal('https://raw.githubusercontent.com/Flowseal/zapret-discord-youtube/refs/heads/main/.service/hosts'));
}

// ---------- diagnostics ----------
async function renderDiagnostics() {
  const wrap = $('#diag-list');
  if (!wrap.dataset.ran) {
    wrap.innerHTML = `<p class="muted">${t('diag_run')} ↓</p>`;
  }
}

const DIAG_NAMES = {
  bfe: 'diag_bfe', proxy: 'diag_proxy', tcp_timestamps: 'diag_tcp_timestamps', adguard: 'diag_adguard',
  killer: 'diag_killer', intel_connectivity: 'diag_intel_connectivity', check_point: 'diag_check_point',
  smartbyte: 'diag_smartbyte', path: 'diag_path', windivert_sys: 'diag_windivert_sys', vpn: 'diag_vpn',
  secure_dns: 'diag_secure_dns', hosts_youtube: 'diag_hosts_youtube', conflict_services: 'diag_conflict_services',
};

function diagDetail(item) {
  const key = `diag_detail_${item.detail}`;
  const dictHas = key in DICT[currentLang] || key in DICT.ru;
  if (!dictHas) return '';
  return t(key, { server: item.meta?.server || '', names: item.meta?.names || '', name: item.meta?.name || '' });
}

function bindDiagnostics() {
  $('#btn-diag-run').addEventListener('click', async () => {
    const wrap = $('#diag-list');
    wrap.textContent = '';
    const items = await window.api.runDiagnostics();
    wrap.dataset.ran = '1';
    for (const item of items) {
      const el = document.createElement('div');
      el.className = 'diag-item';
      const main = document.createElement('div');
      main.className = 'diag-main';
      const dot = document.createElement('span');
      dot.className = `diag-dot ${item.severity === 'ok' ? 'ok' : item.severity === 'warn' ? 'warn' : 'err'}`;
      const text = document.createElement('div');
      text.className = 'diag-text';
      const name = document.createElement('div');
      name.className = 'diag-name';
      name.textContent = t(DIAG_NAMES[item.id] || item.id);
      text.appendChild(name);
      const detail = diagDetail(item);
      if (item.severity !== 'ok' && detail) {
        const d = document.createElement('div');
        d.className = 'diag-detail';
        d.textContent = detail;
        text.appendChild(d);
      }
      main.append(dot, text);
      el.appendChild(main);
      if (item.link) {
        const link = mkBtn(t('diag_fix'), 'btn btn-ghost btn-sm', () => window.api.openExternal(item.link));
        el.appendChild(link);
      }
      wrap.appendChild(el);
    }
  });
  $('#btn-discord-cache').addEventListener('click', async () => {
    const r = await window.api.clearDiscordCache();
    toast(r.foundAny ? t('diag_clear_done') : t('diag_no_discord'), r.foundAny ? 'ok' : 'warn');
  });
}

// ---------- settings ----------
async function renderSettings() {
  $('#set-root-path').textContent = cfg.zapretRoot || '—';
  $('#set-admin').textContent = appInfo.admin ? t('settings_admin_yes') : t('settings_admin_no');
  $('#set-admin').className = `chip ${appInfo.admin ? 'ok' : 'err'}`;
  $('#opt-close-tray').checked = cfg.closeToTray !== false;
  $('#opt-autostart').checked = !!cfg.autostart;
  $('#opt-start-hidden').checked = !!cfg.startHidden;
  $$('.seg-btn[data-lang]').forEach((b) => b.classList.toggle('active', b.dataset.lang === currentLang));
  $$('.seg-btn[data-theme]').forEach((b) => b.classList.toggle('active', b.dataset.theme === (cfg.theme || 'dark')));
}

function bindSettings() {
  $$('.seg-btn[data-lang]').forEach((b) =>
    b.addEventListener('click', async () => {
      setLang(b.dataset.lang);
      currentLang = b.dataset.lang;
      cfg = await window.api.setConfig({ lang: currentLang });
      applyI18n();
      renderSettings();
      toast(t('toast_saved'), 'ok');
    })
  );
  $$('.seg-btn[data-theme]').forEach((b) =>
    b.addEventListener('click', async () => {
      applyTheme(b.dataset.theme);
      cfg = await window.api.setConfig({ theme: b.dataset.theme });
      renderSettings();
    })
  );
  $('#opt-close-tray').addEventListener('change', async (e) => {
    cfg = await window.api.setConfig({ closeToTray: e.target.checked });
  });
  $('#opt-autostart').addEventListener('change', async (e) => {
    cfg = await window.api.setConfig({ autostart: e.target.checked });
    // autostart via app.setLoginItemSettings is wired in main through config read
    await window.api.setConfig({ _autostartChanged: true });
    toast(t('toast_saved'), 'ok');
  });
  $('#opt-start-hidden').addEventListener('change', async (e) => {
    cfg = await window.api.setConfig({ startHidden: e.target.checked });
  });
  $('#opt-vt-key').value = cfg.virustotalKey || '';
  $('#opt-vt-key').addEventListener('change', async (e) => {
    cfg = await window.api.setConfig({ virustotalKey: e.target.value.trim() || null });
    toast(t('toast_saved'), 'ok');
  });
  $('#btn-root-change').addEventListener('click', async () => {
    const dir = await window.api.chooseDir();
    if (!dir) return;
    const v = await window.api.validateRoot(dir);
    if (v.ok) {
      cfg = await window.api.setConfig({ zapretRoot: dir });
      renderSettings();
    } else {
      toast(t('setup_not_found'), 'err');
    }
  });
}

// ---------- power ----------
function bindPower() {
  // One-UAC flow: save the intended action, restart elevated, auto-run it.
  const ensureAdminThen = (pendingAction) => {
    if (appInfo.admin) return false;
    window.api.restartElevated(pendingAction);
    toast(t('banner_admin_fix'), 'ok');
    return true;
  };
  $('#power-switch').addEventListener('click', async () => {
    const { status: st } = await window.api.getStatus();
    if (st.activeMode !== 'off') {
      if (ensureAdminThen('toggle-off')) return;
      if (st.activeMode === 'service') await window.api.stopService();
      await window.api.killWinws();
    } else {
      if (ensureAdminThen('toggle-on')) return;
      const last = cfg.lastStrategyFile;
      const strategies = await window.api.listStrategies();
      const target = strategies.find((s) => s.file === last) || strategies[0];
      if (!target) {
        toast(t('toast_need_root'), 'err');
        return;
      }
      const preferService = cfg.preferServiceMode !== false;
      const r = preferService ? await window.api.installService(target.file) : await window.api.launchProcess(target.file);
      actionToast(r);
    }
    setTimeout(refreshStatus, 1500);
  });
  $('#btn-restart').addEventListener('click', async () => {
    if (ensureAdminThen('toggle-on')) return;
    await window.api.killWinws();
    await window.api.stopService();
    setTimeout(async () => {
      const target = cfg.lastStrategyFile;
      const strategies = await window.api.listStrategies();
      const s = strategies.find((x) => x.file === target) || strategies[0];
      if (s) {
        if (cfg.preferServiceMode !== false) await window.api.installService(s.file);
        else await window.api.launchProcess(s.file);
      }
      setTimeout(refreshStatus, 1500);
    }, 1200);
  });
  $('#banner-disable').addEventListener('click', async () => {
    if (lastBanner.kind === 'conflict') {
      await window.api.elevateCommands([
        `net stop ${lastBanner.name}`,
        `sc delete ${lastBanner.name}`,
      ]);
    } else {
      if (ensureAdminThen('toggle-off')) return;
      await window.api.stopService();
      await window.api.killWinws();
    }
    sessionStorage.removeItem('bannerDismissed');
    setTimeout(refreshStatus, 1500);
  });
  $('#banner-dismiss').addEventListener('click', () => {
    sessionStorage.setItem('bannerDismissed', '1');
    renderBanner({ active: false });
  });
  $('#banner-admin-fix').addEventListener('click', () => window.api.restartElevated());
}

// ---------- titlebar / nav ----------
function bindChrome() {
  $('#btn-min').addEventListener('click', () => window.api.windowAction('minimize'));
  $('#btn-max').addEventListener('click', () => window.api.windowAction('maximize'));
  $('#btn-close').addEventListener('click', () => window.api.windowAction('close'));
  $$('.nav-item').forEach((b) => b.addEventListener('click', () => showPage(b.dataset.page)));
  $('#theme-toggle').addEventListener('click', async () => {
    const next = cfg.theme === 'light' ? 'dark' : 'light';
    applyTheme(next);
    cfg = await window.api.setConfig({ theme: next });
  });
  $('#lang-toggle').addEventListener('click', async () => {
    const next = currentLang === 'ru' ? 'en' : 'ru';
    setLang(next);
    currentLang = next;
    cfg = await window.api.setConfig({ lang: next });
    applyI18n();
  });
}

// ---------- boot ----------
(async function boot() {
  cfg = await window.api.getConfig();
  appInfo = await window.api.appInfo();
  currentLang = cfg.lang || detectLang(appInfo.lang);
  setLang(currentLang);
  applyTheme(cfg.theme || 'dark');
  applyI18n();
  bindChrome();
  bindSetup();
  bindPower();
  bindTester();
  bindFilters();
  bindLists();
  bindUpdates();
  bindPanelUpdater();
  bindDiagnostics();
  bindSettings();

  if (!cfg.zapretRoot) {
    await setupInit();
    $('#setup-view').classList.remove('hidden');
    $('#main-view').classList.add('hidden');
  } else {
    enterApp();
  }

  // After the UAC restart the renderer must finish what was started before
  // the relaunch (e.g. strategy auto-search).
  if (cfg.pendingAction === 'run-tests' && appInfo.admin) {
    await window.api.setConfig({ pendingAction: null });
    if (cfg.zapretRoot) {
      showPage('strategies');
      setTimeout(() => $('#btn-find-working').click(), 600);
    }
  }
})();
})();
