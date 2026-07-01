# Actio

Actio is a local-first workflow runner for YAML pipelines, inspired by GitHub Actions and designed for fast feedback on your own machine.

## Status

Actio is in early development. It can run workflows from `.workflows/`, execute Docker-backed steps and supported actions, persist local run history, logs, and artifacts, and show runs in a lightweight localhost web UI.

Actio does not create GitHub's automatic `GITHUB_TOKEN`. Workflows that need tokens should use explicit local secrets.

## Requirements

- .NET 10 SDK
- Docker

## Usage

Run a workflow:

```bash
dotnet run --project src/Actio.Cli -- run ci.yml
```

Common commands:

```bash
dotnet run --project src/Actio.Cli -- --help
dotnet run --project src/Actio.Cli -- cache list
dotnet run --project src/Actio.Cli -- web
```

Workflow files live in `.workflows/` at the project root.
