#!/usr/bin/env bash
# -----------------------------------------------------------------------------
# Citus auto-bump (six-segment scheme, 2026-04-28).
#
# Display form (in VERSION + <InformationalVersion>):
#
#     X.XX.XXX.YYYY.XX.YY
#
#   Segment 1  X     decimal, 1+ char  (no upper cap; widens past 9)
#   Segment 2  XX    decimal, 2 chars  (00-99)
#   Segment 3  XXX   decimal, 3 chars  (000-999)
#   Segment 4  YYYY  base-36, 4 chars  (0000-ZZZZ, 36^4 = 1 679 616 values)
#   Segment 5  XX    decimal, 2 chars  (00-99)
#   Segment 6  YY    base-36, 2 chars  (00-ZZ, 36^2 = 1 296 values)
#
# Every push bumps segment 6 (the rightmost YY) by +1 and carries left on
# overflow:
#
#   YY      ZZ   -> 00,   segment 5 += 1
#   XX (5)  99   -> 00,   segment 4 += 1
#   YYYY    ZZZZ -> 0000, segment 3 += 1
#   XXX     999  -> 000,  segment 2 += 1
#   XX (2)  99   -> 00,   segment 1 += 1
#   X (1)   no cap (allowed to widen past 9)
#
# This script only touches the human-display fields:
#   - the VERSION file (single line)
#   - <InformationalVersion> in backend/Directory.Build.props
#
# It does NOT touch <Version>/<AssemblyVersion>/<FileVersion>; those stay
# at 0.0.0.0 because System.Version requires 4 strictly-decimal parts each
# capped at 65 534, and the base-36 YYYY slot can exceed that. Keeping
# AssemblyVersion stable also avoids binding-redirect churn.
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
readonly SEG2_DEC_MAX=99      # 10^2 - 1
readonly SEG3_DEC_MAX=999     # 10^3 - 1
readonly SEG4_B36_MAX=1679615 # 36^4 - 1
readonly SEG5_DEC_MAX=99      # 10^2 - 1
readonly SEG6_B36_MAX=1295    # 36^2 - 1

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
  if ! [[ "${version}" =~ ^[0-9]+\.[0-9]{2}\.[0-9]{3}\.[0-9A-Za-z]{4}\.[0-9]{2}\.[0-9A-Za-z]{2}$ ]]; then
    echo "Unsupported version format in ${VERSION_FILE}: '${version}' (expected X.XX.XXX.YYYY.XX.YY with widths 1+/2/3/4/2/2)." >&2
    exit 1
  fi

  printf '%s' "${version}"
}

bump_version() {
  local current="$1"
  IFS='.' read -r seg1 seg2 seg3 seg4 seg5 seg6 <<< "${current}"

  local seg1_dec seg2_dec seg3_dec seg4_dec seg5_dec seg6_dec
  seg1_dec=$(( 10#${seg1} ))
  seg2_dec=$(( 10#${seg2} ))
  seg3_dec=$(( 10#${seg3} ))
  seg4_dec=$(b36_decode "${seg4}")
  seg5_dec=$(( 10#${seg5} ))
  seg6_dec=$(b36_decode "${seg6}")

  seg6_dec=$(( seg6_dec + 1 ))
  if (( seg6_dec > SEG6_B36_MAX )); then
    seg6_dec=0
    seg5_dec=$(( seg5_dec + 1 ))
    if (( seg5_dec > SEG5_DEC_MAX )); then
      seg5_dec=0
      seg4_dec=$(( seg4_dec + 1 ))
      if (( seg4_dec > SEG4_B36_MAX )); then
        seg4_dec=0
        seg3_dec=$(( seg3_dec + 1 ))
        if (( seg3_dec > SEG3_DEC_MAX )); then
          seg3_dec=0
          seg2_dec=$(( seg2_dec + 1 ))
          if (( seg2_dec > SEG2_DEC_MAX )); then
            seg2_dec=0
            seg1_dec=$(( seg1_dec + 1 ))
          fi
        fi
      fi
    fi
  fi

  local next_seg2 next_seg3 next_seg4 next_seg5 next_seg6
  printf -v next_seg2 '%02d' "${seg2_dec}"
  printf -v next_seg3 '%03d' "${seg3_dec}"
  next_seg4="$(b36_encode "${seg4_dec}" 4)"
  printf -v next_seg5 '%02d' "${seg5_dec}"
  next_seg6="$(b36_encode "${seg6_dec}" 2)"

  printf '%d.%s.%s.%s.%s.%s' "${seg1_dec}" "${next_seg2}" "${next_seg3}" "${next_seg4}" "${next_seg5}" "${next_seg6}"
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
