import { expect, Page, TestInfo } from '@playwright/test';
import { spawn, ChildProcessWithoutNullStreams } from 'node:child_process';
import fs from 'node:fs/promises';
import fssync from 'node:fs';
import path from 'node:path';
import net from 'node:net';

export type NexusProcess = {
  process: ChildProcessWithoutNullStreams;
  stdoutPath: string;
  stderrPath: string;
};

export type NexusNodeRuntime = {
  port: number;
  url: string;
  root: string;
  dataRoot: string;
  setupConfig: string;
  process: NexusProcess;
};

export type NexusUiRuntime = {
  port: number;
  url: string;
  process: NexusProcess;
};

const repoRoot = path.resolve(path.join(import.meta.dirname, '..', '..'));
const runtimeRoot = path.join(repoRoot, '.tmp', 'playwright-e2e');

export async function allocatePort(): Promise<number> {
  return await new Promise<number>((resolve, reject) => {
    const server = net.createServer();
    server.once('error', reject);
    server.listen(0, '127.0.0.1', () => {
      const address = server.address();
      if (typeof address === 'object' && address?.port) {
        const port = address.port;
        server.close(() => resolve(port));
      } else {
        server.close(() => reject(new Error('Failed to allocate a free TCP port.')));
      }
    });
  });
}

export async function createRunRoot(testInfo: TestInfo): Promise<string> {
  const safeTitle = testInfo.title.replace(/[^a-z0-9]+/gi, '-').toLowerCase().slice(0, 80);
  const root = path.join(runtimeRoot, `${Date.now()}-${safeTitle}-${testInfo.workerIndex}`);
  await fs.rm(root, { recursive: true, force: true });
  await fs.mkdir(root, { recursive: true });
  return root;
}

export async function startNode(root: string, port?: number): Promise<NexusNodeRuntime> {
  port ??= await allocatePort();
  await fs.mkdir(root, { recursive: true });
  const dataRoot = path.join(root, 'data');
  const setupConfig = path.join(root, 'nexus.setup.json');
  await fs.mkdir(dataRoot, { recursive: true });
  const nodeOutput = path.join(repoRoot, 'Blockchain.Node', 'bin', 'Debug', 'net8.0');
  const process = startProcess('dotnet', [
    path.join(nodeOutput, 'Blockchain.Node.dll')
  ], nodeOutput, root, {
    ASPNETCORE_URLS: `http://127.0.0.1:${port}`,
    NEXUS_SETUP_CONFIG_PATH: setupConfig,
    NEXUS_DATA_PATH: dataRoot,
    OraclePublicKey: '',
    WebhookSecret: '',
    NodeDbPassword: '',
    NodeAdminToken: ''
  });

  const runtime = {
    port,
    url: `http://127.0.0.1:${port}`,
    root,
    dataRoot,
    setupConfig,
    process
  };
  await waitForJson(`${runtime.url}/healthz`, 60_000);
  return runtime;
}

export async function restartNode(node: NexusNodeRuntime): Promise<NexusNodeRuntime> {
  await stopProcess(node.process);
  const samePortAvailable = await waitForPortClosed(node.port, 5_000).then(() => true).catch(() => false);
  const restarted = await startNode(node.root, samePortAvailable ? node.port : undefined);
  return restarted;
}

export async function startUi(root: string, port?: number): Promise<NexusUiRuntime> {
  port ??= await allocatePort();
  await fs.mkdir(root, { recursive: true });
  const process = startProcess('dotnet', [
    'run',
    '--no-build',
    '--no-launch-profile',
    '--project',
    path.join(repoRoot, 'Blockchain.UI', 'Blockchain.UI.csproj')
  ], root, root, {
    ASPNETCORE_URLS: `http://127.0.0.1:${port}`,
    ASPNETCORE_ENVIRONMENT: 'Development'
  });

  const runtime = {
    port,
    url: `http://127.0.0.1:${port}`,
    process
  };
  await waitForHttp(runtime.url, 60_000);
  return runtime;
}

export async function stopAll(...processes: Array<NexusProcess | undefined>): Promise<void> {
  for (const item of processes.reverse()) {
    if (item) {
      await stopProcess(item);
    }
  }
}

export async function waitForJson(url: string, timeoutMs = 30_000): Promise<any> {
  const deadline = Date.now() + timeoutMs;
  let lastError: unknown;
  while (Date.now() < deadline) {
    try {
      const response = await fetch(url);
      if (response.ok) {
        return await response.json();
      }
      lastError = new Error(`${url} returned ${response.status}`);
    } catch (error) {
      lastError = error;
    }
    await delay(500);
  }
  throw lastError instanceof Error ? lastError : new Error(`Timed out waiting for ${url}`);
}

export async function postJson(url: string, body: unknown): Promise<any> {
  const response = await fetch(url, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify(body)
  });
  const text = await response.text();
  if (!response.ok) {
    throw new Error(`${url} returned ${response.status}: ${text}`);
  }
  return text ? JSON.parse(text) : null;
}

