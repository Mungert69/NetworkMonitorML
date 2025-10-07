"""
FastAPI-based HTTP service for simulating network latency patterns that trigger Predict alerts.

Run directly with python, for example:

    python predict_alert_simulator.py --port 8080 --mode normal

Switch to slow or spiky behavior to provoke alerts:

    python predict_alert_simulator.py --mode slow --slow-latency-ms 250
    python predict_alert_simulator.py --mode spike --spike-interval 5 --spike-latency-ms 1200
"""
from __future__ import annotations

import argparse
import asyncio
import logging
import random
import time
from dataclasses import dataclass
from enum import Enum
from typing import Optional

import uvicorn
from fastapi import FastAPI, HTTPException, Request, status
from pydantic import BaseModel, Field


logger = logging.getLogger("predict-alert-simulator")


class Mode(str, Enum):
    """Supported latency simulation modes."""

    NORMAL = "normal"
    SLOW = "slow"
    SPIKE = "spike"


@dataclass
class DelayDecision:
    """Snapshot of the latency choice for a single request."""

    request_number: int
    mode: Mode
    configured_delay_ms: float
    applied_delay_ms: float
    is_spike: bool


class LatencySimulator:
    """Calculates request delays based on the configured mode."""

    def __init__(
        self,
        *,
        mode: Mode,
        normal_latency_ms: float,
        slow_latency_ms: float,
        spike_latency_ms: float,
        spike_interval: int,
        jitter_ms: float,
    ) -> None:
        self._mode = mode
        self.normal_latency_ms = max(0.0, normal_latency_ms)
        self.slow_latency_ms = max(0.0, slow_latency_ms)
        self.spike_latency_ms = max(0.0, spike_latency_ms)
        self.spike_interval = max(1, spike_interval)
        self.jitter_ms = max(0.0, jitter_ms)
        self._request_counter = 0
        self._lock = asyncio.Lock()

    @property
    def mode(self) -> Mode:
        return self._mode

    async def next_delay(self) -> DelayDecision:
        """Determine the next delay and advance the request counter."""
        async with self._lock:
            self._request_counter += 1
            request_number = self._request_counter
            mode = self._mode
            slow_latency_ms = self.slow_latency_ms
            spike_latency_ms = self.spike_latency_ms
            spike_interval = self.spike_interval
            normal_latency_ms = self.normal_latency_ms
            jitter_ms = self.jitter_ms

        base_delay_ms = self._select_base_delay(
            mode=mode,
            request_number=request_number,
            slow_latency_ms=slow_latency_ms,
            spike_latency_ms=spike_latency_ms,
            spike_interval=spike_interval,
            normal_latency_ms=normal_latency_ms,
        )
        applied_delay_ms = self._apply_jitter(base_delay_ms, jitter_ms=jitter_ms)
        is_spike = mode is Mode.SPIKE and base_delay_ms == spike_latency_ms

        return DelayDecision(
            request_number=request_number,
            mode=mode,
            configured_delay_ms=base_delay_ms,
            applied_delay_ms=applied_delay_ms,
            is_spike=is_spike,
        )

    async def snapshot(self) -> dict:
        """Return the current configuration and counters."""
        async with self._lock:
            return {
                "mode": self._mode.value,
                "normal_latency_ms": self.normal_latency_ms,
                "slow_latency_ms": self.slow_latency_ms,
                "spike_latency_ms": self.spike_latency_ms,
                "spike_interval": self.spike_interval,
                "jitter_ms": self.jitter_ms,
                "requests_seen": self._request_counter,
            }

    async def update(
        self,
        *,
        mode: Optional[Mode] = None,
        normal_latency_ms: Optional[float] = None,
        slow_latency_ms: Optional[float] = None,
        spike_latency_ms: Optional[float] = None,
        spike_interval: Optional[int] = None,
        jitter_ms: Optional[float] = None,
        reset_counter: bool = False,
    ) -> None:
        """Update configuration values."""
        async with self._lock:
            if mode is not None:
                self._mode = mode
            if normal_latency_ms is not None:
                self.normal_latency_ms = max(0.0, normal_latency_ms)
            if slow_latency_ms is not None:
                self.slow_latency_ms = max(0.0, slow_latency_ms)
            if spike_latency_ms is not None:
                self.spike_latency_ms = max(0.0, spike_latency_ms)
            if spike_interval is not None and spike_interval > 0:
                self.spike_interval = spike_interval
            if jitter_ms is not None and jitter_ms >= 0.0:
                self.jitter_ms = jitter_ms
            if reset_counter:
                self._request_counter = 0

    @staticmethod
    def _select_base_delay(
        *,
        mode: Mode,
        request_number: int,
        normal_latency_ms: float,
        slow_latency_ms: float,
        spike_latency_ms: float,
        spike_interval: int,
    ) -> float:
        if mode is Mode.SLOW:
            return slow_latency_ms

        if mode is Mode.SPIKE:
            if spike_interval <= 0:
                return spike_latency_ms
            if request_number % spike_interval == 0:
                return spike_latency_ms

        return normal_latency_ms

    @staticmethod
    def _apply_jitter(base_delay_ms: float, *, jitter_ms: float) -> float:
        if jitter_ms <= 0.0:
            return max(0.0, base_delay_ms)
        delta = random.uniform(-jitter_ms, jitter_ms)
        return max(0.0, base_delay_ms + delta)


