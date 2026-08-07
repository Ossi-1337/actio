![Actio workflow runner](docs/assets/actio-banner.png)

[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)

Actio is a local-first workflow runner for YAML pipelines. It executes Docker-backed workflows from `.workflows/`, stores runs, logs, caches, and artifacts locally, and provides a lightweight loopback web UI.

## Start

Requirements: the .NET 10 SDK from `global.json` and Docker in Linux-container mode.

```bash
dotnet restore Actio.slnx
dotnet run --project src/Actio.Cli -- validate ci.yml
dotnet run --project src/Actio.Cli -- ci.yml
```

Use `actio --help` after installing the CLI. `actio compatibility` shows the current action support matrix.

## Git Pre-Push

Install optional repository-local automation from the Git repository root:

```bash
actio hooks install
actio hooks status
```

The managed hook runs workflows whose `on.push` branch, tag, and path filters match the refs being pushed. It requires a clean current `HEAD`, blocks the push when validation or execution fails, and never starts the web UI. `git push --no-verify` bypasses local hooks.

## Platform Status

Linux and Windows are verification targets. macOS is best effort and currently unverified. Actio is under active development and does not yet promise a stable workflow compatibility surface.

Docker containers reduce risk but are not VM security boundaries. The web UI is loopback-only and does not protect against other processes running as the same local user. Actio does not create an automatic `GITHUB_TOKEN`; external actions and mutable references must be reviewed before use.

See [Architecture](docs/architecture.md) and [Security](SECURITY.md).

Licensed under the [MIT License](LICENSE).
