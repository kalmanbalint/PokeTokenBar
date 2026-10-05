# PokeTokenBar — VS Code port

Your AI coding tokens, hatched into Pokémon — in the VS Code status bar and sidebar.

A cross-platform (Windows / macOS / Linux) reimplementation of
[chattymin/PokeTokenBar](https://github.com/chattymin/PokeTokenBar), which is a macOS menu bar
app written in Swift. This branch is the port: a **C# core and sidecar** in `dotnet/` doing the
reading and arithmetic, behind a thin **TypeScript extension** in `extension/` that renders it.
The original Swift sources are not in this fork; they remain [upstream](https://github.com/chattymin/PokeTokenBar).

## Prerequisites

| Tool | Version | Notes |
|---|---|---|
| .NET SDK | **10.0+** | `dotnet --version`. Builds the core and the sidecar. |
| Node.js | **20+** | `node --version`. Compiles the extension. |
| VS Code | 1.90+ | Runs it. |

Nothing else. There is one NuGet dependency (`StreamJsonRpc`) and no build-time downloads.

## Install it as a real extension

One command builds and installs it:

```bash
cd extension
npm install
npm run install:local
```

Then **reload VS Code** (`Developer: Reload Window`) so open windows pick it up.

After that it runs on its own. Activation is `onStartupFinished`, so VS Code starts the sidecar
itself in every window -- no F5, no development host, and it survives restarts. Click the
`$(graph)` item in the status bar to open the companion.

If the `code` CLI is not on your `PATH`, either add it from the Command Palette
(*Shell Command: Install 'code' command in PATH*) or install the built file by hand:

```bash
npm run package
code --install-extension poketokenbar-win32-x64-0.1.0.vsix --force
# or: Extensions view -> ... menu -> "Install from VSIX..."
```

To remove it:

```bash
code --uninstall-extension kalmanbalint.poketokenbar
```

### About the size

The VSIX is around 33 MB and needs **no .NET runtime on the target machine**, which is what makes
it standalone. Three things about that number.

It is **platform-specific**: it carries one platform's binary, so `win32-x64`, `darwin-arm64` and
`linux-x64` are separate builds produced on matching machines. NativeAOT would cut the binary
from 75 MB to a few and is the intended release path, but it needs a full C++ toolchain (see
[Releasing](#releasing)). And trimming is deliberately off: StreamJsonRpc's
`Microsoft.VisualStudio.Threading` dependency emits trim warnings, and trimming an assembly that
warns can strip code it needs at run time.

## Run it from source

For development, run it from source instead of installing. It will not appear in the Extensions
list this way.

**1. Build the sidecar and stage it into the extension.**

```bash
cd extension
npm install
npm run build:all
```

`build:all` runs `dotnet build -c Release ../dotnet/PokeTokenBar.slnx`, copies the built sidecar
into `extension/server/`, then compiles the TypeScript. The staging step is required rather than
convenient: the extension resolves its executable from its own install directory and never from
a setting, so the binary has to be *copied in* rather than pointed at.

**2. Open this folder in VS Code and press F5.**

Open either the repository root or `extension/` — both carry a `.vscode/launch.json` with an
`extensionHost` configuration. F5 without one does nothing useful, and the repository root looks
like a Swift package to VS Code, which is why the configuration exists rather than being
optional.

F5 opens a **second window**, titled `[Extension Development Host]`. Everything appears in
*that* window, not the one you pressed F5 in.

**3. Click the `$(graph)` item in the status bar.**

Bottom right, near the Copilot icon — a small bar-chart glyph with today's token count, such as
`$(graph) 732.7M`. Hovering shows today, this week and this month with estimated cost and a
per-model breakdown. **Clicking opens the companion**, which is also reachable from the pokéball
icon in the activity bar on the left.

The companion view is lazy: VS Code does not render it until it first becomes visible, so
nothing appears there until you open it once.

After changing `package.json`, stop with **Shift+F5** and press F5 again — manifest changes are
not hot-reloaded.

### From a terminal instead

```bash
cd extension
npm run build:sidecar   # dotnet build -c Release ../dotnet/PokeTokenBar.slnx
npm run dev             # stage the sidecar into server/, then compile
npm run test:all        # 151 C# tests + 44 extension tests
```

The sidecar is a stdio JSON-RPC server, so running it by hand just waits for a client.
`extension/test/protocol.test.mjs` drives the real binary if you want to watch it answer.

## What you get

Today's token total in the status bar, and a sidebar companion that evolves as you spend tokens:
a line is drawn, fed by your usage, and advances through its forms toward graduation. A common
line graduates at 750M tokens and a legendary one at 6B — calibrated against a measured average
of roughly 253M tokens a day. A line you have already graduated is not drawn again; a branching
one such as Eevee stays in the pool until every branch has been raised.

Cost is an estimate of what those tokens would bill at published API rates, matching `ccusage`.
It is **not** what a subscription charges.

## When something does not appear

The extension's own log answers this faster than guessing. Open **Output → PokeTokenBar** in the
development host window, or read it from disk:

```
%APPDATA%/Code/logs/<session>/window<N>/exthost/undefined_publisher.poketokenbar/PokeTokenBar.log
```

| Log line | Meaning |
|---|---|
| `status bar shows "..."` | The status bar rendered. Check which window you are looking at. |
| `companion view not open yet` | Expected until you open it. Click the status bar item. |
| `companion view opened; rendering` | The view is live; a blank panel is a rendering fault. |
| `helper ... ready` and nothing after | The scan is still running, or the refresh threw. |
| `helper executable missing` | The sidecar was not staged. Run `npm run build:all`. |
| No log file at all | It never activated. Check `exthost.log` for `_doActivateExtension`. |

Status bar states, all deliberate rather than errors:

| Item | Meaning |
|---|---|
| `$(shield) Usage: trust required` | Untrusted workspace — the helper is not started at all. |
| `$(circle-slash) Usage` | No Claude Code transcripts on this machine. |
| `$(warning)` suffix | Numbers are incomplete; run **PokeTokenBar: Show scan diagnostics**. |
| `$(error) Usage` | The helper could not start, or exited five times. |

## Releasing

`dotnet publish` needs a full C++ toolchain, because the sidecar publishes with NativeAOT, and it
fails in two ways worth knowing about: `vswhere.exe` must be on `PATH` (it ships under
`C:\Program Files (x86)\Microsoft Visual Studio\Installer\` but is not added there), and the
Windows SDK must be installed rather than just the MSVC compiler, or the link step fails with
`LNK1181: cannot open input file 'advapi32.lib'`.

Day-to-day development never invokes it — only `publish` does. NativeAOT cannot cross-compile,
so per-platform binaries are built on matching CI runners and bundled into platform-specific
VSIX builds.

## Layout

| Path | What |
|---|---|
| `dotnet/src/PokeTokenBar.Core` | Parsing, aggregation, pricing, companion. No UI, no host. |
| `dotnet/src/PokeTokenBar.Sidecar` | stdio JSON-RPC host. Owns credentials and all network access. |
| `extension/` | VS Code extension. Renders; holds no credentials. |

Architecture, the security invariants both sides uphold, and the dependency policy are in
[`dotnet/README.md`](dotnet/README.md). Extension-specific notes are in
[`extension/README.md`](extension/README.md).

## Upstream

This is a fork. The original macOS app, its screenshots and its release notes are at
[chattymin/PokeTokenBar](https://github.com/chattymin/PokeTokenBar); that project's README is in
this repository's git history before this commit, and its Korean and Japanese translations are
still alongside as `README.ko.md` and `README.ja.md`.

## Licence

MIT, as upstream. See [LICENSE](LICENSE).
