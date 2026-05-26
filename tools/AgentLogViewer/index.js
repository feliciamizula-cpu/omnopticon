import * as fs from 'fs';
import * as path from 'path';
import { fileURLToPath } from 'url';

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);

const projectRoot = path.resolve(__dirname, '..', '..', '..');
const logFilePath = path.join(projectRoot, 'ai_logs.txt');

let lastPosition = 0;

function clearScreen() {
  process.stdout.write('\x1b[2J\x1b[H');
}

function parseLogEntry(line) {
  const timestampMatch = line.match(/\[([^\]]+)\]/);
  const agentIdMatch = line.match(/AGENT_ID:\s*(\S+)/);
  const taskMatch = line.match(/TASK:\s*(\S+)/);
  const statusMatch = line.match(/STATUS:\s*(\S+)/);
  const messageMatch = line.match(/MESSAGE:\s*(.+)/);

  return {
    timestamp: timestampMatch ? timestampMatch[1] : '',
    agentId: agentIdMatch ? agentIdMatch[1] : 'UNKNOWN',
    task: taskMatch ? taskMatch[1] : 'N/A',
    status: statusMatch ? statusMatch[1] : 'INFO',
    message: messageMatch ? messageMatch[1] : line
  };
}

function getStatusColor(status) {
  const upperStatus = status.toUpperCase();
  switch (upperStatus) {
    case 'STARTED': return '\x1b[92m';
    case 'PROGRESS': return '\x1b[94m';
    case 'COMPLETED': return '\x1b[92m';
    case 'LOCKING': return '\x1b[93m';
    case 'RELEASED': return '\x1b[96m';
    case 'BLOCKED': return '\x1b[91m';
    case 'CONFLICT': return '\x1b[91m';
    case 'ERROR': return '\x1b[91m';
    case 'WAITING': return '\x1b[90m';
    default: return '\x1b[97m';
  }
}

function displayLogEntry(entry) {
  const color = getStatusColor(entry.status);
  const reset = '\x1b[0m';

  console.log(`${entry.timestamp} ${color}[${entry.status}]${reset} ${entry.agentId}`);
  console.log(`  Task: ${entry.task}`);
  console.log(`  ${entry.message}`);
  console.log();
}

function watchLogFile() {
  try {
    if (!fs.existsSync(logFilePath)) {
      console.log('[WAITING] Log file not yet created. Waiting for agent activity...\n');
      setTimeout(watchLogFile, 2000);
      return;
    }

    const stats = fs.statSync(logFilePath);
    
    if (stats.size < lastPosition) {
      lastPosition = 0;
      console.clear();
      console.log('[RESET] Log file was truncated, reading from start...\n');
    }

    if (stats.size > lastPosition) {
      const stream = fs.createReadStream(logFilePath, {
        start: lastPosition,
        encoding: 'utf8'
      });

      let buffer = '';

      stream.on('data', (chunk) => {
        buffer += chunk;
        const lines = buffer.split('\n');
        buffer = lines.pop() || '';
        
        for (const line of lines) {
          if (line.trim()) {
            displayLogEntry(parseLogEntry(line));
          }
        }
      });

      stream.on('end', () => {
        lastPosition = stats.size;
        setTimeout(watchLogFile, 2000);
      });

      stream.on('error', (err) => {
        console.error('[ERROR]', err.message);
        setTimeout(watchLogFile, 2000);
      });
    } else {
      setTimeout(watchLogFile, 2000);
    }
  } catch (err) {
    console.error('[ERROR]', err.message);
    setTimeout(watchLogFile, 2000);
  }
}

function init() {
  clearScreen();
  console.log('╔══════════════════════════════════════════════════════════════════════════════╗');
  console.log('║                    AGENT LOG MONITOR - Omnopticon                          ║');
  console.log('╚══════════════════════════════════════════════════════════════════════════════╝');
  console.log();
  console.log('Watching:', logFilePath);
  console.log('Press Ctrl+C to exit...');
  console.log();
  console.log('═'.repeat(90));
  console.log();
  
  watchLogFile();
}

process.on('SIGINT', () => {
  console.log('\n\nShutting down log monitor...');
  process.exit(0);
});

init();