# Actio

[![.NET 10](https://img.shields.io/badge/.NET-10-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Docker runner](https://img.shields.io/badge/runner-Docker-2496ED?logo=docker&logoColor=white)](https://www.docker.com/)
[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)

Actio is a local-first workflow runner for YAML pipelines, inspired by GitHub Actions and designed for fast feedback on your own machine.

It runs workflows from `.workflows/`, executes Docker-backed jobs and supported actions, stores local run history, logs, caches, and artifacts, and exposes a lightweight localhost web UI.

## Requirements

- .NET 10 SDK
- Docker

## Quick Start

Create a workflow at `.workflows/ci.yml`:

```yaml
name: CI

jobs:
  smoke:
    runs-on: alpine-latest
    steps:
      - name: Smoke test
        run: echo "Actio workflow is running"
```

Run it locally:

```bash
dotnet run --project src/Actio.Cli -- run ci.yml
```

Common commands:

```bash
dotnet run --project src/Actio.Cli -- --help
dotnet run --project src/Actio.Cli -- compatibility
dotnet run --project src/Actio.Cli -- cache list
dotnet run --project src/Actio.Cli -- web
```

## Status

Actio is in early development. It intentionally does not create GitHub's automatic `GITHUB_TOKEN`; workflows that need tokens should use explicit local secrets.

Run `dotnet run --project src/Actio.Cli -- compatibility` to see the current action compatibility matrix.

## License

Actio is licensed under the [MIT License](LICENSE).
