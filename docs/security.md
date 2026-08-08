# Security Overview

Actio applies a Docker Secure Baseline by default and offers an explicit Strict profile. Central policies deny privileged containers, Docker socket mounts, host namespaces, unsafe capabilities, unconstrained ports, and filesystem mount escapes. Runtime resources and logs are bounded, secrets require explicit binding, and recursive host reads reject filesystem links.

Recursive YAML aliases are rejected and matrix expansion is bounded before execution planning. Runtime workflow and local-action references must resolve canonically inside their owning project or action root. Reusable workflow calls retain the root run's protected workspace masks, while their owner-only temporary environment files are removed after execution. Docker image operands are separated from Docker CLI options, and artifact writes use collision-safe strict descendants of the run artifact root.

Secure Baseline preserves compatibility by allowing the image's configured user, including root, a writable container root filesystem and workspace, and outbound runtime networking. Use Strict when a workload requires verified non-root execution, a read-only root filesystem, dropped capabilities, and tighter network isolation.

The local web server binds only to literal loopback HTTP addresses, rejects requests for any authority other than its bound address, and applies same-origin checks to browser mutations. Run and cache metadata use atomic writes and cross-process coordination where concurrent access is expected.

Residual risks remain: Docker is not a VM boundary, the workspace is writable, the Docker daemon and host kernel are trusted, and reviewed external code still executes locally. The web control plane does not yet use capability authentication, so same-user processes and other local OS accounts able to reach loopback remain an unresolved risk. Actio never creates an automatic `GITHUB_TOKEN`.

The optional managed Git pre-push hook is a convenience gate, not a security boundary. It runs before the remote changes and blocks ordinary pushes on workflow failure, but users can bypass Git hooks with `git push --no-verify`.

Use immutable action commit SHAs and image digests where possible. See [SECURITY.md](../SECURITY.md) for private reporting.
