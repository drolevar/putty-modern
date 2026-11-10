'use strict';
/*
 * A small SSH server for tests, on the ssh2 npm package. Not for any other use: it accepts a fixed
 * user and password, listens on loopback only, and its "shell" is a ten-line toy.
 *
 *   node server.js [--port N] [--user NAME] [--password PW] [--methods password,keyboard-interactive,publickey]
 *                  [--authorized-key "ssh-ed25519 AAAA..."] [--host-key-type ed25519|rsa|ecdsa]
 *                  [--host-key-file PATH] [--banner TEXT] [--motd TEXT]
 *
 * Once it is listening it prints one JSON line on stdout:
 *   {"port":2222,"hostKeyType":"ssh-ed25519","sha256":"SHA256:...","md5":"aa:bb:..."}
 * and it exits when its stdin closes, so the process that started it only has to close the pipe
 * (or kill it).
 *
 * ssh2 is installed outside the source tree (see tools/start-ssh-test-server.ps1, which sets NODE_PATH).
 */
const crypto = require('node:crypto');
const fs = require('node:fs');
const { Server, utils } = require('ssh2');

function parseArgs(argv) {
  const options = {
    port: 0,
    user: 'test',
    password: 'secret',
    methods: ['password', 'keyboard-interactive', 'publickey'],
    authorizedKeys: [],
    hostKeyType: 'ed25519',
    hostKeyFile: null,
    banner: null,
    motd: 'Welcome to the PuTTY modern test server.',
  };
  for (let i = 2; i < argv.length; i++) {
    const name = argv[i];
    const value = () => {
      if (i + 1 >= argv.length) throw new Error(`${name} needs a value`);
      return argv[++i];
    };
    switch (name) {
      case '--port': options.port = Number(value()); break;
      case '--user': options.user = value(); break;
      case '--password': options.password = value(); break;
      case '--methods': options.methods = value().split(',').filter(Boolean); break;
      case '--authorized-key': options.authorizedKeys.push(value()); break;
      case '--host-key-type': options.hostKeyType = value(); break;
      case '--host-key-file': options.hostKeyFile = value(); break;
      case '--banner': options.banner = value(); break;
      case '--motd': options.motd = value(); break;
      default: throw new Error(`unknown argument ${name}`);
    }
  }
  return options;
}

function makeHostKey(options) {
  if (options.hostKeyFile && fs.existsSync(options.hostKeyFile)) {
    return fs.readFileSync(options.hostKeyFile);
  }
  const generated = options.hostKeyType === 'rsa'
    ? utils.generateKeyPairSync('rsa', { bits: 2048 })
    : options.hostKeyType === 'ecdsa'
      ? utils.generateKeyPairSync('ecdsa', { bits: 256 })
      : utils.generateKeyPairSync('ed25519');
  if (options.hostKeyFile) {
    fs.writeFileSync(options.hostKeyFile, generated.private, { mode: 0o600 });
  }
  return generated.private;
}

function fingerprints(hostKey) {
  const parsed = utils.parseKey(hostKey);
  if (parsed instanceof Error) throw parsed;
  const blob = parsed.getPublicSSH();
  return {
    type: parsed.type,
    sha256: 'SHA256:' + crypto.createHash('sha256').update(blob).digest('base64').replace(/=+$/, ''),
    md5: crypto.createHash('md5').update(blob).digest('hex').replace(/(..)(?=.)/g, '$1:'),
  };
}

/** A line shell: echoes what is typed (as a terminal on the server would), runs a few built-ins. */
function runShell(stream, options) {
  let line = '';
  const prompt = () => stream.write('$ ');
  stream.write(options.motd + '\r\n');
  prompt();
  stream.on('data', (data) => {
    for (const byte of data) {
      if (byte === 0x0d || byte === 0x0a) {
        stream.write('\r\n');
        const command = line.trim();
        line = '';
        if (command === 'exit') {
          stream.exit(0);
          stream.end();
          return;
        }
        if (command.startsWith('echo ')) stream.write(command.slice(5) + '\r\n');
        else if (command === 'whoami') stream.write(options.user + '\r\n');
        else if (command) stream.write(`${command}: command not found\r\n`);
        prompt();
      } else if (byte === 0x7f || byte === 0x08) {
        if (line.length > 0) {
          line = line.slice(0, -1);
          stream.write('\b \b');
        }
      } else if (byte >= 0x20) {
        line += String.fromCharCode(byte);
        stream.write(Buffer.from([byte]));
      }
    }
  });
}

function main() {
  const options = parseArgs(process.argv);
  const hostKey = makeHostKey(options);
  const authorized = options.authorizedKeys.map((text) => {
    const key = utils.parseKey(text);
    if (key instanceof Error) throw key;
    return Array.isArray(key) ? key[0] : key;
  });

  const server = new Server({ hostKeys: [hostKey], banner: options.banner ?? undefined }, (client) => {
    client.on('error', () => { /* a client that drops the connection is not news */ });
    client.on('authentication', (ctx) => {
      if (ctx.username !== options.user) return ctx.reject(options.methods);
      if (ctx.method === 'password' && options.methods.includes('password')) {
        return ctx.password === options.password ? ctx.accept() : ctx.reject(options.methods);
      }
      if (ctx.method === 'keyboard-interactive' && options.methods.includes('keyboard-interactive')) {
        return ctx.prompt([{ prompt: 'Password: ', echo: false }], 'Test server', 'Type the password.', (answers) => {
          if (answers.length === 1 && answers[0] === options.password) ctx.accept();
          else ctx.reject(options.methods);
        });
      }
      if (ctx.method === 'publickey' && options.methods.includes('publickey')) {
        const match = authorized.find((key) =>
          ctx.key.algo === key.type && crypto.timingSafeEqual(ctx.key.data, key.getPublicSSH()));
        if (!match) return ctx.reject(options.methods);
        if (!ctx.signature) return ctx.accept();        // the "would this key do?" probe
        return match.verify(ctx.blob, ctx.signature, ctx.hashAlgo) === true ? ctx.accept() : ctx.reject(options.methods);
      }
      return ctx.reject(options.methods);               // 'none' and anything else: say what is on offer
    });
    client.on('ready', () => {
      client.on('session', (acceptSession) => {
        const session = acceptSession();
        session.on('pty', (accept) => accept && accept());
        session.on('window-change', (accept) => accept && accept());
        session.on('env', (accept) => accept && accept());
        session.on('shell', (accept) => runShell(accept(), options));
        session.on('exec', (accept, reject, info) => {
          const stream = accept();
          const match = /^echo (.*)$/.exec(info.command);
          if (match) {
            stream.write(match[1] + '\n');
            stream.exit(0);
          } else {
            stream.stderr.write(`${info.command}: command not found\n`);
            stream.exit(127);
          }
          stream.end();
        });
      });
    });
  });

  server.listen(options.port, '127.0.0.1', () => {
    const fp = fingerprints(hostKey);
    process.stdout.write(JSON.stringify({
      port: server.address().port,
      hostKeyType: fp.type,
      sha256: fp.sha256,
      md5: fp.md5,
    }) + '\n');
  });

  // The parent controls the lifetime: closing our stdin, or killing us, ends the server.
  process.stdin.resume();
  process.stdin.on('end', () => process.exit(0));
  process.stdin.on('close', () => process.exit(0));
}

try {
  main();
} catch (error) {
  process.stderr.write(`${error.message}\n`);
  process.exit(2);
}
