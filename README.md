# NetworkMonitorML

## Overview
`NetworkMonitorML` hosts the machine-learning pipeline that detects anomalies in
monitoring data. It consumes ping/windows from the data service, runs them through the
TimesFM (Google) time-series model, and publishes alerts/predict status updates back
onto RabbitMQ.

Main capabilities:
- Builds rolling windows of latency data (Change & Spike detection).
- Streams requests to the `GradLLM` / TimesFM backend via Rabbit (`oa.chat.*`).
- Aggregates the results into `PredictStatusAlert` messages and notifies the alert service.
- Exposes a health endpoint and readiness signalling (`predictServiceReady` events).

## Project layout
```
NetworkMonitorML/
 ├── Models/                // TimesFMRabbitModel + helper classes
 ├── Services/              // MonitorMLService, RabbitListener, repos
 ├── Tests/                 // Integration tests using FakeSpaceResponder
 ├── appsettings*.json      // Config templates (actual copies in securefiles/)
 └── build-run scripts / Dockerfile
```

## Prerequisites
- .NET 9 SDK
- RabbitMQ 4.x (`devrabbitmq`) with exchanges `oa.chat.*`, `predictAlert`, etc.
- TimesFM backend running (GradLLM container or local app) reachable via Rabbit.
- MariaDB + Redis (for data repo + state caches).
- Config secrets in `securefiles/dev/appsettings-predict.json` and `.env`
  (ServiceID/ServiceAuthKey, EmailEncryptKey, etc.).

## Service identity
- **ServiceID** – use the identifier configured in `appsettings-predict.json`.
- **ServiceAuthKey** – base64 token generated with `NetworkMonitorAuthKeyGen`. This must
  match the value expected by `NetworkMonitorAlert`. Do **not** commit the actual key to
  source control.

## Running locally
```bash
dotnet restore
dotnet run --project NetworkMonitorML.csproj
```

Watch logs for:
- `timesfm summary … flagged=…` – model output
- `Published event MonitorMLItitObj.IsMLReady …` – readiness signalling
- `Published … predictStatusAlerts …` – alerts pushed to the alert service

To control the spike simulator used in tests:
```bash
curl -X POST http://localhost:8080/mode -H 'Content-Type: application/json' \
     -d '{"mode":"spike","spike_interval":1,"spike_latency_ms":1200}'
```

## Configuration
- `securefiles/dev/appsettings-predict.json` – primary configuration (ServiceID,
  routing keys, detection thresholds).
- `.env` – overrides for `EmailEncryptKey`, `RabbitPassword`, `ServiceAuthKey`, etc.
- Logging levels can be tuned via `Logging.LogLevel` (TimesFM + RabbitTransport loggers).

### How the data flows
1. **Retrieve window** – `MonitorMLService` asks `IMonitorMLDataRepo` for the latest
   `PreTrain + PredictWindow` samples of `LocalPingInfo`. The repo pulls the current dataset
   and, if needed, stitches in data from the previous dataset to keep the series contiguous.
2. **Warm-up** – The first `PreTrain` points seed the detector’s robust statistics
   (baseline median/MAD, rolling sigma, martingale seed). They are not scored.
3. **Build prefixes** – For every subsequent point we build a prefix `[0..i]` of round-trip
   times. For example, with `PreTrain = 60` and a 120-sample window, we create 60 prefixes.
4. **TimesFM request** – Those prefixes are wrapped into an OpenAI-style request with
   `horizon = 1` and `quantiles = true` and published to `oa.chat.create`. The GradLLM service
   streams back one-step forecasts (and quantile bands) for each prefix.
5. **Scoring** – We align each forecast with the actual measurement, compute residuals,
   band breaches, run-length/k-of-n persistence, relative shift vs baseline, martingale
   p-values, and cooldown behaviour. Diagnostics (`timesfm sample …`) log the first four
   and last two samples for visibility.
6. **Publish alert** – Results are written back to the database and published as
   `PredictStatusAlert` messages (`alertUpdatePredictStatusAlerts`) so the alert service
   can notify users.

## Testing
Integration tests (`Tests/TimesFmRabbitModelTests.cs`) spin up a fake responder to
simulate TimesFM responses:
```bash
dotnet test
```
These require a local RabbitMQ instance and read configuration from `Tests/appsettings.json`.

## Deployment
- Build the container image with `./build-run` or via CI.
- Ensure `ServiceID`/`ServiceAuthKey` are kept in sync with `NetworkMonitorAlert`.
- Restart the downstream alert service if you update auth tokens so it reloads cached
  `UserInfos` / processor list.

## Related components
- **GradLLM** – Python backend providing TimesFM streaming responses.
- **NetworkMonitorAlert** – consumes the published predict alerts.
- **NetworkMonitorData** – provides state builders and encryption helpers.
- **predict-alert-simulator** – FastAPI service used to simulate spike conditions.
