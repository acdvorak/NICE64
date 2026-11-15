#!/usr/bin/env bash

set -euo pipefail

cd -- "$(dirname "${BASH_SOURCE[0]}")" || exit 1

source .venv/bin/activate

python nice64.py "$@"
