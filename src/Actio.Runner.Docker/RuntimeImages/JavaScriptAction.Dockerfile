ARG BASE_IMAGE=node:24.18.0-bookworm-slim@sha256:6f7b03f7c2c8e2e784dcf9295400527b9b1270fd37b7e9a7285cf83b6951452d
FROM ${BASE_IMAGE}

ARG GIT_VERSION
ARG CA_CERTIFICATES_VERSION

RUN apt-get update \
    && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
        git="${GIT_VERSION}" \
        ca-certificates="${CA_CERTIFICATES_VERSION}" \
    && rm -rf /var/lib/apt/lists/* \
    && node --version \
    && git --version \
    && git config --system --add safe.directory /workspace \
    && test -s /etc/ssl/certs/ca-certificates.crt

USER node
