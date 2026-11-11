'use strict';
/*
 * A small SSH server for tests, on the ssh2 npm package. Not for any other use: it accepts a fixed
 * user and password, listens on loopback only, and its "shell" is a ten-line toy.
 *
 *   node server.js [--port N] [--user NAME] [--password PW] [--methods password,keyboard-interactive,publickey]
 *                  [--authorized-key "ssh-ed25519 AAAA..."] [--host-key-type ed25519|rsa|ecdsa]
 *                  [--host-key-file PATH] [--banner TEXT] [--motd TEXT] [--chatter] [--no-forwarding]
 *
 * Port forwarding is on: a "direct-tcpip" channel request (a local or dynamic forward of the client) connects to the
 * address it names, from this machine, and relays; a "tcpip-forward" request (a remote forward) makes the server
 * listen on 127.0.0.1 at the requested port and open a "forwarded-tcpip" channel to the client for each connection.
 * --no-forwarding refuses both, as a server that does not allow TCP forwarding does.
 *
 * --chatter: 150 ms after the server asks the keyboard-interactive question it sends the client one more packet
 * (an OpenSSH keep-alive request, which PuTTY does not expect before login). It wakes PuTTY's login code while the
 * question is still open, as a real OpenSSH server does; the toy server otherwise never sends anything meanwhile.
 * The login itself may then fail on that unexpected packet: only the question's survival is of interest.
 *
 * Once it is listening it prints one JSON line on stdout:
 *   {"port":2222,"hostKeyType":"ssh-ed25519","sha256":"SHA256:...","md5":"aa:bb:..."}
 * and it exits when its stdin closes, so the process that started it only has to close the pipe
 * (or kill it). After that line it reports what happens, one JSON object per line, so a test can
 * check that a login or a typed command really reached the server:
 *   {"event":"auth","method":"password","user":"test","ok":true}
 *   {"event":"shell"}    {"event":"command","text":"echo hi"}    {"event":"exec","text":"echo hi"}
 *   {"event":"tcpip","dest":"127.0.0.1:8080","ok":true}    {"event":"tcpip-forward","port":5000}    {"event":"tcpip-forward-cancel","port":5000}
 *
 * ssh2 is installed outside the source tree (see tools/start-ssh-test-server.ps1, which sets NODE_PATH).
 */
const crypto = require('node:crypto');
const fs = require('node:fs');
const net = require('node:net');
const { Server, utils } = require('ssh2');

function emit(event) {
  process.stdout.write(JSON.stringify(event) + '\n');
}

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
    chatter: false,
    noForwarding: false,
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
      case '--chatter': options.chatter = true; break;
      case '--no-forwarding': options.noForwarding = true; break;
      default: throw new Error(`unknown argument ${name}`);
    }
  }
  return options;
}

function makeHostKey(options) {
  if (options.hostKeyFile && fs.existsSync(options.hostKeyFile)) {
    return fs.readFileSync(options.hostKeyFile);
  }
  // ssh2 now and then generates a key that its own parser then calls malformed ("Malformed OpenSSH
  // private key", about one in a few hundred), so a key counts only once it has been parsed back.
  let generated;
  for (let attempt = 0; attempt < 20; attempt++) {
    generated = options.hostKeyType === 'rsa'
      ? utils.generateKeyPairSync('rsa', { bits: 2048 })
      : options.hostKeyType === 'ecdsa'
        ? utils.generateKeyPairSync('ecdsa', { bits: 256 })
        : utils.generateKeyPairSync('ed25519');
    if (!(utils.parseKey(generated.private) instanceof Error)) break;
    generated = null;
  }
  if (!generated) throw new Error('could not generate a host key that ssh2 accepts');
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
        emit({ event: 'command', text: command });
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
    client.on('authentication', (rawCtx) => {
      // Every outcome is reported; ctx wraps the real context so accept and reject can say so.
      const ctx = {
        username: rawCtx.username,
        method: rawCtx.method,
        password: rawCtx.password,
        key: rawCtx.key,
        signature: rawCtx.signature,
        blob: rawCtx.blob,
        hashAlgo: rawCtx.hashAlgo,
        prompt: (...args) => rawCtx.prompt(...args),
        accept: () => { emit({ event: 'auth', method: rawCtx.method, user: rawCtx.username, ok: true }); rawCtx.accept(); },
        reject: (methods) => { emit({ event: 'auth', method: rawCtx.method, user: rawCtx.username, ok: false }); rawCtx.reject(methods); },
      };
      if (ctx.username !== options.user) return ctx.reject(options.methods);
      if (ctx.method === 'password' && options.methods.includes('password')) {
        return ctx.password === options.password ? ctx.accept() : ctx.reject(options.methods);
      }
      if (ctx.method === 'keyboard-interactive' && options.methods.includes('keyboard-interactive')) {
        if (options.chatter) {
          // _protocol is ssh2's own, not a public API; this is a test double, so a miss is only reported.
          setTimeout(() => { try { client._protocol.ping(); } catch (e) { emit({ event: 'chatter-failed', message: String(e) }); } }, 150);
        }
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
      // Local and dynamic forwards of the client: it asks for a channel to somewhere, we connect there and relay.
      client.on('tcpip', (accept, reject, info) => {
        const dest = `${info.destIP}:${info.destPort}`;
        if (options.noForwarding) { emit({ event: 'tcpip', dest, ok: false }); return reject(); }
        const socket = net.connect(info.destPort, info.destIP);
        let accepted = false;
        socket.on('connect', () => {
          accepted = true;
          emit({ event: 'tcpip', dest, ok: true });
          const channel = accept();
          channel.pipe(socket);
          socket.pipe(channel);
          channel.on('error', () => socket.destroy());
        });
        socket.on('error', () => { if (!accepted) { emit({ event: 'tcpip', dest, ok: false }); reject(); } else socket.destroy(); });
      });
      // Remote forwards: listen here, open a channel to the client for each connection.
      const remoteListeners = new Map();
      client.on('request', (accept, reject, name, info) => {
        if (name === 'tcpip-forward') {
          if (options.noForwarding) return reject();
          const listener = net.createServer((socket) => {
            client.forwardOut(info.bindAddr, listener.address().port, socket.remoteAddress ?? '127.0.0.1', socket.remotePort ?? 0, (error, channel) => {
              if (error) return socket.destroy();
              socket.pipe(channel);
              channel.pipe(socket);
              channel.on('error', () => socket.destroy());
              socket.on('error', () => channel.destroy());
            });
          });
          listener.on('error', () => reject());
          listener.listen(info.bindPort, '127.0.0.1', () => {
            const port = listener.address().port;
            remoteListeners.set(port, listener);
            emit({ event: 'tcpip-forward', port });
            accept(info.bindPort === 0 ? port : undefined);
          });
        } else if (name === 'cancel-tcpip-forward') {
          const listener = remoteListeners.get(info.bindPort);
          if (listener) { listener.close(); remoteListeners.delete(info.bindPort); emit({ event: 'tcpip-forward-cancel', port: info.bindPort }); }
          accept();
        } else {
          reject();
        }
      });
      client.on('close', () => { for (const listener of remoteListeners.values()) listener.close(); });

      client.on('session', (acceptSession) => {
        const session = acceptSession();
        session.on('pty', (accept) => accept && accept());
        session.on('window-change', (accept) => accept && accept());
        session.on('env', (accept) => accept && accept());
        session.on('shell', (accept) => { emit({ event: 'shell' }); runShell(accept(), options); });
        session.on('exec', (accept, reject, info) => {
          const stream = accept();
          emit({ event: 'exec', text: info.command });
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
