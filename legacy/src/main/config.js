'use strict';
const fs = require('node:fs');
const path = require('node:path');

let file = null;
let data = {};

function init(userDataDir) {
  file = path.join(userDataDir, 'config.json');
  try {
    data = JSON.parse(fs.readFileSync(file, 'utf8'));
  } catch {
    data = {};
  }
}

// Test/dev bootstrap without Electron userData
function initAt(dir) {
  init(dir);
}

function save() {
  if (!file) return;
  try {
    fs.mkdirSync(path.dirname(file), { recursive: true });
    fs.writeFileSync(file, JSON.stringify(data, null, 2), 'utf8');
  } catch {
    /* best effort */
  }
}

function get(key, fallback) {
  return key in data && data[key] !== undefined ? data[key] : fallback;
}

function set(key, value) {
  data[key] = value;
  save();
  return value;
}

function all() {
  return { ...data };
}

module.exports = { init, initAt, get, set, all, save };
