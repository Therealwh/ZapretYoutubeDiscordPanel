'use strict';
// Black-window recovery ladder. The renderer can die right at startup for
// reasons that have nothing to do with our code: GPU/driver bugs, or security
// software injecting DLLs that Chromium's code-integrity / sandbox rejects.
// Each step is persisted in config.json and applied on the NEXT launch
// (Chromium switches can only be set before the app is ready), so we relaunch.
//
//   1. reload the page (transient crash)
//   2. relaunch with hardware acceleration disabled   (GPU / driver)
//   3. relaunch in safe renderer mode                 (AV / code integrity / sandbox)
//   4. give up and tell the user where the log is

const FATAL_REASONS = new Set(['crashed', 'oom', 'launch-failed', 'integrity-failure', 'abnormal-exit', 'killed']);

// Chromium switches for safe renderer mode.
const SAFE_SWITCHES = [
  ['disable-features', 'RendererCodeIntegrity'],
  ['no-sandbox'],
];

/**
 * @param {{crashes:number, reason:string, gpuOff:boolean, safeMode:boolean}} s
 *   crashes — renderer crashes in THIS launch, including the current one
 * @returns {{action:'reload'|'relaunch'|'give-up'|'none', set?:object}}
 */
function decideRecovery({ crashes, reason, gpuOff, safeMode }) {
  if (!FATAL_REASONS.has(reason)) return { action: crashes <= 3 ? 'reload' : 'none' };
  // Code-integrity rejection is unambiguous: skip the GPU step.
  if (reason === 'integrity-failure' && !safeMode) {
    return { action: 'relaunch', set: { safeRenderer: true } };
  }
  if (crashes < 2) return { action: 'reload' };
  if (!gpuOff) return { action: 'relaunch', set: { disableHardwareAcceleration: true } };
  if (!safeMode) return { action: 'relaunch', set: { safeRenderer: true } };
  return { action: crashes <= 3 ? 'reload' : 'give-up' };
}

// Windows exit codes are NTSTATUS values; hex makes them searchable
// (e.g. -1073741819 -> 0xC0000005 access violation).
function formatExitCode(code) {
  if (typeof code !== 'number' || !Number.isFinite(code)) return 'n/a';
  return `${code} (0x${(code >>> 0).toString(16).toUpperCase().padStart(8, '0')})`;
}

module.exports = { decideRecovery, formatExitCode, SAFE_SWITCHES, FATAL_REASONS };
