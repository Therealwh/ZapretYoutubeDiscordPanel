'use strict';
// Regression tests for `sc query` parsing.
//
// WinDivert is a KERNEL_DRIVER and `sc query` prints its TYPE line with the
// value "1" BEFORE the STATE line. Parsing the first number therefore reported
// a running driver as STOPPED — exactly the "WinDivert service not visible"
// complaint in issue #3.
const { test } = require('node:test');
const assert = require('node:assert');
const { parseScState } = require('../src/main/zapret/status');

const RU_RUNNING_DRIVER = [
  '',
  'ИМЯ_СЛУЖБЫ: WinDivert ',
  '        ТИП               : 1  KERNEL_DRIVER  ',
  '        СОСТОЯНИЕ          : 4  RUNNING',
  '        WIN32_EXIT_CODE    : 0  (0x0)',
  '',
].join('\r\n');

const EN_RUNNING_OWN = [
  '',
  'SERVICE_NAME: zapret ',
  '        TYPE               : 10  WIN32_OWN_PROCESS  ',
  '        STATE              : 4  RUNNING ',
  '        WIN32_EXIT_CODE    : 0  (0x0)',
  '',
].join('\r\n');

const RU_STOPPED = [
  '',
  'ИМЯ_СЛУЖБЫ: zapret ',
  '        ТИП               : 10  WIN32_OWN_PROCESS  ',
  '        СОСТОЯНИЕ          : 1  STOPPED ',
  '',
].join('\r\n');

test('reads RUNNING for a kernel driver whose TYPE is also 1', () => {
  assert.strictEqual(parseScState(RU_RUNNING_DRIVER), 4);
});

test('reads RUNNING for a normal service', () => {
  assert.strictEqual(parseScState(EN_RUNNING_OWN), 4);
});

test('reads STOPPED without being confused by the TYPE value', () => {
  assert.strictEqual(parseScState(RU_STOPPED), 1);
});

test('still falls back to the first number when there is no TYPE line', () => {
  assert.strictEqual(parseScState('        STATE : 3  STOP_PENDING'), 3);
});

test('returns null for empty or unreadable output', () => {
  assert.strictEqual(parseScState(''), null);
  assert.strictEqual(parseScState('нет данных'), null);
});

test('START_PENDING is distinguishable from RUNNING', () => {
  const pending = ['', 'SERVICE_NAME: zapret ', '  TYPE : 10  WIN32_OWN_PROCESS ', '  STATE : 2  START_PENDING ', ''].join('\n');
  assert.strictEqual(parseScState(pending), 2);
});