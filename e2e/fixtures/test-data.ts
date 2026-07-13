export const backupPassword = 'playwright-backup-password';

export function uniqueName(prefix: string): string {
  return `${prefix}-${Date.now()}-${Math.random().toString(16).slice(2, 8)}`;
}

export function fakeIrohNodeId(seed: string): string {
  const hex = Buffer.from(seed).toString('hex').padEnd(64, '0');
  return hex.slice(0, 64);
}
