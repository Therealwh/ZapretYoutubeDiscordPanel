'use strict';
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const { exists } = require('../util');

const ACTIVE_DISCORD = 'ACTIVE_DISCORD_UDP.bin';
const ACTIVE_GAME = 'ACTIVE_GAME_UDP.bin';

function sha256(file) {
  return crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex').toLowerCase();
}

// List .bin fakes in bin/ and resolve which are active by hash comparison
function listFakes(rootDir) {
  const bin = path.join(rootDir, 'bin');
  const discordActive = path.join(bin, ACTIVE_DISCORD);
  const gameActive = path.join(bin, ACTIVE_GAME);
  const hashes = { discord: null, game: null };
  try {
    if (exists(discordActive)) hashes.discord = sha256(discordActive);
    if (exists(gameActive)) hashes.game = sha256(gameActive);
  } catch {
    /* ignore */
  }
  let files = [];
  try {
    files = fs
      .readdirSync(bin)
      .filter((n) => n.toLowerCase().endsWith('.bin') && !/^ACTIVE_/i.test(n))
      .map((n) => {
        const file = path.join(bin, n);
        let hash = null;
        try {
          hash = sha256(file);
        } catch {
          /* unreadable file */
        }
        return {
          name: path.basename(n, '.bin'),
          file,
          hash,
          activeDiscord: !!hash && hash === hashes.discord,
          activeGame: !!hash && hash === hashes.game,
        };
      });
  } catch {
    /* no bin dir */
  }
  return { fakes: files, hasDiscordActive: exists(discordActive), hasGameActive: exists(gameActive) };
}

// Replace ACTIVE_*.bin with the chosen fake (copy, as service.bat does)
function setFake(rootDir, kind, fakeFile) {
  const bin = path.join(rootDir, 'bin');
  const target = kind === 'discord' ? path.join(bin, ACTIVE_DISCORD) : kind === 'game' ? path.join(bin, ACTIVE_GAME) : null;
  if (!target) return { ok: false, reason: 'bad_kind' };
  if (!exists(fakeFile) || !path.dirname(fakeFile).toLowerCase().startsWith(bin.toLowerCase())) {
    return { ok: false, reason: 'bad_source' };
  }
  try {
    fs.copyFileSync(fakeFile, target);
    return { ok: true };
  } catch {
    return { ok: false, reason: 'copy_failed' };
  }
}

module.exports = { listFakes, setFake, ACTIVE_DISCORD, ACTIVE_GAME };
