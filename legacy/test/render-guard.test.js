'use strict';
const { test } = require('node:test');
const assert = require('node:assert');
const { decideRecovery, formatExitCode } = require('../src/main/render-guard');

const base = { reason: 'crashed', gpuOff: false, safeMode: false };

test('first crash -> reload', () => {
  assert.deepStrictEqual(decideRecovery({ ...base, crashes: 1 }), { action: 'reload' });
});

test('second crash with GPU on -> relaunch with GPU off', () => {
  assert.deepStrictEqual(decideRecovery({ ...base, crashes: 2 }),
    { action: 'relaunch', set: { disableHardwareAcceleration: true } });
});

test('second crash with GPU already off -> relaunch in safe renderer mode', () => {
  assert.deepStrictEqual(decideRecovery({ ...base, crashes: 2, gpuOff: true }),
    { action: 'relaunch', set: { safeRenderer: true } });
});

test('integrity-failure goes straight to safe mode', () => {
  assert.deepStrictEqual(decideRecovery({ ...base, reason: 'integrity-failure', crashes: 1 }),
    { action: 'relaunch', set: { safeRenderer: true } });
});

test('everything already tried -> reload a bit, then give up (no relaunch loop)', () => {
  const s = { ...base, gpuOff: true, safeMode: true };
  assert.strictEqual(decideRecovery({ ...s, crashes: 2 }).action, 'reload');
  assert.strictEqual(decideRecovery({ ...s, crashes: 4 }).action, 'give-up');
  assert.strictEqual(decideRecovery({ ...s, reason: 'integrity-failure', crashes: 9 }).action, 'give-up');
});

test('non-fatal exits only reload, never escalate', () => {
  assert.strictEqual(decideRecovery({ ...base, reason: 'clean-exit', crashes: 2 }).action, 'reload');
  assert.strictEqual(decideRecovery({ ...base, reason: 'memory-eviction', crashes: 5 }).action, 'none');
});

test('formatExitCode renders NTSTATUS hex', () => {
  assert.strictEqual(formatExitCode(-1073741819), '-1073741819 (0xC0000005)');
  assert.strictEqual(formatExitCode(0), '0 (0x00000000)');
  assert.strictEqual(formatExitCode(undefined), 'n/a');
});
