#!/usr/bin/env bash
set -Eeuo pipefail

readonly SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
# When invoked from pre-push the caller passes CITUS_REPO_ROOT explicitly so
# the bump targets the worktree being pushed, not the main repo where the
# hook script lives. Fall back to script-relative discovery for direct runs.
readonly REPO_ROOT="${CITUS_REPO_ROOT:-$(cd -- "${SCRIPT_DIR}/../.." && pwd)}"
readonly VERSION_FILE="${REPO_ROOT}/VERSION"
readonly BUILD_PROPS_FILE="${REPO_ROOT}/backend/Directory.Build.props"

trim_version() {
  local raw="$1"
  raw="${raw%$'\r'}"
  raw="${raw#"${raw%%[![:space:]]*}"}"
  raw="${raw%"${raw##*[![:space:]]}"}"
  printf '%s' "${raw}"
}

read_current_version() {
  [[ -f "${VERSION_FILE}" ]] || {
    echo "Missing ${VERSION_FILE}" >&2
    exit 1
  }

  local version
  version="$(trim_version "$(head -n 1 "${VERSION_FILE}")")"
  [[ "${version}" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]] || {
    echo "Unsupported version format in ${VERSION_FILE}: ${version}" >&2
    exit 1
  }

  printf '%s' "${version}"
}

bump_version() {
  local current="$1"
  IFS='.' read -r major minor build revision <<< "${current}"

  local width="${#revision}"
  if (( width < 2 )); then
    width=2
  fi

  local next_revision
  printf -v next_revision "%0*d" "${width}" "$((10#${revision} + 1))"
  printf '%s.%s.%s.%s' "${major}" "${minor}" "${build}" "${next_revision}"
}

update_build_props() {
  local version="$1"
  sed -i -E "s|<Version>[^<]+</Version>|<Version>${version}</Version>|" "${BUILD_PROPS_FILE}"
  sed -i -E "s|<AssemblyVersion>[^<]+</AssemblyVersion>|<AssemblyVersion>${version}</AssemblyVersion>|" "${BUILD_PROPS_FILE}"
  sed -i -E "s|<FileVersion>[^<]+</FileVersion>|<FileVersion>${version}</FileVersion>|" "${BUILD_PROPS_FILE}"
  sed -i -E "s|<InformationalVersion>[^<]+</InformationalVersion>|<InformationalVersion>${version}</InformationalVersion>|" "${BUILD_PROPS_FILE}"
}

main() {
  local current_version next_version
  current_version="$(read_current_version)"
  next_version="$(bump_version "${current_version}")"

  printf '%s\n' "${next_version}" > "${VERSION_FILE}"
  update_build_props "${next_version}"
  printf 'Bumped Citus version: %s -> %s\n' "${current_version}" "${next_version}"
}

main "$@"
