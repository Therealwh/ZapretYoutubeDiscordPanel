'use strict';
// Mock bridge for the Tauri spike.
//
// The dashboard is the REAL renderer (index.html + styles.css + i18n.js +
// renderer.js). Only the Electron preload bridge is missing here, so every
// window.api.* call resolves to mock data instead of talking to a main
// process. Lets us measure the real UI (same DOM, same CSS, same fonts)
// without porting the backend yet.
(function () {
  const OK = { ok: true };
  const STRATEGIES = [
    { file: 'C:/Zapret/general (ALT).bat', name: 'general (ALT).bat', title: '(ALT)', badges: ['fake', 'ts'], desync: 'fake,fakedsplit' },
    { file: 'C:/Zapret/general (ALT7).bat', name: 'general (ALT7).bat', title: '(ALT7)', badges: ['badseq'], desync: 'multidisorder' },
    { file: 'C:/Zapret/general (SIMPLE FAKE).bat', name: 'general (SIMPLE FAKE).bat', title: '(SIMPLE FAKE)', badges: ['fake'], desync: 'fake' },
    { file: 'C:/Zapret/general.bat', name: 'general.bat', title: '', badges: [], desync: 'split2' },
  ];
  const LOGS = [
    { ts: '21:04:11', level: 'info', source: 'panel', msg: 'Panel 1.9.2 started (admin=true)' },
    { ts: '21:04:11', level: 'info', source: 'zapret', msg: 'WinDivert service installed, start=auto' },
    { ts: '21:04:12', level: 'info', source: 'tester', msg: 'strategy general (ALT7).bat: Discord OK, YouTube OK' },
    { ts: '21:04:12', level: 'info', source: 'autoconnect', msg: 'Working bypass connected: general (ALT7).bat' },
    { ts: '21:05:02', level: 'warn', source: 'tg-proxy', msg: 'DC2: ws kws2.web.telegram.org failed (ws_handshake_timeout)' },
    { ts: '21:05:04', level: 'warn', source: 'tg-proxy', msg: 'DC2: no Cloudflare relay available' },
  ];

  const MOCK = {
    getStatus: () => ({
      status: {
        activeMode: 'service',
        service: { exists: true, running: true, state: 'RUNNING', name: 'zapret', startMode: 'auto' },
        winDivert: { exists: true, running: true, state: 'RUNNING', name: 'WinDivert64' },
        winwsProcess: true,
        winwsProcessPath: 'C:\\Zapret\\bin\\winws.exe',
        serviceStrategy: 'general (ALT7).bat',
        rootDir: 'C:/Zapret',
        conflicts: [],
      },
      banner: { active: false },
    }),
    appInfo: () => ({ version: '1.9.2', electron: '', chrome: '', node: '', platform: 'win32', arch: 'x64', packaged: false }),
    getConfig: () => ({
      zapretRoot: 'C:/Zapret', lang: 'ru', theme: 'dark', autoConnect: false, preferServiceMode: true,
      gameFilter: false, ipsetMode: 'off', dnsFix: false, tgProxyPort: 1443, tgProxyAuto: false,
    }),
    setConfig: () => OK,
    ownership: () => ({ kind: 'none', foreign: null }),
    runningInfo: () => ({ winwsRoot: 'C:/Zapret', service: 'zapret', running: true }),
    discoverZapret: () => [{ path: 'C:/Zapret', hasWinws: true, hasService: true }],
    validateRoot: () => ({ ok: true, writable: true, hasWinws: true }),
    listStrategies: () => STRATEGIES,
    lastTestResults: () => ({ ok: true, results: [], winner: 'general (ALT7).bat', file: 'test_results.json' }),
    logBuffer: () => LOGS,
    tgStatus: () => ({ running: false, host: '127.0.0.1', port: 1443, connections: 0, bytesUp: 0, bytesDown: 0, proxyUrl: '' }),
    updaterState: () => ({ checking: false, available: false, version: '' }),
    updaterCheck: () => ({ available: false }),
    getFilters: () => ({ gameFilter: false, ipsetMode: 'off', dnsFix: false }),
    engineCurrent: () => ({ ok: true, tag: 'v1.10.3' }),
    engineReleases: () => ({ ok: true, releases: [] }),
    runDiagnostics: () => ({ ok: true, items: [{ name: 'WinDivert', ok: true }, { name: 'zapret service', ok: true }] }),
    on: () => () => {},
  };

  // Every preload method is async in the real bridge, so the mock returns
  // promises too — a sync value silently breaks `await api.x().then(...)`.
  const async = (fn) => (...args) => Promise.resolve(fn(...args));

  window.api = new Proxy({}, {
    get(_t, key) {
      if (typeof key !== 'string') return undefined;
      // Event subscriptions are synchronous and must return an unsubscribe fn.
      if (key === 'on') return () => () => {};
      if (MOCK[key]) return async(MOCK[key]);
      // Everything else resolves to a harmless empty value.
      return () => Promise.resolve(OK);
    },
  });
})();
