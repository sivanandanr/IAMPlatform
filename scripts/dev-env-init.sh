#!/usr/bin/env bash
# S1-03: create the LOCAL DEVELOPMENT .env for compose.yaml from infra/docker/dev.env.template.
# Each __GENERATE__ value gets a fresh random secret. Secrets are never printed.
# Usage: scripts/dev-env-init.sh [--force]     (--force overwrites an existing .env)
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
template="$root/infra/docker/dev.env.template"
target="$root/.env"

if [[ -e "$target" && "${1:-}" != "--force" ]]; then
  echo ".env already exists; keeping it. Use --force to regenerate (existing database volumes keep the OLD passwords)." >&2
  exit 0
fi

# 32 alphanumeric chars from the kernel CSPRNG, prefixed so SQL Server complexity rules
# (upper, lower, digit, symbol) always hold. No '$' or quotes: safe for compose .env parsing.
gen() {
  local s=""
  # Bounded reads (no SIGPIPE under pipefail); loop until 32 characters survive the filter.
  while ((${#s} < 32)); do
    s+="$(head -c 64 /dev/urandom | LC_ALL=C tr -dc 'A-Za-z0-9')"
  done
  printf 'Aa1-%s' "${s:0:32}"
}

umask 077
tmp="$(mktemp "$root/.env.tmp.XXXXXX")"
trap 'rm -f "$tmp"' EXIT
while IFS= read -r line || [[ -n "$line" ]]; do
  line="${line%$'\r'}"
  if [[ "$line" == *=__GENERATE__ ]]; then
    printf '%s=%s\n' "${line%%=*}" "$(gen)" >>"$tmp"
  else
    printf '%s\n' "$line" >>"$tmp"
  fi
done <"$template"
mv "$tmp" "$target"
trap - EXIT
echo "Wrote $target with generated secrets (permissions 600). It is gitignored; do not commit or share it."
