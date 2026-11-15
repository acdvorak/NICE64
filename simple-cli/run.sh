#!/usr/bin/env bash

set -euo pipefail

cd -- "/Users/acdvorak/dev/nice64/simple-cli" || exit 1

source .venv/bin/activate

python nice64.py "$@"
