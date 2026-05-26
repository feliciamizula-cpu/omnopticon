/* eslint-disable */
// CLI lazy-loader - downloads and installs CLI tools on first use.

const { useState, useEffect } = React;

// Installed state tracking
const _installed = {
  claude: false,
  gemini: false,
  opencode: false,
  openai: false,
  codex: false,
};

const _installing = {
  claude: false,
  gemini: false,
  opencode: false,
  openai: false,
  codex: false,
};

// Lazy install function - downloads and installs a CLI on first use
async function lazyInstall(cli) {
  if (_installed[cli] || _installing[cli]) return true;
  _installing[cli] = true;

  try {
    switch (cli) {
      case "claude":
        await _installClaude();
        break;
      case "gemini":
        await _installGemini();
        break;
      case "opencode":
        await _installOpencode();
        break;
      case "openai":
        await _installOpenAI();
        break;
      case "codex":
        await _installCodex();
        break;
    }
    _installed[cli] = true;
    console.log(`[cli-loader] ${cli} installed successfully`);
    return true;
  } catch (err) {
    console.error(`[cli-loader] Failed to install ${cli}:`, err);
    return false;
  } finally {
    _installing[cli] = false;
  }
}

async function _runCommand(cmd, args = []) {
  return new Promise((resolve, reject) => {
    const { exec } = require('child_process');
    const fullCmd = `${cmd} ${args.join(' ')}`;
    exec(fullCmd, { timeout: 120000 }, (err, stdout, stderr) => {
      if (err) reject(err);
      else resolve({ stdout, stderr });
    });
  });
}

async function _installClaude() {
  const { exec } = require('child_process');
  return new Promise((resolve, reject) => {
    exec('curl -fsSL https://downloads.anthropic.com/claude/Claude-latest-linux-x86_64 -o /usr/local/bin/claude && chmod +x /usr/local/bin/claude', { timeout: 60000 }, (err) => {
      if (err) reject(err);
      else resolve();
    });
  });
}

async function _installGemini() {
  const { exec } = require('child_process');
  return new Promise((resolve, reject) => {
    exec('npm install -g @google/gemini-cli 2>/dev/null && ln -sf /usr/local/lib/node_modules/@google/gemini-cli/bundle/gemini.js /usr/local/bin/gemini', { timeout: 120000 }, (err) => {
      if (err) reject(err);
      else resolve();
    });
  });
}

async function _installOpencode() {
  const { exec } = require('child_process');
  return new Promise((resolve, reject) => {
    exec('npm install -g opencode-ai 2>/dev/null && ln -sf /usr/local/lib/node_modules/opencode-ai/bin/opencode.exe /usr/local/bin/opencode', { timeout: 120000 }, (err) => {
      if (err) reject(err);
      else resolve();
    });
  });
}

async function _installOpenAI() {
  const { exec } = require('child_process');
  return new Promise((resolve, reject) => {
    exec('npm install -g openai 2>/dev/null && ln -sf /usr/local/lib/node_modules/openai/bin/run.js /usr/local/bin/openai', { timeout: 120000 }, (err) => {
      if (err) reject(err);
      else resolve();
    });
  });
}

async function _installCodex() {
  const { exec } = require('child_process');
  return new Promise((resolve, reject) => {
    exec('npm install -g @openai/codex 2>/dev/null && ln -sf /usr/local/lib/node_modules/@openai/codex/bin/run.js /usr/local/bin/codex', { timeout: 120000 }, (err) => {
      if (err) reject(err);
      else resolve();
    });
  });
}

// Check if a CLI is available, install if not
async function ensureCli(cli) {
  const { exec } = require('child_process');
  return new Promise((resolve) => {
    exec(`which ${cli} || echo "not found"`, (err, stdout) => {
      if (stdout.includes('/')) {
        resolve(true);
      } else {
        lazyInstall(cli).then(resolve);
      }
    });
  });
}

// CLI command runner - ensures CLI is installed before running
async function runCli(cli, args = [], options = {}) {
  await ensureCli(cli);
  
  return new Promise((resolve, reject) => {
    const { spawn } = require('child_process');
    const cmd = cli === 'claude' ? '/usr/local/bin/claude' : cli;
    const proc = spawn(cmd, args, { 
      timeout: options.timeout || 300000,
      env: { ...process.env, ...options.env }
    });
    
    let stdout = '';
    let stderr = '';
    
    proc.stdout.on('data', (data) => { stdout += data; });
    proc.stderr.on('data', (data) => { stderr += data; });
    
    proc.on('close', (code) => {
      if (code === 0 || options.ignoreExitCode) {
        resolve({ stdout, stderr, code });
      } else {
        reject(new Error(`${cli} exited with code ${code}: ${stderr}`));
      }
    });
    
    proc.on('error', reject);
  });
}

// React hook for CLI availability status
function useCliStatus(cli) {
  const [status, setStatus] = useState('unknown'); // 'unknown' | 'checking' | 'available' | 'installing' | 'error'

  useEffect(() => {
    const checkCli = async () => {
      setStatus('checking');
      const { exec } = require('child_process');
      exec(`which ${cli} 2>/dev/null || echo "not found"`, (err, stdout) => {
        if (stdout.includes('/')) {
          setStatus('available');
        } else if (_installing[cli]) {
          setStatus('installing');
        } else {
          setStatus('installing');
          lazyInstall(cli).then(() => setStatus('available')).catch(() => setStatus('error'));
        }
      });
    };
    checkCli();
  }, [cli]);

  return status;
}

// Auto-installer - triggers installation in background when page loads
function triggerBackgroundInstall() {
  const clis = ['claude', 'gemini', 'opencode', 'openai', 'codex'];
  clis.forEach(cli => {
    setTimeout(() => lazyInstall(cli), Math.random() * 10000); // Stagger 0-10s
  });
}

Object.assign(window, {
  lazyInstall,
  ensureCli,
  runCli,
  useCliStatus,
  triggerBackgroundInstall,
  cliStatus: _installed,
});