class ModeUpdateRequest(BaseModel):
    mode: Optional[Mode] = Field(
        None, description="Switch to a different simulation mode."
    )
    normal_latency_ms: Optional[float] = Field(
        None, ge=0.0, description="Override the baseline latency for normal mode."
    )
    slow_latency_ms: Optional[float] = Field(
        None, ge=0.0, description="Override the latency used in slow mode."
    )
    spike_latency_ms: Optional[float] = Field(
        None,
        ge=0.0,
        description="Override the latency applied to spike requests.",
    )
    spike_interval: Optional[int] = Field(
        None,
        gt=0,
        description="Serve one spike every N requests while in spike mode.",
    )
    jitter_ms: Optional[float] = Field(
        None,
        ge=0.0,
        description="Random jitter added to each request delay (uniform distribution).",
    )
    reset_counter: bool = Field(
        False,
        description="Reset the internal request counter (affects spike cadence).",
    )
    token: Optional[str] = Field(
        None, description="Change-control token if the service was started with one."
    )


app = FastAPI(
    title="Predict Alert Simulator",
    description="Simulate HTTP latency patterns to exercise Predict alerting.",
    version="0.1.0",
)

# Default simulator, can be overridden in main().
app.state.simulator = LatencySimulator(
    mode=Mode.NORMAL,
    normal_latency_ms=50.0,
    slow_latency_ms=250.0,
    spike_latency_ms=1000.0,
    spike_interval=10,
    jitter_ms=5.0,
)
app.state.control_token: Optional[str] = None


@app.get("/healthz", tags=["meta"])
async def healthcheck() -> dict:
    """Lightweight endpoint with no induced delay."""
    return {"status": "ok"}


@app.get("/", tags=["traffic"])
async def serve(request: Request) -> dict:
    """Primary endpoint that applies simulated latency before responding."""
    simulator: LatencySimulator = request.app.state.simulator
    decision = await simulator.next_delay()

    request_start = time.perf_counter()
    await asyncio.sleep(decision.applied_delay_ms / 1000.0)
    observed_latency_ms = (time.perf_counter() - request_start) * 1000.0

    payload = {
        "status": "ok",
        "mode": decision.mode.value,
        "request_number": decision.request_number,
        "configured_delay_ms": round(decision.configured_delay_ms, 3),
        "applied_delay_ms": round(decision.applied_delay_ms, 3),
        "observed_handler_ms": round(observed_latency_ms, 3),
        "spike_triggered": decision.is_spike,
        "timestamp": time.time(),
    }
    logger.info(
        "handled request %s mode=%s applied_delay_ms=%.3f spike=%s",
        decision.request_number,
        decision.mode.value,
        decision.applied_delay_ms,
        decision.is_spike,
    )
    return payload


