'use strict';
const fs = require('node:fs');
const path = require('node:path');
const { run, exists } = require('../util');

// Discord cache clearing (Stable, PTB, Canary, Development) — mirrors service.bat
const VARIANTS = [
  { exe: 'Discord.exe', dir: 'discord', name: 'Discord' },
  { exe: 'DiscordPTB.exe', dir: 'discordptb', name: 'Discord PTB' },
  { exe: 'DiscordCanary.exe', dir: 'discordcanary', name: 'Discord Canary' },
  { exe: 'DiscordDevelopment.exe', dir: 'discorddevelopment', name: 'Discord Development' },
];

async function clearDiscordCache() {
  const results = [];
  const appdata = process.env.APPDATA;
  if (!appdata) return { ok: false, reason: 'no_appdata', results };
  for (const v of VARIANTS) {
    const dir = path.join(appdata, v.dir);
    if (!exists(dir)) continue;
    // kill if running
    await run('taskkill', ['/IM', v.exe, '/F'], { timeout: 6000 });
    const removed = [];
    const failed = [];
    for (const sub of ['Cache', 'Code Cache', 'GPUCache']) {
      const p = path.join(dir, sub);
      if (!exists(p)) continue;
      try {
        fs.rmSync(p, { recursive: true, force: true });
        removed.push(sub);
      } catch {
        failed.push(sub);
      }
    }
    results.push({ name: v.name, removed, failed });
  }
  return { ok: results.length > 0, foundAny: results.length > 0, results };
}

module.exports = { clearDiscordCache };
