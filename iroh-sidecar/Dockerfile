FROM rust:1.89 AS build
WORKDIR /src
COPY iroh-sidecar/Cargo.toml ./
COPY iroh-sidecar/src ./src
RUN cargo build --release

FROM debian:bookworm-slim
WORKDIR /app
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates curl \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /src/target/release/nexus-iroh-sidecar /usr/local/bin/nexus-iroh-sidecar
EXPOSE 49152
ENTRYPOINT ["nexus-iroh-sidecar"]
