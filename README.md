# Actio

Actio is a local-first workflow runner inspired by GitHub Actions.

Current status: early implementation. The CLI can run YAML workflows from `.workflows/`, execute `run:` steps in Docker, run local composite actions with `uses: ./...`, persist run history/logs/artifacts under `ACTIO_HOME`, inspect local action cache entries, and show runs in a localhost web UI.

```bash
dotnet run --project src/Actio.Cli -- run ci.yml
dotnet run --project src/Actio.Cli -- cache list
dotnet run --project src/Actio.Cli -- web
```
