# Security Overview

Actio applies a Docker Secure Baseline by default and offers an explicit Strict profile. Central policies deny privileged containers, Docker socket mounts, host namespaces, unsafe capabilities, unconstrained ports, and filesystem mount escapes. Runtime resources and logs are bounded, secrets require explicit binding, and recursive host reads reject filesystem links.

Secure Baseline preserves compatibility by allowing the image's configured user, including root, a writable container root filesystem and workspace, and outbound runtime networking. Use Strict when a workload requires verified non-root execution, a read-only root filesystem, dropped capabilities, and tighter network isolation.

The local web server binds only to literal loopback HTTP addresses and applies same-origin checks to browser mutations. Run and cache metadata use atomic writes and cross-process coordination where concurrent access is expected.

Residual risks remain: Docker is not a VM boundary, the workspace is writable, the Docker daemon and host kernel are trusted, other same-user local processes can reach loopback services, and reviewed external code still executes locally. Actio never creates an automatic `GITHUB_TOKEN`.

The optional managed Git pre-push hook is a convenience gate, not a security boundary. It runs before the remote changes and blocks ordinary pushes on workflow failure, but users can bypass Git hooks with `git push --no-verify`.

Use immutable action commit SHAs and image digests where possible. See [SECURITY.md](../SECURITY.md) for private reporting.
