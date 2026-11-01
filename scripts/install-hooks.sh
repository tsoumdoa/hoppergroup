#!/bin/sh
set -eu
cd "$(dirname "$0")/.."
git config core.hooksPath .githooks
printf '%s\n' 'Installed local hooks: core.hooksPath=.githooks'
