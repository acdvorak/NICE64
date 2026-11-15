#!/usr/bin/env bash

set -euo pipefail

cd -- "$(dirname "${BASH_SOURCE[0]}")" || exit 1

python3 -m venv .venv
source .venv/bin/activate
python -m pip install -r requirements.txt
