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

## TimesFM Prediction Monitor

### Model lifecycle
- `MonitorMLService` caches a `TimesFmRabbitModel` per monitor and per mode ("Change" vs "Spike"), initializing adapters on demand with the configured confidence and pre-train values.
- Both modes share the same TimesFM adapter; service-level thresholds determine how many alerts or martingale excursions constitute an incident.
- Each evaluation window warms up on the first `PreTrain` samples, then reuses the adapters for subsequent scoring runs to avoid reconnecting to Rabbit.

### Request/response flow
- Every post-pretrain sample produces a prefix `[0..i]` of round-trip times; these prefixes form the `series` payload that is serialized into an OpenAI-style chat request targeting `google/timesfm-2.5-200m-pytorch`.
- Requests stream over RabbitMQ (`oa.chat.create` and `oa.chat.reply`) via `RabbitTransport`. The adapter concatenates response chunks until the end-of-stream sentinel arrives.
- Forecasts and quantiles are normalized to a consistent shape, tolerating `[v]`, `[[v]]`, or multi-dimensional `BxHx10` quantile layouts. Missing quantiles fall back to symmetric +/-3 sigma bands.

### Detection heuristics
- Bands are widened with robust statistics (rolling MAD-derived sigma) and clamped to absolute (`5 ms`) or relative (`15%` of |forecast|) minimum widths so jitter does not create razor-thin thresholds.
- A change flag requires both persistence (>=3 consecutive breaches or 6-of-12 recent breaches) and a magnitude gate (>=20% shift from the rolling baseline median). Confirmed changes trigger a 30-sample cooldown that freezes sigma to avoid overreacting while the system settles.
- The adapter emits four telemetry channels per sample: `alert` (0/1), `score` (normalized residual), `p` (p-value shaped by persistence), and `martingale` (tempered evidence accumulator with a 25% dead-zone inside the band).

### Observability and alerting
- Informational logs capture batch summaries (`timesfm summary …`) alongside structured JSON samples (first four and last two rows) so on-call engineers can see residuals, gates, and martingale values without replaying the run.
- `MonitorMLService` rolls those predictions into `DetectionResult` objects, counting detections, tracking first-occurrence timestamps, averaging residuals for flagged points, and recording minimum p-values / maximum martingale values. Windows downshift after multiple quiet runs and spring back to the configured maximum as soon as martingale/alerts heat up.
- Updated results persist to the `PredictStatus` records and publish through Rabbit so downstream alerting services can fan out notifications.

## Model configuration reference

`appsettings.json` contains a `ModelParameters` block that lets each backend (TimesFM or MicrosoftMLTS) run with custom sensitivity. Every value is optional: omit a field to fall back to the defaults shown below.

### Shared parameters

- **ChangeConfidence** – probability threshold for change-point detection. Lower values make the change model fire on smaller shifts; higher values demand stronger evidence.
- **SpikeConfidence** – confidence level for spike detection. Lowering it increases sensitivity to one-off spikes; raising it suppresses noise-induced alerts.
- **ChangePreTrain** – number of warm-up samples consumed before the change detector emits results. Larger buffers stabilise the baseline but delay initial visibility.
- **SpikePreTrain** – warm-up window for spike detection. More history reduces noise but slows reaction time.
- **PredictWindow** – maximum number of samples fetched for each run. Bigger windows retain more context; smaller windows reduce compute.
- **SpikeDetectionThreshold** – post-processing guard that requires at least N spike detections before the service marks a spike incident.

### TimesFM-only settings

TimesFM now lets you tailor change and spike behaviour separately. Each profile has the following knobs:

- **RunLength** – minimum consecutive band breaches before the detector fires. Increase to require more persistence; drop to `1` for immediate alerts.
- **KOfNK / KOfNN** – secondary persistence gate (k-of-n). Useful for smoothing bursts without demanding full run-length.
- **MadAlpha** – multiples of rolling sigma added to the band. Higher values widen the band (fewer alerts); smaller values tighten it.
- **MinBandAbs / MinBandRel** – absolute and relative floor for band width so jitter doesn’t collapse thresholds.
- **RollSigmaWindow / BaselineWindow** – lookback windows for sigma and baseline median/MAD. Longer windows smooth volatility; shorter windows adapt faster.
- **SigmaCooldown** – how long to freeze sigma after a confirmed change to avoid immediate retriggers.
- **MinRelShift** – minimum relative deviation (vs. baseline) required in addition to persistence.
- **SampleRows / NearMissFraction / LogJson** – logging controls for the diagnostics payload.

Configure shared defaults under `TimesFmSettings`, and override per mode with `TimesFmChangeSettings` and `TimesFmSpikeSettings` inside each model entry.

Because `MonitorMLService` replays settings on every reuse, you can adjust the config and restart the service to adopt new thresholds without code changes.

### Testing touchpoints
- Integration tests spin up an in-process Rabbit responder to exercise happy paths, quantile fallbacks, streaming multi-chunk replies, cooldown behavior, and failure cases (unknown forecast shapes).
- Run them locally with `dotnet test`; the suite lives under `Tests/TimesFmRabbitModelTests.cs`.

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
