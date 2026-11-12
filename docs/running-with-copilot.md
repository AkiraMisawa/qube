# Running the AI pane on Copilot

The AI pane talks to an agent runtime chosen by `Agent:Runtime` (ADR-0007 §5). `scripted` needs nothing and has no
model. `copilot` asks the **agent host**, a process of its own that holds the model credential and nothing that can act
in the workbench (C44). This page says how to run it on your own machine (C46).

## What you need

- A Copilot subscription, and a **fine-grained personal access token** (`github_pat_…`) of that account. The Copilot
  SDK accepts `github_pat_`, `gho_` and `ghu_` tokens; a classic `ghp_` token is refused. Create it at
  github.com → Settings → Developer settings → Fine-grained tokens. It needs no repository access.
- Keep the token out of the repository, the chat and the logs. Put it in your shell for the session, or in a file
  only the host's user can read (below).

## One command

```sh
export COPILOT_GITHUB_TOKEN=github_pat_...      # this shell only
tools/run-with-copilot.sh --model <model id> [--model <another id> ...]
```

Each `--model` is one the AI may use (`Agent:Copilot:Models`, ADR-0017 §6.3). A new conversation starts on your
default model, a setting (`settings default model <id>`, or the settings dialog), which is the first listed until you
set it. The AI pane's Model choice changes the model of its conversation alone.

The script does the following:

1. Makes a fresh key for the two processes to share.
2. Starts the agent host on `127.0.0.1:5395` and waits until it answers.
3. Starts the workbench on `http://localhost:5080` with `Agent:Runtime=copilot`. The workbench gets the key but not the token: `COPILOT_GITHUB_TOKEN`, `GH_TOKEN` and `GITHUB_TOKEN` are removed from its environment.

The key and the token travel in the environment, never on a command line, where any user could read them with `ps`.
Ctrl-C stops both. Build the client first (`npm run build` in `client/`) if you want the page and not only the API.

The model ids your account may use are listed by the host:

```sh
curl -s -H "X-Agent-Host-Key: $AgentHost__Key" http://127.0.0.1:5395/models
```

`$AgentHost__Key` is set in the script's shell only. To list models by hand, start the host yourself (below) with a key you chose.

## By hand, in two terminals

```sh
# Terminal 1: the agent host, the only process with the token
export COPILOT_GITHUB_TOKEN=github_pat_...
export AgentHost__Key=$(openssl rand -hex 32)    # give the same value to terminal 2
nix develop -c dotnet run --project server/src/Qube.AgentHost

# Terminal 2: the workbench, without the token
export Agent__Runtime=copilot
export Agent__Copilot__HostKey=<the same key>
export Agent__Copilot__Models__0=<model id>      # :1, :2 … for more; the first is a new conversation's default
nix develop -c dotnet run --project server/src/Qube.Web --urls http://localhost:5080
```

If the host is not running, a request fails with "The agent host … could not be reached". It never falls back to the scripted agent. If `Agent:Copilot:HostKey` or `Agent:Copilot:Models` is missing, the workbench does not start. `Agent:Copilot:Model`, the one model of before, is not read: the workbench stops and says to list it under `Agent:Copilot:Models`.

## Running the agent as another OS user

The host can run as a user of its own. The OS then keeps the workbench's database and your credentials (`~/.ssh`, `gh`'s login, the repository) out of its reach. This is the company's separate service account, at home.

1. Create the user. On macOS, use System Settings → Users & Groups, or `sudo sysadminctl -addUser _qubeagent`. On Linux, use `sudo useradd --system --create-home qubeagent`.
2. Publish the host where that user can read it:
   `tools/run-with-copilot.sh --install-host /usr/local/lib/qube-agent-host`
3. Put the token in a file only that user can read, for example `/Users/_qubeagent/.copilot-token`. Set its mode with `sudo chmod 600` and its owner with `sudo chown _qubeagent`.
4. Start both:
   `tools/run-with-copilot.sh --model <id> [--model <id> ...] --as-user _qubeagent --host-dir /usr/local/lib/qube-agent-host --token-file /Users/_qubeagent/.copilot-token`

The SDK keeps its state under that user's home (`~/.local/share/qube/agent-host` or its macOS equivalent).
A loopback port is open to every user of the machine, which is why the shared key is required in this form too.
To start the host at login, a launchd plist with `UserName` (macOS) or a systemd unit with `User=` (Linux) runs the
same command.

## Opening it from a phone, a tablet or another Mac

The workbench listens on `localhost`. With Tailscale on the machine that runs it:

```sh
tailscale serve --bg 5080
```

Then open `https://<machine>.<tailnet>.ts.net` from any device on your tailnet. It is HTTPS, so the browser's secure-context features (the clipboard) work there too. Two things stay off:

- Never use `tailscale funnel`, which puts the workbench on the internet.
- Never serve the agent host's port.

The agent host always runs on the same machine as the workbench.

## What is checked, and what is not

- Checked in `server/tests/Qube.Composition.Tests/CopilotRuntimeTests.cs`: the wire, the host and the workbench's side, with a fake model behind the host.
- Checked in the development container: the SDK's runtime starts, and a run made with a made-up token ends with the SDK's own error, shown on the card.
- Not checked: a real model answering. That needs your token, on your machine.
