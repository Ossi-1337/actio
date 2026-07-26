# Security

Actio is local-first software under active development. Security fixes apply to the latest revision only.

## Report A Vulnerability

Use GitHub Private Vulnerability Reporting for this repository. Do not open a public issue containing exploit details, credentials, or sensitive host information.

Private reporting must be enabled in repository settings before the first public release. If it is unavailable, contact the repository owner privately.

## Trust Boundaries

- Docker containers reduce exposure but are not equivalent to virtual machines.
- The writable workspace and Docker daemon remain meaningful host trust boundaries.
- The web control plane accepts literal loopback HTTP addresses only. Other local processes running as the same user are outside its protection boundary.
- Actio does not create GitHub's automatic `GITHUB_TOKEN` or an OIDC token.
- External action code executes locally. Commit SHAs and image digests are safer than mutable tags.
- Secrets must be explicitly bound and remain the workflow author's responsibility.

See [the security overview](docs/security.md) for the implemented controls and residual risks.