export async function attachRuntimeLogs(testInfo: TestInfo, ...processes: Array<NexusProcess | undefined>): Promise<void> {
  for (const item of processes) {
    if (!item) {
      continue;
    }
    await attachFileIfExists(testInfo, path.basename(item.stdoutPath), item.stdoutPath);
    await attachFileIfExists(testInfo, path.basename(item.stderrPath), item.stderrPath);
  }
}

export function attachPageDiagnostics(page: Page): string[] {
  const errors: string[] = [];
  page.on('console', message => {
    if (message.type() === 'error') {
      errors.push(`console error: ${message.text()}`);
    }
  });
  page.on('pageerror', error => errors.push(`page error: ${error.message}`));
  page.on('response', response => {
    if (response.status() >= 500) {
      errors.push(`http ${response.status()}: ${response.url()}`);
    }
  });
  page.on('requestfailed', request => {
    const failure = request.failure();
    errors.push(`request failed: ${request.url()} ${failure?.errorText ?? ''}`.trim());
  });
  return errors;
}

export async function expectNoBrowserErrors(errors: string[]): Promise<void> {
  expect(errors, errors.join('\n')).toEqual([]);
}

export function setupUrl(ui: NexusUiRuntime, node: NexusNodeRuntime): string {
  return `${ui.url}/setup?nodeUrl=${encodeURIComponent(node.url)}`;
}

export function dashboardUrl(ui: NexusUiRuntime, node: NexusNodeRuntime): string {
  return `${ui.url}/?nodeUrl=${encodeURIComponent(node.url)}`;
}

function startProcess(command: string, args: string[], cwd: string, logRoot: string, env: Record<string, string>): NexusProcess {
  const name = `${command}-${Date.now()}-${Math.random().toString(16).slice(2)}`;
  const stdoutPath = path.join(logRoot, `${name}.stdout.log`);
  const stderrPath = path.join(logRoot, `${name}.stderr.log`);
  const stdout = fssync.createWriteStream(stdoutPath, { flags: 'a' });
  const stderr = fssync.createWriteStream(stderrPath, { flags: 'a' });
  const child = spawn(command, args, {
    cwd,
    env: {
      ...process.env,
      ...env
    },
    windowsHide: true,
    detached: process.platform !== 'win32'
  });
  child.stdout.pipe(stdout);
  child.stderr.pipe(stderr);
  return { process: child, stdoutPath, stderrPath };
}

async function stopProcess(item: NexusProcess): Promise<void> {
  if (item.process.exitCode !== null || item.process.killed) {
    return;
  }

  if (process.platform === 'win32') {
    try {
      item.process.kill();
    } catch {
    }
    await delay(500);
    await killWindowsProcessTree(item.process.pid);
  } else if (item.process.pid) {
    try {
      process.kill(-item.process.pid, 'SIGTERM');
    } catch {
      item.process.kill('SIGTERM');
    }
  } else {
    item.process.kill('SIGTERM');
  }

  await delay(1000);
  if (item.process.exitCode === null && !item.process.killed) {
    if (process.platform === 'win32') {
      await killWindowsProcessTree(item.process.pid);
    } else if (item.process.pid) {
      try {
        process.kill(-item.process.pid, 'SIGKILL');
      } catch {
        item.process.kill('SIGKILL');
      }
    } else {
      item.process.kill('SIGKILL');
    }
  }
}

async function killWindowsProcessTree(pid: number | undefined): Promise<void> {
  if (!pid) {
    return;
  }

  await new Promise<void>(resolve => {
    const killer = spawn('taskkill', ['/PID', String(pid), '/T', '/F'], {
      windowsHide: true,
      stdio: 'ignore'
    });
    killer.on('exit', () => resolve());
    killer.on('error', () => resolve());
  });
}

async function waitForHttp(url: string, timeoutMs: number): Promise<void> {
  const deadline = Date.now() + timeoutMs;
  let lastError: unknown;
  while (Date.now() < deadline) {
    try {
      const response = await fetch(url);
      if (response.ok) {
        return;
      }
      lastError = new Error(`${url} returned ${response.status}`);
    } catch (error) {
      lastError = error;
    }
    await delay(500);
  }
  throw lastError instanceof Error ? lastError : new Error(`Timed out waiting for ${url}`);
}

async function waitForPortClosed(port: number, timeoutMs: number): Promise<void> {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    const available = await isPortAvailable(port);
    if (available) {
      return;
    }
    await delay(250);
  }

  throw new Error(`Timed out waiting for port ${port} to close.`);
}

async function isPortAvailable(port: number): Promise<boolean> {
  return await new Promise<boolean>(resolve => {
    const server = net.createServer();
    server.once('error', () => resolve(false));
    server.listen(port, '127.0.0.1', () => {
      server.close(() => resolve(true));
    });
  });
}

async function attachFileIfExists(testInfo: TestInfo, name: string, filePath: string): Promise<void> {
  try {
    await fs.access(filePath);
    await testInfo.attach(name, { path: filePath, contentType: 'text/plain' });
  } catch {
  }
}

function delay(ms: number): Promise<void> {
  return new Promise(resolve => setTimeout(resolve, ms));
}
