# Security Overview

Actio applies a Docker Secure Baseline by default and offers an explicit Strict profile. Central policies deny privileged containers, Docker socket mounts, host namespaces, unsafe capabilities, unconstrained ports, and filesystem mount escapes. Runtime resources and logs are bounded, secrets require explicit binding, and recursive host reads reject filesystem links.

The local web server binds only to literal loopback HTTP addresses and applies same-origin checks to browser mutations. Run and cache metadata use atomic writes and cross-process coordination where concurrent access is expected.

Residual risks remain: Docker is not a VM boundary, the workspace is writable, the Docker daemon and host kernel are trusted, other same-user local processes can reach loopback services, and reviewed external code still executes locally. Actio never creates an automatic `GITHUB_TOKEN`.

Use immutable action commit SHAs and image digests where possible. See [SECURITY.md](../SECURITY.md) for private reporting.
