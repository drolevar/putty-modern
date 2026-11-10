<#
.SYNOPSIS
  Installs the test SSH server's one dependency (ssh2 from npm, without running install scripts)
  into artifacts\ssh-test-server, outside the source tree, and optionally runs the server.

.DESCRIPTION
  The server is tools\ssh-test-server\server.js. Tests start it themselves (see SshServerFixture in
  the interop tests); this script is for installing it once and for trying it by hand:

    tools\start-ssh-test-server.ps1 -Install          # once
    tools\start-ssh-test-server.ps1 -Port 2222        # then: ssh -p 2222 test@127.0.0.1  (password: secret)

  Any further arguments go to server.js, for example -- --methods keyboard-interactive.
#>
param(
    [switch] $Install,
    [int] $Port = 0,
    [Parameter(ValueFromRemainingArguments)] [string[]] $ServerArguments
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$modules = Join-Path $root 'artifacts\ssh-test-server'
$server = Join-Path $root 'tools\ssh-test-server\server.js'

if ($Install -or -not (Test-Path (Join-Path $modules 'node_modules\ssh2'))) {
    New-Item -ItemType Directory -Force -Path $modules | Out-Null
    & npm install --prefix $modules --ignore-scripts --no-audit --no-fund ssh2@1.17.0
    if ($LASTEXITCODE -ne 0) { throw 'npm install failed' }
    if ($Install) { return }
}

$env:NODE_PATH = Join-Path $modules 'node_modules'
& node $server --port $Port @ServerArguments
