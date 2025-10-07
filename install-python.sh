#!/usr/bin/env bash
set -euo pipefail

# Provision Python environment, system user, and systemd unit for the Predict Alert Simulator.

if [[ ${EUID} -ne 0 ]]; then
  echo "This installer must be run as root." >&2
  exit 1
fi

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
VENV_DIR="${ROOT_DIR}/.venv"
REQUIREMENTS_FILE="${ROOT_DIR}/requirements.txt"
SERVICE_TEMPLATE="${ROOT_DIR}/predict-alert-simulator.service"
SERVICE_DEST="/etc/systemd/system/predict-alert-simulator.service"
ENV_FILE="/etc/default/predict-alert-simulator"

SERVICE_USER="predictsim"
SERVICE_GROUP="${SERVICE_USER}"
SERVICE_HOME="/var/lib/predict-alert-simulator"

if ! command -v python3 >/dev/null 2>&1; then
  echo "python3 is required but not found in PATH." >&2
  exit 1
fi

if [[ ! -f "${REQUIREMENTS_FILE}" ]]; then
  echo "requirements.txt not found at ${REQUIREMENTS_FILE}" >&2
  exit 1
fi

if [[ ! -f "${SERVICE_TEMPLATE}" ]]; then
  echo "Systemd unit template not found at ${SERVICE_TEMPLATE}" >&2
  exit 1
fi

if ! getent group "${SERVICE_GROUP}" >/dev/null 2>&1; then
  groupadd --system "${SERVICE_GROUP}"
fi

if ! id -u "${SERVICE_USER}" >/dev/null 2>&1; then
  useradd --system \
    --gid "${SERVICE_GROUP}" \
    --home-dir "${SERVICE_HOME}" \
    --create-home \
    --shell /usr/sbin/nologin \
    "${SERVICE_USER}"
fi

install -d -o "${SERVICE_USER}" -g "${SERVICE_GROUP}" "${SERVICE_HOME}"

if [[ ! -d "${VENV_DIR}" ]]; then
  python3 -m venv "${VENV_DIR}"
fi

# shellcheck disable=SC1090
source "${VENV_DIR}/bin/activate"

python -m pip install --upgrade pip
python -m pip install -r "${REQUIREMENTS_FILE}"

deactivate

chown -R "${SERVICE_USER}:${SERVICE_GROUP}" "${VENV_DIR}"

sed "s|__INSTALL_DIR__|${ROOT_DIR}|g" "${SERVICE_TEMPLATE}" > "${SERVICE_DEST}"
chmod 644 "${SERVICE_DEST}"

if [[ ! -f "${ENV_FILE}" ]]; then
  cat <<'EOF' > "${ENV_FILE}"
# This file is created in /etc/default/. Edit it aftet install. It contains options passed to predict_alert_simulator.py
# Example: SIMULATOR_OPTS="--port 8080 --mode spike --spike-interval 5"
SIMULATOR_OPTS="--port 8080 --mode normal"
EOF
fi

chmod 644 "${ENV_FILE}"

systemctl daemon-reload

cat <<EOF

Installation complete.

Service user: ${SERVICE_USER}
Virtualenv:   ${VENV_DIR}
Unit file:    ${SERVICE_DEST}
Env config:   ${ENV_FILE}

To start the simulator:
  systemctl enable --now predict-alert-simulator.service

To change launch options edit ${ENV_FILE} and restart:
  systemctl restart predict-alert-simulator.service

EOF
