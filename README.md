# OTel sample with .NET Framework

This example demonstrates piecemeal OTel installation on .NET Framework with a custom set of instrumentations and their versions.

The application uses:

- .NET Framework 4.7.2 with the latest `OpenTelemetry` and `OpenTelemetry.Exporter.OpenTelemetryProtocol` NuGet package versions
- AWSSDK.S3 v3 and the latest compatible OTel instrumentation `OpenTelemetry.Instrumentation.AWS` (v 1.11.3). Newer OTel instrumentations target AWS SDK v4
- StackExchange.Redis v1 - there is no OTel instrumentation library that supports StackExchange.Redis v1, so the app implements a manual wrapper for a few Redis commands being used.

Depending on the needs of the application, additional instrumentations may be required.

## How to run

- Add a `.env` file with the following environment variables:

  ```env
  GRAFANA_CLOUD_API_KEY="your key"
  GRAFANA_CLOUD_OTLP_ENDPOINT="https://your-endpoint.grafana.net/otlp"
  GRAFANA_CLOUD_INSTANCE_ID="..."
  ```

- Run dependencies with `docker compose up` (using Linux containers). This will start:
  - S3 emulator ([min.io](https://www.min.io/))
  - Redis
  - Grafana Alloy

- If you want to run against real S3 and external Redis endpoints, update the connection details in [`Program.cs`](./Sample/Program.cs).

- Then build and start the Sample application on your Windows machine with .NET Framework installed.

## Redis instrumentation details

A manual instrumentation example is provided in [InstrumentedDatabase.cs](./Sample/InstrumentedDatabase.cs) and follows [OTel database semantic conventions](https://github.com/open-telemetry/semantic-conventions/blob/main/docs/database/redis.md) as closely as possible.

It emits a database client span and `db.client.operation.duration` metric along with the following attributes:

- `db.operation.name`
- `db.namespace`
- `error.type` (when an exception occurs)
- `server.address` and `server.port` are reported only when Redis is configured with a single endpoint. This instrumentation cannot report a specific Redis instance when clustering is used.

The instrumentation is optimized for the most basic use case when only SET and GET Redis commands are used.

Due to limitations of the Profiling API in StackExchange.Redis v1, it's not trivially possible to downport the [OTel StackExchange.Redis v2 instrumentation](https://github.com/open-telemetry/opentelemetry-dotnet-contrib/tree/main/src/OpenTelemetry.Instrumentation.StackExchangeRedis) - with Redis v1, the [profiling context](https://github.com/StackExchange/StackExchange.Redis/blob/main/docs/Profiling_v1.md#choosing-context) (aka session) needs to be started by the application code.
