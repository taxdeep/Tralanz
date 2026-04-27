#!/usr/bin/env bash
# -----------------------------------------------------------------------------
# Citus auto-bump (post-reset 2026-04-27).
#
# Version format: MAJOR.MINOR.PATCH.BUILD
#   MAJOR  decimal (typically 1 char, no upper cap)
#   MINOR  decimal, zero-padded to 3 chars (000-999)
#   PATCH  base-36, zero-padded to 3 chars (000-ZZZ, 36^3 = 46 656)
#   BUILD  base-36, zero-padded to 4 chars (0000-ZZZZ, 36^4 = 1 679 616)
#
# Each push bumps BUILD. On overflow it carries:
#   BUILD  ZZZZ -> 0000, PATCH += 1
#   PATCH  ZZZ  -> 000,  MINOR += 1
#   MINOR  999  -> 000,  MAJOR += 1
#
# The script only touches the human display:
#   - the VERSION file (single line)
#   - <InformationalVersion> in backend/Directory.Build.props
# It does NOT touch <Version>/<AssemblyVersion>/<FileVersion>; those stay at
# 0.0.0.0 because System.Version requires decimal-only parts capped at 65534
# and the base-36 BUILD range exceeds that. See Directory.Build.props for the
# longer note. AssemblyVersion stability also keeps binding redirects quiet
# across pushes.
# -----------------------------------------------------------------------------
set -Eeuo pipefail

readonly SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
# When invoked from pre-push the caller passes CITUS_REPO_ROOT explicitly so
# the bump targets the worktree being pushed, not the main repo where the
# hook script lives. Fall back to script-relative discovery for direct runs.
readonly REPO_ROOT="${CITUS_REPO_ROOT:-$(cd -- "${SCRIPT_DIR}/../.." && pwd)}"
readonly VERSION_FILE="${REPO_ROOT}/VERSION"
readonly BUILD_PROPS_FILE="${REPO_ROOT}/backend/Directory.Build.props"

readonly B36_CHARS="0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ"
readonly B36_PATCH_MAX=46655    # 36^3 - 1
readonly B36_BUILD_MAX=1679615  # 36^4 - 1
readonly DEC_MINOR_MAX=999

trim_version() {
  local raw="$1"
  raw="${raw%$'\r'}"
  raw="${raw#"${raw%%[![:space:]]*}"}"
  raw="${raw%"${raw##*[![:space:]]}"}"
  printf '%s' "${raw}"
}

# Convert a base-36 string (case-insensitive) to a decimal integer.
b36_decode() {
  local input="${1^^}"
  local n=0 i ch prefix
  for (( i=0; i<${#input}; i++ )); do
    ch="${input:i:1}"
    prefix="${B36_CHARS%%${ch}*}"
    if [[ "${prefix}" == "${B36_CHARS}" ]]; then
      echo "Invalid base-36 character '${ch}' in '${input}'." >&2
      exit 1
    fi
    n=$(( n * 36 + ${#prefix} ))
  done
  printf '%d' "${n}"
}

# Convert a decimal integer to a base-36 string, zero-padded to ${width}.
b36_encode() {
  local n="$1"
  local width="$2"
  local s=""
  if (( n == 0 )); then
    s="0"
  fi
  while (( n > 0 )); do
    s="${B36_CHARS:n%36:1}${s}"
    n=$(( n / 36 ))
  done
  while (( ${#s} < width )); do
    s="0${s}"
  done
  printf '%s' "${s}"
}

read_current_version() {
  [[ -f "${VERSION_FILE}" ]] || {
    echo "Missing ${VERSION_FILE}" >&2
    exit 1
  }

  local version
  version="$(trim_version "$(head -n 1 "${VERSION_FILE}")")"
  if ! [[ "${version}" =~ ^[0-9]+\.[0-9]{3}\.[0-9A-Za-z]{3}\.[0-9A-Za-z]{4}$ ]]; then
    echo "Unsupported version format in ${VERSION_FILE}: '${version}' (expected MAJOR.MINOR.PATCH.BUILD with widths 1+/3/3/4)." >&2
    exit 1
  fi

  printf '%s' "${version}"
}

bump_version() {
  local current="$1"
  IFS='.' read -r major minor patch build <<< "${current}"

  local major_dec minor_dec patch_dec build_dec
  major_dec=$(( 10#${major} ))
  minor_dec=$(( 10#${minor} ))
  patch_dec=$(b36_decode "${patch}")
  build_dec=$(b36_decode "${build}")

  build_dec=$(( build_dec + 1 ))
  if (( build_dec > B36_BUILD_MAX )); then
    build_dec=0
    patch_dec=$(( patch_dec + 1 ))
    if (( patch_dec > B36_PATCH_MAX )); then
      patch_dec=0
      minor_dec=$(( minor_dec + 1 ))
      if (( minor_dec > DEC_MINOR_MAX )); then
        minor_dec=0
        major_dec=$(( major_dec + 1 ))
      fi
    fi
  fi

  local next_minor next_patch next_build
  printf -v next_minor '%03d' "${minor_dec}"
  next_patch="$(b36_encode "${patch_dec}" 3)"
  next_build="$(b36_encode "${build_dec}" 4)"

  printf '%d.%s.%s.%s' "${major_dec}" "${next_minor}" "${next_patch}" "${next_build}"
}

update_build_props() {
  local version="$1"
  # Only InformationalVersion follows the human display. Version /
  # AssemblyVersion / FileVersion stay at 0.0.0.0 — see the file's header
  # comment for why.
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