@app.get("/config", tags=["meta"])
async def get_config(request: Request) -> dict:
    """Inspect the current simulator configuration."""
    simulator: LatencySimulator = request.app.state.simulator
    snapshot = await simulator.snapshot()
    return snapshot


@app.post("/mode", tags=["control"])
async def update_mode(request: Request, update_request: ModeUpdateRequest) -> dict:
    """Adjust the simulator mode or latency characteristics."""
    token_required: Optional[str] = request.app.state.control_token
    if token_required is not None:
        if update_request.token != token_required:
            raise HTTPException(
                status_code=status.HTTP_401_UNAUTHORIZED,
                detail="Invalid control token.",
            )

    simulator: LatencySimulator = request.app.state.simulator
    await simulator.update(
        mode=update_request.mode,
        normal_latency_ms=update_request.normal_latency_ms,
        slow_latency_ms=update_request.slow_latency_ms,
        spike_latency_ms=update_request.spike_latency_ms,
        spike_interval=update_request.spike_interval,
        jitter_ms=update_request.jitter_ms,
        reset_counter=update_request.reset_counter,
    )
    snapshot = await simulator.snapshot()

    logger.info(
        "updated simulator config %s",
        snapshot,
    )
    return snapshot


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="FastAPI service that simulates latency patterns to trigger Predict alerts.",
    )
    parser.add_argument("--host", default="0.0.0.0", help="Host/IP to bind.")
    parser.add_argument(
        "--port", type=int, default=8080, help="Port number for the HTTP server."
    )
    parser.add_argument(
        "--mode",
        choices=[mode.value for mode in Mode],
        default=Mode.NORMAL.value,
        help="Initial latency mode.",
    )
    parser.add_argument(
        "--normal-latency-ms",
        type=float,
        default=50.0,
        help="Baseline latency for normal mode.",
    )
    parser.add_argument(
        "--slow-latency-ms",
        type=float,
        default=250.0,
        help="Latency applied for every request when in slow mode.",
    )
    parser.add_argument(
        "--spike-latency-ms",
        type=float,
        default=1000.0,
        help="Latency applied to spike requests in spike mode.",
    )
    parser.add_argument(
        "--spike-interval",
        type=int,
        default=10,
        help="Number of requests between spikes while in spike mode.",
    )
    parser.add_argument(
        "--jitter-ms",
        type=float,
        default=5.0,
        help="Random jitter applied to each response in milliseconds.",
    )
    parser.add_argument(
        "--token",
        type=str,
        default=None,
        help="Optional token required for runtime configuration changes.",
    )
    parser.add_argument(
        "--log-level",
        default="info",
        choices=["critical", "error", "warning", "info", "debug", "trace"],
        help="Logging verbosity.",
    )
    return parser.parse_args()


def configure_logging(log_level: str) -> None:
    logging.basicConfig(
        level=log_level.upper(),
        format="%(asctime)s %(levelname)s %(name)s - %(message)s",
    )


def configure_app_from_args(args: argparse.Namespace) -> None:
    """Apply CLI arguments to the global FastAPI app instance."""
    simulator = LatencySimulator(
        mode=Mode(args.mode),
        normal_latency_ms=args.normal_latency_ms,
        slow_latency_ms=args.slow_latency_ms,
        spike_latency_ms=args.spike_latency_ms,
        spike_interval=max(1, args.spike_interval),
        jitter_ms=max(0.0, args.jitter_ms),
    )
    app.state.simulator = simulator
    app.state.control_token = args.token
    logger.info(
        "Simulator initialised with config %s",
        {
            "mode": simulator.mode.value,
            "normal_latency_ms": simulator.normal_latency_ms,
            "slow_latency_ms": simulator.slow_latency_ms,
            "spike_latency_ms": simulator.spike_latency_ms,
            "spike_interval": simulator.spike_interval,
            "jitter_ms": simulator.jitter_ms,
            "control_token": "set" if args.token else "not-set",
        },
    )


def main() -> None:
    args = parse_args()
    configure_logging(args.log_level)
    configure_app_from_args(args)
    uvicorn.run(
        app,
        host=args.host,
        port=args.port,
        log_level=args.log_level,
        loop="asyncio",
    )


if __name__ == "__main__":
    main()
