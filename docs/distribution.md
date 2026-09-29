# Distribution tiers

PeakRelay ships **two tiers from one pipeline** (a single `Directory.Build.props` version,
CI on every push, tagged releases on GitHub). Pick the tier that matches who is deploying:

## Consumer tier — the installers

**Who:** players and server owners on Windows who want one-click setup.

**Artifacts** (attached to each GitHub Release):
- `PeakRelayInstaller-Client.exe` — game-dir picker, deploys BepInEx + Doorstop 4.5.0
  loader + client plugin + config
- `PeakRelayInstaller-Server.exe` — same + dedicated plugin + `server.json`, optional
  relay staging

Both are self-contained single-file exes (no .NET needed on the target), GUI wizards with
the identical CLI verbs for scripting (`--dir --room --host --port --yes`; exit codes
0/1/2). Usage: `docs/installers.md`.

## Operator tier — the raw files

**Who:** enterprises / homelab operators running the relay in Docker, Kubernetes, or a
plain service manager; automated deployments; CI consumers.

**Artifacts:**
- `PeakRelay.Server.zip` — framework-dependent publish. Run anywhere .NET 8 runs:
  ```sh
  unzip PeakRelay.Server.zip -d relay
  dotnet relay/PeakRelay.Server.dll 5055 5056     # TCP 5055 game, HTTP 5056 directory
  ```
- `PeakRelay-plugins.zip` — `PeakRelay.Dedicated/` and `PeakRelay.Client/` plugin DLLs;
  drop into `<PEAK>/BepInEx/plugins/` of a prepped game install (BepInEx 5.4.23.2 +
  **Doorstop 4.5.0** — the stock BepInEx loader crashes the 2026-09 PEAK update; see
  `docs/client-guide.md` §6).

### Container example

The relay is a plain console app — no game files, no Windows dependency:

```dockerfile
FROM mcr.microsoft.com/dotnet/runtime:8.0
WORKDIR /app
COPY relay/ .
EXPOSE 5055 5056
ENTRYPOINT ["dotnet", "PeakRelay.Server.dll", "5055", "5056"]
```

`docker run -p 5055:5055 -p 5056:5056 peakrelay` is a working relay. Kubernetes: expose
both ports behind a Service; the directory endpoint doubles as a health probe
(`GET /rooms` → 200).

### The PEAK host box is the exception

The dedicated host is inherently a Windows game process (`PEAK.exe -batchmode
-nographics`), so "server in a container" means: relay in the container, host on a Windows
box pointing at it via `server.json` (`relayHost`/`relayPort`). Clients likewise run on
player PCs.

## Versioning & updates

One version in `Directory.Build.props` flows into every assembly; plugins read their
BepInEx version from the assembly. Tags `v*` trigger the release workflow — those Releases
are the update channel the planned launcher will poll for self- and component-updates.
