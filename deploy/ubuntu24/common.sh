#!/usr/bin/env bash
set -Eeuo pipefail

readonly COMMON_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
readonly REPO_ROOT="$(cd -- "${COMMON_DIR}/../.." && pwd)"
readonly APP_USER="${CITUS_APP_USER:-citus}"
readonly APP_GROUP="${CITUS_APP_GROUP:-citus}"
readonly APP_HOME="${CITUS_APP_HOME:-/opt/citus/home}"
readonly INSTALL_ROOT="${CITUS_INSTALL_ROOT:-/opt/citus}"
readonly SOURCE_DIR="${INSTALL_ROOT}/source"
readonly PUBLISH_DIR="${INSTALL_ROOT}/publish"
readonly RUNTIME_DIR="${INSTALL_ROOT}/runtime"
readonly BACKUP_DIR="${INSTALL_ROOT}/backups"
readonly DOTNET_INSTALL_DIR="${CITUS_DOTNET_INSTALL_DIR:-/opt/dotnet}"
readonly ENV_DIR="/etc/citus"
readonly ENV_FILE="${ENV_DIR}/citus.env"
readonly NGINX_SITE_PATH="/etc/nginx/sites-available/citus.conf"
readonly NGINX_ENABLED_PATH="/etc/nginx/sites-enabled/citus.conf"
readonly SYSTEMD_DIR="/etc/systemd/system"
readonly ACME_WEBROOT="${CITUS_ACME_WEBROOT:-/var/www/certbot}"
readonly CERTBOT_DEPLOY_HOOK="/etc/letsencrypt/renewal-hooks/deploy/citus-nginx-reload.sh"
readonly APT_FORCE_IPV4="${CITUS_APT_FORCE_IPV4:-1}"
readonly APT_RETRIES="${CITUS_APT_RETRIES:-5}"
readonly APT_HTTP_TIMEOUT="${CITUS_APT_HTTP_TIMEOUT:-30}"
readonly APT_PRIMARY_MIRROR="${CITUS_APT_PRIMARY_MIRROR:-http://archive.ubuntu.com/ubuntu}"
readonly APT_SECURITY_MIRROR="${CITUS_APT_SECURITY_MIRROR:-http://security.ubuntu.com/ubuntu}"
readonly VERSION_FILE="${REPO_ROOT}/VERSION"

log() {
  printf '[%s] %s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$*"
}

fail() {
  echo "ERROR: $*" >&2
  exit 1
}

trim_whitespace() {
  local value="$1"
  value="${value%$'\r'}"
  value="${value#"${value%%[![:space:]]*}"}"
  value="${value%"${value##*[![:space:]]}"}"
  printf '%s' "${value}"
}

read_repo_version() {
  [[ -f "${VERSION_FILE}" ]] || return 0

  local version
  version="$(trim_whitespace "$(head -n 1 "${VERSION_FILE}")")"
  [[ "${version}" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]] || fail "Unsupported version format in ${VERSION_FILE}: ${version}"
  printf '%s' "${version}"
}

print_usage() {
  cat <<'EOF'
Usage:
  sudo ./install.sh [options]
  sudo ./upgrade.sh [options]

Options:
  --domain NAME              Public DNS name for nginx and optional SSL.
  --server-name NAME         Alias for --domain.
  --ssl, --https             Enable Let's Encrypt HTTPS automation.
  --no-ssl, --no-https       Disable HTTPS automation.
  --http-only                Disable HTTPS automation.
  --email ADDRESS            Let's Encrypt contact email.
  --certbot-email ADDRESS    Alias for --email.
  --redirect-http            Redirect HTTP to HTTPS after a certificate exists.
  --no-redirect-http         Keep HTTP proxying enabled after SSL is active.
  --start                    Start/restart Citus services after deployment.
  --no-start                 Leave Citus services stopped after deployment.
  -h, --help                 Show this help.

Examples:
  sudo ./install.sh
  sudo ./install.sh --domain app.example.com --ssl --email ops@example.com
  sudo CITUS_SERVER_NAME=app.example.com CITUS_ENABLE_HTTPS=1 CITUS_CERTBOT_EMAIL=ops@example.com ./install.sh
EOF
}

set_cli_override() {
  local key="$1"
  local value="$2"
  export "${key}=${value}"
  export "CLI_OVERRIDE_${key}=1"
}

parse_runtime_args() {
  while [[ "$#" -gt 0 ]]; do
    case "$1" in
      --domain|--server-name)
        [[ "$#" -ge 2 ]] || fail "$1 requires a value."
        set_cli_override "CITUS_SERVER_NAME" "$2"
        shift 2
        ;;
      --ssl|--https)
        set_cli_override "CITUS_ENABLE_HTTPS" "1"
        shift
        ;;
      --no-ssl|--no-https|--http-only)
        set_cli_override "CITUS_ENABLE_HTTPS" "0"
        shift
        ;;
      --email|--certbot-email)
        [[ "$#" -ge 2 ]] || fail "$1 requires a value."
        set_cli_override "CITUS_CERTBOT_EMAIL" "$2"
        shift 2
        ;;
      --redirect-http)
        set_cli_override "CITUS_HTTPS_REDIRECT" "1"
        shift
        ;;
      --no-redirect-http)
        set_cli_override "CITUS_HTTPS_REDIRECT" "0"
        shift
        ;;
      --start)
        set_cli_override "CITUS_AUTO_START" "1"
        shift
        ;;
      --no-start)
        set_cli_override "CITUS_AUTO_START" "0"
        shift
        ;;
      -h|--help)
        print_usage
        exit 0
        ;;
      *)
        fail "Unknown option: $1. Run with --help for usage."
        ;;
    esac
  done
}

require_root() {
  [[ "${EUID}" -eq 0 ]] || fail "Run this script as root or via sudo."
}

require_ubuntu_24_04() {
  [[ -f /etc/os-release ]] || fail "Unable to detect the operating system."
  # shellcheck disable=SC1091
  . /etc/os-release
  [[ "${ID:-}" == "ubuntu" ]] || fail "This deployment script only supports Ubuntu."
  [[ "${VERSION_ID:-}" == "24.04" ]] || fail "This deployment script targets Ubuntu 24.04."
}

validate_identifier() {
  local value="$1"
  local label="$2"
  [[ "${value}" =~ ^[a-zA-Z_][a-zA-Z0-9_]*$ ]] || fail "${label} must match ^[a-zA-Z_][a-zA-Z0-9_]*$"
}

generate_secret() {
  openssl rand -hex 16
}

load_env_file() {
  [[ -f "${ENV_FILE}" ]] || fail "Missing ${ENV_FILE}. Run install.sh first."
  while IFS= read -r line || [[ -n "${line}" ]]; do
    line="${line%$'\r'}"
    [[ -z "${line}" ]] && continue
    [[ "${line:0:1}" == "#" ]] && continue
    [[ "${line}" == *=* ]] || continue
    export "${line}"
  done < "${ENV_FILE}"

  CITUS_ENABLE_HTTPS="${CITUS_ENABLE_HTTPS:-0}"
  CITUS_HTTPS_REDIRECT="${CITUS_HTTPS_REDIRECT:-1}"
  CITUS_CERTBOT_EMAIL="${CITUS_CERTBOT_EMAIL:-}"
  CITUS_AUTO_START="${CITUS_AUTO_START:-1}"
  CITUS_APP_VERSION="${CITUS_APP_VERSION:-}"

  : "${CITUS_FRONTEND_PORT:?Missing CITUS_FRONTEND_PORT}"
  : "${CITUS_ACCOUNTING_API_PORT:?Missing CITUS_ACCOUNTING_API_PORT}"
  : "${CITUS_SYSADMIN_API_PORT:?Missing CITUS_SYSADMIN_API_PORT}"
  CITUS_SYSADMIN_WEB_PORT="${CITUS_SYSADMIN_WEB_PORT:-3010}"
  : "${CITUS_FRONTEND_HOST:?Missing CITUS_FRONTEND_HOST}"
  : "${CITUS_API_HOST:?Missing CITUS_API_HOST}"
  : "${CITUS_DB_HOST:?Missing CITUS_DB_HOST}"
  : "${CITUS_DB_PORT:?Missing CITUS_DB_PORT}"
  : "${CITUS_DB_NAME:?Missing CITUS_DB_NAME}"
  : "${CITUS_DB_USER:?Missing CITUS_DB_USER}"
  : "${CITUS_DB_PASSWORD:?Missing CITUS_DB_PASSWORD}"
  : "${CITUS_ACCOUNTING_DB:?Missing CITUS_ACCOUNTING_DB}"
  : "${CITUS_SERVER_NAME:?Missing CITUS_SERVER_NAME}"

  AppHost__PublicBaseUrl="${AppHost__PublicBaseUrl:-http://${CITUS_FRONTEND_HOST}:${CITUS_FRONTEND_PORT}/}"
  AppHost__AccountingApiBaseUrl="${AppHost__AccountingApiBaseUrl:-http://${CITUS_API_HOST}:${CITUS_ACCOUNTING_API_PORT}/}"
  AppHost__SysAdminApiBaseUrl="${AppHost__SysAdminApiBaseUrl:-http://${CITUS_API_HOST}:${CITUS_SYSADMIN_API_PORT}/}"
  AppHost__BusinessAppBaseUrl="${AppHost__BusinessAppBaseUrl:-http://${CITUS_FRONTEND_HOST}:${CITUS_FRONTEND_PORT}/}"
  AppHost__PathBase="${AppHost__PathBase:-/sysadmin}"
  DOTNET_ROOT="${DOTNET_ROOT:-${DOTNET_INSTALL_DIR}}"
  DOTNET_CLI_TELEMETRY_OPTOUT="${DOTNET_CLI_TELEMETRY_OPTOUT:-1}"
  DOTNET_PRINT_TELEMETRY_MESSAGE="${DOTNET_PRINT_TELEMETRY_MESSAGE:-false}"
  export AppHost__PublicBaseUrl AppHost__AccountingApiBaseUrl AppHost__SysAdminApiBaseUrl AppHost__BusinessAppBaseUrl AppHost__PathBase DOTNET_ROOT DOTNET_CLI_TELEMETRY_OPTOUT DOTNET_PRINT_TELEMETRY_MESSAGE

  validate_identifier "${CITUS_DB_NAME}" "CITUS_DB_NAME"
  validate_identifier "${CITUS_DB_USER}" "CITUS_DB_USER"
}

append_env_if_missing() {
  local key="$1"
  local value="$2"
  grep -q "^${key}=" "${ENV_FILE}" 2>/dev/null || printf '%s=%s\n' "${key}" "${value}" >> "${ENV_FILE}"
}

set_env_value() {
  local key="$1"
  local value="$2"
  local temp_file
  temp_file="$(mktemp)"

  if [[ -f "${ENV_FILE}" ]]; then
    grep -v "^${key}=" "${ENV_FILE}" > "${temp_file}" || true
  fi

  printf '%s=%s\n' "${key}" "${value}" >> "${temp_file}"
  install -m 640 "${temp_file}" "${ENV_FILE}"
  rm -f "${temp_file}"
}

apply_cli_overrides_to_env_file() {
  local key
  for key in \
    CITUS_SERVER_NAME \
    CITUS_ENABLE_HTTPS \
    CITUS_CERTBOT_EMAIL \
    CITUS_HTTPS_REDIRECT \
    CITUS_AUTO_START; do
    local marker="CLI_OVERRIDE_${key}"
    if [[ "${!marker:-0}" == "1" ]]; then
      set_env_value "${key}" "${!key}"
    fi
  done
}

ensure_env_defaults() {
  mkdir -p "${ENV_DIR}"
  touch "${ENV_FILE}"
  append_env_if_missing "CITUS_ENABLE_HTTPS" "0"
  append_env_if_missing "CITUS_HTTPS_REDIRECT" "1"
  append_env_if_missing "CITUS_CERTBOT_EMAIL" ""
  append_env_if_missing "CITUS_AUTO_START" "1"
  append_env_if_missing "CITUS_APP_VERSION" ""
  append_env_if_missing "CITUS_SYSADMIN_WEB_PORT" "3010"
  append_env_if_missing "AppHost__SysAdminApiBaseUrl" "http://127.0.0.1:5089/"
  append_env_if_missing "AppHost__BusinessAppBaseUrl" "http://127.0.0.1:3000/"
  append_env_if_missing "AppHost__PathBase" "/sysadmin"
  append_env_if_missing "ASPNETCORE_FORWARDEDHEADERS_ENABLED" "true"
  append_env_if_missing "DOTNET_CLI_TELEMETRY_OPTOUT" "1"
  append_env_if_missing "DOTNET_PRINT_TELEMETRY_MESSAGE" "false"
  chmod 640 "${ENV_FILE}"
}

ensure_env_file() {
  mkdir -p "${ENV_DIR}"
  chmod 750 "${ENV_DIR}"

  if [[ -f "${ENV_FILE}" ]]; then
    return
  fi

  local db_password
  db_password="${CITUS_DB_PASSWORD:-$(generate_secret)}"
  local db_host="${CITUS_DB_HOST:-127.0.0.1}"
  local db_port="${CITUS_DB_PORT:-5432}"
  local db_name="${CITUS_DB_NAME:-citus_accounting}"
  local db_user="${CITUS_DB_USER:-citus_app}"
  local frontend_host="${CITUS_FRONTEND_HOST:-127.0.0.1}"
  local api_host="${CITUS_API_HOST:-127.0.0.1}"
  local frontend_port="${CITUS_FRONTEND_PORT:-3000}"
  local accounting_port="${CITUS_ACCOUNTING_API_PORT:-5088}"
  local sysadmin_port="${CITUS_SYSADMIN_API_PORT:-5089}"
  local sysadmin_web_port="${CITUS_SYSADMIN_WEB_PORT:-3010}"
  local server_name="${CITUS_SERVER_NAME:-_}"

  cat > "${ENV_FILE}" <<EOF
NODE_ENV=production
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_FORWARDEDHEADERS_ENABLED=true
DOTNET_CLI_TELEMETRY_OPTOUT=1
DOTNET_PRINT_TELEMETRY_MESSAGE=false
NEXT_TELEMETRY_DISABLED=1
CITUS_SERVER_NAME=${server_name}
CITUS_FRONTEND_HOST=${frontend_host}
CITUS_FRONTEND_PORT=${frontend_port}
CITUS_API_HOST=${api_host}
CITUS_ACCOUNTING_API_PORT=${accounting_port}
CITUS_SYSADMIN_API_PORT=${sysadmin_port}
CITUS_SYSADMIN_WEB_PORT=${sysadmin_web_port}
CITUS_DB_HOST=${db_host}
CITUS_DB_PORT=${db_port}
CITUS_DB_NAME=${db_name}
CITUS_DB_USER=${db_user}
CITUS_DB_PASSWORD=${db_password}
CITUS_ACCOUNTING_DB=Host=${db_host};Port=${db_port};Database=${db_name};Username=${db_user};Password=${db_password};Pooling=true
AppHost__PublicBaseUrl=http://${frontend_host}:${frontend_port}/
AppHost__AccountingApiBaseUrl=http://${api_host}:${accounting_port}/
AppHost__SysAdminApiBaseUrl=http://${api_host}:${sysadmin_port}/
AppHost__BusinessAppBaseUrl=http://${frontend_host}:${frontend_port}/
AppHost__PathBase=/sysadmin
EOF

  chmod 640 "${ENV_FILE}"
  ensure_env_defaults
  log "Created ${ENV_FILE}."
}

is_truthy() {
  case "${1,,}" in
    1|true|yes|y|on) return 0 ;;
    *) return 1 ;;
  esac
}

default_prompt_choice() {
  if is_truthy "${1}"; then
    printf 'Y\n'
  else
    printf 'N\n'
  fi
}

is_interactive_session() {
  [[ -t 0 && -t 1 ]]
}

prompt_yes_no() {
  local prompt="$1"
  local default_choice="${2:-Y}"
  local suffix="[Y/n]"
  local answer

  if [[ "${default_choice}" == "N" ]]; then
    suffix="[y/N]"
  fi

  while true; do
    read -r -p "${prompt} ${suffix} " answer || return 1
    answer="${answer:-${default_choice}}"

    case "${answer,,}" in
      y|yes) return 0 ;;
      n|no) return 1 ;;
    esac

    echo "Please answer yes or no." >&2
  done
}

prompt_nonempty_value() {
  local prompt="$1"
  local default_value="${2:-}"
  local answer

  while true; do
    if [[ -n "${default_value}" ]]; then
      read -r -p "${prompt} [${default_value}] " answer || return 1
      answer="${answer:-${default_value}}"
    else
      read -r -p "${prompt} " answer || return 1
    fi

    if [[ -n "${answer}" ]]; then
      printf '%s\n' "${answer}"
      return 0
    fi

    echo "A value is required." >&2
  done
}

prompt_optional_value() {
  local prompt="$1"
  local default_value="${2:-}"
  local answer

  if [[ -n "${default_value}" ]]; then
    read -r -p "${prompt} [${default_value}] " answer || return 1
    printf '%s\n' "${answer:-${default_value}}"
  else
    read -r -p "${prompt} " answer || return 1
    printf '%s\n' "${answer}"
  fi
}

looks_like_ip_address() {
  local value="$1"
  [[ "${value}" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]] || [[ "${value}" == *:* ]]
}

supports_public_tls_server_name() {
  local server_name="${1,,}"
  [[ -n "${server_name}" ]] || return 1
  [[ "${server_name}" != "_" ]] || return 1
  [[ "${server_name}" != "localhost" ]] || return 1
  [[ "${server_name}" != *" "* ]] || return 1
  [[ "${server_name}" == *.* ]] || return 1
  looks_like_ip_address "${server_name}" && return 1
  return 0
}

validate_https_configuration() {
  load_env_file
  if ! is_truthy "${CITUS_ENABLE_HTTPS}"; then
    return
  fi

  supports_public_tls_server_name "${CITUS_SERVER_NAME}" ||
    fail "CITUS_SERVER_NAME must be a public DNS name before HTTPS automation can run."
  [[ -n "${CITUS_CERTBOT_EMAIL}" ]] ||
    fail "CITUS_CERTBOT_EMAIL is required when HTTPS automation is enabled."
}

get_public_frontend_url() {
  load_env_file

  if supports_public_tls_server_name "${CITUS_SERVER_NAME}"; then
    if is_truthy "${CITUS_ENABLE_HTTPS}"; then
      printf 'https://%s/\n' "${CITUS_SERVER_NAME}"
    else
      printf 'http://%s/\n' "${CITUS_SERVER_NAME}"
    fi
    return
  fi

  local public_ip
  public_ip="$(hostname -I 2>/dev/null | awk '{print $1}')"
  printf 'http://%s/\n' "${public_ip}"
}

configure_runtime_preferences() {
  local deployment_mode="${1:-install}"

  ensure_env_defaults
  apply_cli_overrides_to_env_file
  load_env_file

  local enable_https="${CITUS_ENABLE_HTTPS}"
  local https_redirect="${CITUS_HTTPS_REDIRECT}"
  local certbot_email="${CITUS_CERTBOT_EMAIL}"
  local auto_start="${CITUS_AUTO_START}"
  local server_name="${CITUS_SERVER_NAME}"

  if is_interactive_session; then
    if [[ "${deployment_mode}" == "upgrade" ]]; then
      log "Skipping HTTPS prompts during upgrade; using saved settings from ${ENV_FILE} plus any CLI overrides."
    else
      if ! supports_public_tls_server_name "${server_name}"; then
        local prompted_server_name
        prompted_server_name="$(
          prompt_optional_value \
            "Public domain for Citus; leave blank for HTTP/IP-only deployment:" \
            ""
        )"
        if [[ -n "${prompted_server_name}" ]]; then
          server_name="${prompted_server_name}"
          set_env_value "CITUS_SERVER_NAME" "${server_name}"
        fi
      fi

      if supports_public_tls_server_name "${server_name}"; then
        local https_prompt
        if is_truthy "${enable_https}"; then
          https_prompt="Keep HTTPS enabled and request or renew a Let's Encrypt certificate for ${server_name}?"
        else
          https_prompt="Enable HTTPS and request a Let's Encrypt certificate for ${server_name}?"
        fi

        if prompt_yes_no "${https_prompt}" "$(default_prompt_choice "${enable_https}")"; then
          enable_https="1"
          certbot_email="$(prompt_nonempty_value "Let's Encrypt contact email:" "${certbot_email}")"
          if prompt_yes_no "Redirect plain HTTP traffic to HTTPS for ${server_name}?" "$(default_prompt_choice "${https_redirect}")"; then
            https_redirect="1"
          else
            https_redirect="0"
          fi
        else
          enable_https="0"
        fi
      else
        log "Skipping HTTPS certificate prompt because CITUS_SERVER_NAME=${server_name} is not a public DNS name."
        enable_https="0"
        https_redirect="1"
        certbot_email=""
      fi
    fi

    if prompt_yes_no "Start the Citus application services when deployment finishes?" "$(default_prompt_choice "${auto_start}")"; then
      auto_start="1"
    else
      auto_start="0"
    fi

    set_env_value "CITUS_ENABLE_HTTPS" "${enable_https}"
    set_env_value "CITUS_HTTPS_REDIRECT" "${https_redirect}"
    set_env_value "CITUS_CERTBOT_EMAIL" "${certbot_email}"
    set_env_value "CITUS_AUTO_START" "${auto_start}"
  fi

  set_env_value "AppHost__PublicBaseUrl" "http://${CITUS_FRONTEND_HOST}:${CITUS_FRONTEND_PORT}/"
  set_env_value "AppHost__AccountingApiBaseUrl" "http://${CITUS_API_HOST}:${CITUS_ACCOUNTING_API_PORT}/"
  append_env_if_missing "CITUS_SYSADMIN_WEB_PORT" "${CITUS_SYSADMIN_WEB_PORT:-3010}"
  set_env_value "AppHost__SysAdminApiBaseUrl" "http://${CITUS_API_HOST}:${CITUS_SYSADMIN_API_PORT}/"
  set_env_value "AppHost__BusinessAppBaseUrl" "$(get_public_frontend_url)"
  set_env_value "AppHost__PathBase" "/sysadmin"
  validate_https_configuration
}

apt_install() {
  local packages=("$@")
  DEBIAN_FRONTEND=noninteractive apt-get install -y --fix-missing "${packages[@]}"
}

configure_apt_network_resilience() {
  cat > /etc/apt/apt.conf.d/99citus-network <<EOF
Acquire::Retries "${APT_RETRIES}";
Acquire::http::Timeout "${APT_HTTP_TIMEOUT}";
Acquire::https::Timeout "${APT_HTTP_TIMEOUT}";
Acquire::ForceIPv4 "$(is_truthy "${APT_FORCE_IPV4}" && printf 'true' || printf 'false')";
EOF
}

configure_ubuntu_mirrors() {
  local ubuntu_sources="/etc/apt/sources.list.d/ubuntu.sources"
  local backup_suffix=".citus.bak"

  if [[ -f "${ubuntu_sources}" ]]; then
    if [[ ! -f "${ubuntu_sources}${backup_suffix}" ]]; then
      cp "${ubuntu_sources}" "${ubuntu_sources}${backup_suffix}"
    fi

    local rewritten_sources
    rewritten_sources="$(mktemp)"
    awk -v primary="${APT_PRIMARY_MIRROR}" -v security="${APT_SECURITY_MIRROR}" '
      BEGIN { RS = ""; ORS = ""; }
      {
        mirror = ($0 ~ /(^|\n)Suites:[^\n]*noble-security/) ? security : primary;
        line_count = split($0, lines, "\n");
        for (line_index = 1; line_index <= line_count; line_index++) {
          if (lines[line_index] ~ /^URIs: /) {
            lines[line_index] = "URIs: " mirror;
          }

          print lines[line_index];
          if (line_index < line_count) {
            print "\n";
          }
        }

        if (NR > 0) {
          print "\n\n";
        }
      }
    ' "${ubuntu_sources}" > "${rewritten_sources}"
    cat "${rewritten_sources}" > "${ubuntu_sources}"
    rm -f "${rewritten_sources}"
    return
  fi

  if [[ -f /etc/apt/sources.list ]]; then
    if [[ ! -f /etc/apt/sources.list${backup_suffix} ]]; then
      cp /etc/apt/sources.list /etc/apt/sources.list${backup_suffix}
    fi

    sed -i \
      -e "s|http://[a-zA-Z0-9.-]*/ubuntu|${APT_PRIMARY_MIRROR}|g" \
      -e "s|https://[a-zA-Z0-9.-]*/ubuntu|${APT_PRIMARY_MIRROR}|g" \
      /etc/apt/sources.list
  fi
}

apt_update_with_retry() {
  local attempt
  local max_attempts=3

  for (( attempt=1; attempt<=max_attempts; attempt++ )); do
    if apt-get update; then
      return 0
    fi

    if (( attempt == max_attempts )); then
      break
    fi

    log "apt-get update failed on attempt ${attempt}/${max_attempts}; retrying in 5 seconds."
    sleep 5
  done

  fail "apt-get update failed after ${max_attempts} attempts. Check Ubuntu mirror reachability or set CITUS_APT_PRIMARY_MIRROR."
}

ensure_base_packages() {
  log "Installing Ubuntu packages."
  configure_apt_network_resilience
  configure_ubuntu_mirrors
  apt_update_with_retry
  apt_install \
    ca-certificates \
    curl \
    git \
    rsync \
    xz-utils \
    openssl \
    snapd \
    nginx \
    postgresql \
    postgresql-contrib \
    postgresql-client \
    build-essential
  systemctl enable --now postgresql
  systemctl enable --now nginx
  systemctl enable --now snapd.socket
}

append_package_if_available() {
  local -n package_list_ref="$1"
  local package_name="$2"
  if apt-cache show "${package_name}" >/dev/null 2>&1; then
    package_list_ref+=("${package_name}")
    return 0
  fi

  return 1
}

ensure_dotnet_dependencies() {
  local packages=(
    ca-certificates
    libc6
    libgcc-s1
    libgssapi-krb5-2
    libstdc++6
    tzdata
    zlib1g
  )

  append_package_if_available packages "libicu74" ||
    append_package_if_available packages "libicu76" ||
    fail "Unable to find a supported libicu package for .NET on this Ubuntu 24.04 host."

  append_package_if_available packages "libssl3t64" ||
    append_package_if_available packages "libssl3" ||
    fail "Unable to find a supported libssl package for .NET on this Ubuntu 24.04 host."

  apt_install "${packages[@]}"
}

dotnet_sdk_version_from_global_json() {
  sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "${REPO_ROOT}/global.json" | head -n 1
}

dotnet_sdk_tarball_url() {
  local sdk_version="$1"

  if [[ -n "${CITUS_DOTNET_SDK_TARBALL_URL:-}" ]]; then
    printf '%s\n' "${CITUS_DOTNET_SDK_TARBALL_URL}"
    return 0
  fi

  [[ -n "${sdk_version}" ]] || return 0
  printf 'https://builds.dotnet.microsoft.com/dotnet/Sdk/%s/dotnet-sdk-%s-linux-x64.tar.gz\n' "${sdk_version}" "${sdk_version}"
}

dotnet_sdk_is_installed() {
  local sdk_version="$1"
  [[ -x "${DOTNET_INSTALL_DIR}/dotnet" ]] || return 1

  DOTNET_ROOT="${DOTNET_INSTALL_DIR}" \
    "${DOTNET_INSTALL_DIR}/dotnet" --list-sdks 2>/dev/null |
    grep -q "^${sdk_version}[[:space:]]"
}

install_dotnet_sdk_from_tarball() {
  local sdk_version="$1"
  local tarball_url="$2"
  local archive="/tmp/citus-dotnet-sdk-${sdk_version}.tar.gz"

  log "Installing .NET SDK ${sdk_version} from tarball fallback."
  curl -fL "${tarball_url}" -o "${archive}"
  tar -xzf "${archive}" -C "${DOTNET_INSTALL_DIR}"
  rm -f "${archive}"
}

ensure_dotnet() {
  ensure_dotnet_dependencies

  local sdk_version="${CITUS_DOTNET_SDK_VERSION:-$(dotnet_sdk_version_from_global_json)}"
  local dotnet_channel="${CITUS_DOTNET_CHANNEL:-11.0}"
  local dotnet_quality="${CITUS_DOTNET_QUALITY:-preview}"
  local allow_channel_fallback="${CITUS_DOTNET_ALLOW_CHANNEL_FALLBACK:-1}"
  local install_script="/tmp/citus-dotnet-install.sh"
  local tarball_url=""

  log "Installing .NET SDK into ${DOTNET_INSTALL_DIR}."
  mkdir -p "${DOTNET_INSTALL_DIR}"
  curl -fsSL "https://dot.net/v1/dotnet-install.sh" -o "${install_script}"
  chmod 755 "${install_script}"

  if [[ -n "${sdk_version}" ]]; then
    if ! "${install_script}" --version "${sdk_version}" --install-dir "${DOTNET_INSTALL_DIR}" --no-path; then
      tarball_url="$(dotnet_sdk_tarball_url "${sdk_version}")"

      if [[ -n "${tarball_url}" ]]; then
        log "Exact SDK ${sdk_version} was not installed through dotnet-install.sh; trying tarball fallback ${tarball_url}."
        if ! install_dotnet_sdk_from_tarball "${sdk_version}" "${tarball_url}"; then
          if is_truthy "${allow_channel_fallback}"; then
            log "Tarball fallback for ${sdk_version} failed; falling back to channel ${dotnet_channel} (${dotnet_quality})."
            "${install_script}" --channel "${dotnet_channel}" --quality "${dotnet_quality}" --install-dir "${DOTNET_INSTALL_DIR}" --no-path
          else
            fail "Unable to install .NET SDK ${sdk_version} through dotnet-install.sh or tarball fallback."
          fi
        fi
      elif is_truthy "${allow_channel_fallback}"; then
        log "Exact SDK ${sdk_version} was not installed; falling back to channel ${dotnet_channel} (${dotnet_quality})."
        "${install_script}" --channel "${dotnet_channel}" --quality "${dotnet_quality}" --install-dir "${DOTNET_INSTALL_DIR}" --no-path
      else
        fail "Unable to install .NET SDK ${sdk_version}."
      fi
    fi
  else
    "${install_script}" --channel "${dotnet_channel}" --quality "${dotnet_quality}" --install-dir "${DOTNET_INSTALL_DIR}" --no-path
  fi

  ln -sfn "${DOTNET_INSTALL_DIR}/dotnet" /usr/local/bin/dotnet
  DOTNET_ROOT="${DOTNET_INSTALL_DIR}" /usr/local/bin/dotnet --info >/dev/null

  if [[ -n "${sdk_version}" ]] && ! dotnet_sdk_is_installed "${sdk_version}"; then
    if is_truthy "${allow_channel_fallback}"; then
      log "Exact SDK ${sdk_version} is still not present after fallback. Continuing with the installed SDK because global.json allows roll-forward."
    else
      fail "Expected .NET SDK ${sdk_version} was not installed into ${DOTNET_INSTALL_DIR}."
    fi
  fi
}

ensure_certbot() {
  log "Installing Certbot through snap."
  apt_install snapd
  systemctl enable --now snapd.socket
  systemctl start snapd.service 2>/dev/null || true
  snap wait system seed.loaded 2>/dev/null || true

  if dpkg-query -W -f='${Status}' certbot 2>/dev/null | grep -q "install ok installed"; then
    DEBIAN_FRONTEND=noninteractive apt-get remove -y certbot python3-certbot python3-certbot-nginx python3-acme || true
  fi

  if ! snap list certbot >/dev/null 2>&1; then
    snap install --classic certbot
  fi

  ln -sfn /snap/bin/certbot /usr/local/bin/certbot
}

ensure_node() {
  local required_major="${CITUS_NODE_MAJOR:-20}"
  local current_major=""

  if command -v node >/dev/null 2>&1; then
    current_major="$(node --version 2>/dev/null | sed -E 's/^v([0-9]+).*/\1/')"
  fi

  if [[ -z "${current_major}" || "${current_major}" -lt "${required_major}" ]]; then
    log "Installing Node.js ${required_major} (current: ${current_major:-none})."
    curl -fsSL "https://deb.nodesource.com/setup_${required_major}.x" | bash -
    apt_install nodejs
  fi

  if ! command -v pnpm >/dev/null 2>&1; then
    log "Installing pnpm via npm."
    npm install -g pnpm@9.12.0
  fi

  log "Node $(node --version), pnpm $(pnpm --version) ready."
}

ensure_app_user() {
  if ! getent group "${APP_GROUP}" >/dev/null 2>&1; then
    groupadd --system "${APP_GROUP}"
  fi

  if ! id -u "${APP_USER}" >/dev/null 2>&1; then
    useradd --system --gid "${APP_GROUP}" --create-home --home-dir "${APP_HOME}" --shell /bin/bash "${APP_USER}"
  else
    mkdir -p "${APP_HOME}"
  fi
}

ensure_layout() {
  mkdir -p "${SOURCE_DIR}" "${PUBLISH_DIR}" "${RUNTIME_DIR}" "${BACKUP_DIR}"
  chown -R "${APP_USER}:${APP_GROUP}" "${INSTALL_ROOT}"
}

sync_source_tree() {
  log "Syncing the repository into ${SOURCE_DIR}."
  rsync -a --delete \
    --exclude '.git/' \
    --exclude '.next/' \
    --exclude 'node_modules/' \
    --exclude 'bin/' \
    --exclude 'obj/' \
    --exclude '.env' \
    --exclude '.env.local' \
    --exclude '.env.production' \
    "${REPO_ROOT}/" "${SOURCE_DIR}/"
  chown -R "${APP_USER}:${APP_GROUP}" "${SOURCE_DIR}"
}

ensure_postgres_database() {
  load_env_file

  log "Ensuring PostgreSQL role and database exist."
  sudo -u postgres psql postgres \
    -v ON_ERROR_STOP=1 \
    --set=citus_db_user="${CITUS_DB_USER}" \
    --set=citus_db_password="${CITUS_DB_PASSWORD}" <<'SQL'
SELECT CASE
  WHEN EXISTS (SELECT 1 FROM pg_roles WHERE rolname = :'citus_db_user')
    THEN format('ALTER ROLE %I WITH LOGIN PASSWORD %L', :'citus_db_user', :'citus_db_password')
  ELSE format('CREATE ROLE %I LOGIN PASSWORD %L', :'citus_db_user', :'citus_db_password')
END
\gexec
SQL

  if ! sudo -u postgres psql -tAc "SELECT 1 FROM pg_database WHERE datname = '${CITUS_DB_NAME}'" | grep -q 1; then
    sudo -u postgres createdb --owner="${CITUS_DB_USER}" "${CITUS_DB_NAME}"
  fi
}

apply_backend_baseline_if_needed() {
  load_env_file

  local users_table_exists
  users_table_exists="$(
    PGPASSWORD="${CITUS_DB_PASSWORD}" \
      psql \
        -h "${CITUS_DB_HOST}" \
        -p "${CITUS_DB_PORT}" \
        -U "${CITUS_DB_USER}" \
        -d "${CITUS_DB_NAME}" \
        -tAc "SELECT EXISTS (SELECT 1 FROM information_schema.tables WHERE table_schema = 'public' AND table_name = 'users');"
  )"

  if [[ "${users_table_exists//[[:space:]]/}" == "t" ]]; then
    log "Backend PostgreSQL baseline already exists; skipping ${REPO_ROOT}/CITUS_POSTGRESQL_MIGRATION_DRAFT.sql."
    return
  fi

  local public_table_count
  public_table_count="$(
    PGPASSWORD="${CITUS_DB_PASSWORD}" \
      psql \
        -h "${CITUS_DB_HOST}" \
        -p "${CITUS_DB_PORT}" \
        -U "${CITUS_DB_USER}" \
        -d "${CITUS_DB_NAME}" \
        -tAc "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = 'public';"
  )"

  if [[ "${public_table_count//[[:space:]]/}" != "0" ]]; then
    fail "The PostgreSQL database is not empty but the baseline sentinel table 'users' is missing. Refusing to auto-apply ${SOURCE_DIR}/CITUS_POSTGRESQL_MIGRATION_DRAFT.sql."
  fi

  log "Applying backend PostgreSQL baseline draft."
  PGPASSWORD="${CITUS_DB_PASSWORD}" \
    psql \
      -v ON_ERROR_STOP=1 \
      -h "${CITUS_DB_HOST}" \
      -p "${CITUS_DB_PORT}" \
      -U "${CITUS_DB_USER}" \
      -d "${CITUS_DB_NAME}" \
      -f "${SOURCE_DIR}/CITUS_POSTGRESQL_MIGRATION_DRAFT.sql"
}

publish_backends() {
  load_env_file

  log "Building Tailwind CSS bundles for the Blazor shells."
  (
    cd "${SOURCE_DIR}/backend/frontend"
    PNPM_HOME="${HOME}/.local/share/pnpm" PATH="${PNPM_HOME}:${PATH}" pnpm install --frozen-lockfile
    PNPM_HOME="${HOME}/.local/share/pnpm" PATH="${PNPM_HOME}:${PATH}" pnpm run css:build
  )

  log "Publishing .NET services and the Blazor shells."
  # web-shell directory remains for backwards-compat with operators upgrading
  # from the old Web.Shell layout — it now hosts Citus.Business.Blazor.
  rm -rf \
    "${PUBLISH_DIR}/business-web" \
    "${PUBLISH_DIR}/web-shell" \
    "${PUBLISH_DIR}/accounting-api" \
    "${PUBLISH_DIR}/sysadmin-api" \
    "${PUBLISH_DIR}/sysadmin-web" \
    "${PUBLISH_DIR}/consoleapp"

  mkdir -p \
    "${PUBLISH_DIR}/business-web" \
    "${PUBLISH_DIR}/accounting-api" \
    "${PUBLISH_DIR}/sysadmin-api" \
    "${PUBLISH_DIR}/sysadmin-web" \
    "${PUBLISH_DIR}/consoleapp"

  DOTNET_ROOT="${DOTNET_INSTALL_DIR}" /usr/local/bin/dotnet publish "${SOURCE_DIR}/backend/src/Citus.Business.Blazor/Citus.Business.Blazor.csproj" -c Release -o "${PUBLISH_DIR}/business-web" -p:SkipCssBuild=true
  DOTNET_ROOT="${DOTNET_INSTALL_DIR}" /usr/local/bin/dotnet publish "${SOURCE_DIR}/backend/src/Citus.Accounting.Api/Citus.Accounting.Api.csproj" -c Release -o "${PUBLISH_DIR}/accounting-api"
  DOTNET_ROOT="${DOTNET_INSTALL_DIR}" /usr/local/bin/dotnet publish "${SOURCE_DIR}/backend/src/Citus.SysAdmin.Api/Citus.SysAdmin.Api.csproj" -c Release -o "${PUBLISH_DIR}/sysadmin-api"
  DOTNET_ROOT="${DOTNET_INSTALL_DIR}" /usr/local/bin/dotnet publish "${SOURCE_DIR}/backend/src/Citus.SysAdmin.Blazor/Citus.SysAdmin.Blazor.csproj" -c Release -o "${PUBLISH_DIR}/sysadmin-web" -p:SkipCssBuild=true
  DOTNET_ROOT="${DOTNET_INSTALL_DIR}" /usr/local/bin/dotnet publish "${SOURCE_DIR}/backend/src/Citus.ConsoleApp/Citus.ConsoleApp.csproj" -c Release -o "${PUBLISH_DIR}/consoleapp"

  chown -R "${APP_USER}:${APP_GROUP}" "${PUBLISH_DIR}"
}

bootstrap_platform_core() {
  load_env_file
  log "Bootstrapping the platform metadata registry."
  (
    cd "${PUBLISH_DIR}/consoleapp"
    export CITUS_ACCOUNTING_DB
    DOTNET_ROOT="${DOTNET_INSTALL_DIR}" /usr/local/bin/dotnet ./Citus.ConsoleApp.dll bootstrap-core
  )
}

write_systemd_units() {
  load_env_file
  log "Writing systemd service units."

  cat > "${SYSTEMD_DIR}/citus-web.service" <<EOF
[Unit]
Description=Citus Business Blazor frontend
After=network.target citus-accounting-api.service
Wants=network.target
Wants=citus-accounting-api.service

[Service]
Type=simple
User=${APP_USER}
Group=${APP_GROUP}
WorkingDirectory=${PUBLISH_DIR}/business-web
EnvironmentFile=${ENV_FILE}
Environment=PATH=/usr/local/bin:/usr/bin:/bin
Environment=DOTNET_ROOT=${DOTNET_INSTALL_DIR}
Environment=AppHost__PublicBaseUrl=http://${CITUS_FRONTEND_HOST}:${CITUS_FRONTEND_PORT}/
Environment=AppHost__AccountingApiBaseUrl=http://${CITUS_API_HOST}:${CITUS_ACCOUNTING_API_PORT}/
ExecStart=/usr/local/bin/dotnet ${PUBLISH_DIR}/business-web/Citus.Business.Blazor.dll --urls http://${CITUS_FRONTEND_HOST}:${CITUS_FRONTEND_PORT}
Restart=always
RestartSec=5
SyslogIdentifier=citus-web

[Install]
WantedBy=multi-user.target
EOF

  cat > "${SYSTEMD_DIR}/citus-sysadmin-web.service" <<EOF
[Unit]
Description=Citus SysAdmin Blazor frontend
After=network.target citus-sysadmin-api.service
Wants=network.target
Wants=citus-sysadmin-api.service

[Service]
Type=simple
User=${APP_USER}
Group=${APP_GROUP}
WorkingDirectory=${PUBLISH_DIR}/sysadmin-web
EnvironmentFile=${ENV_FILE}
Environment=PATH=/usr/local/bin:/usr/bin:/bin
Environment=DOTNET_ROOT=${DOTNET_INSTALL_DIR}
Environment=AppHost__PathBase=/sysadmin
Environment=AppHost__SysAdminApiBaseUrl=http://${CITUS_API_HOST}:${CITUS_SYSADMIN_API_PORT}/
Environment=AppHost__AccountingApiBaseUrl=http://${CITUS_API_HOST}:${CITUS_ACCOUNTING_API_PORT}/
Environment=AppHost__BusinessAppBaseUrl=${AppHost__BusinessAppBaseUrl}
ExecStart=/usr/local/bin/dotnet ${PUBLISH_DIR}/sysadmin-web/Citus.SysAdmin.Blazor.dll --urls http://${CITUS_FRONTEND_HOST}:${CITUS_SYSADMIN_WEB_PORT}
Restart=always
RestartSec=5
SyslogIdentifier=citus-sysadmin-web

[Install]
WantedBy=multi-user.target
EOF

  cat > "${SYSTEMD_DIR}/citus-accounting-api.service" <<EOF
[Unit]
Description=Citus Accounting API
After=network.target postgresql.service
Wants=network.target
Requires=postgresql.service

[Service]
Type=simple
User=${APP_USER}
Group=${APP_GROUP}
WorkingDirectory=${PUBLISH_DIR}/accounting-api
EnvironmentFile=${ENV_FILE}
Environment=PATH=/usr/local/bin:/usr/bin:/bin
Environment=DOTNET_ROOT=${DOTNET_INSTALL_DIR}
ExecStart=/usr/local/bin/dotnet ${PUBLISH_DIR}/accounting-api/Citus.Accounting.Api.dll --urls http://${CITUS_API_HOST}:${CITUS_ACCOUNTING_API_PORT}
Restart=always
RestartSec=5
SyslogIdentifier=citus-accounting-api

[Install]
WantedBy=multi-user.target
EOF

  cat > "${SYSTEMD_DIR}/citus-sysadmin-api.service" <<EOF
[Unit]
Description=Citus SysAdmin API
After=network.target postgresql.service
Wants=network.target
Requires=postgresql.service

[Service]
Type=simple
User=${APP_USER}
Group=${APP_GROUP}
WorkingDirectory=${PUBLISH_DIR}/sysadmin-api
EnvironmentFile=${ENV_FILE}
Environment=PATH=/usr/local/bin:/usr/bin:/bin
Environment=DOTNET_ROOT=${DOTNET_INSTALL_DIR}
ExecStart=/usr/local/bin/dotnet ${PUBLISH_DIR}/sysadmin-api/Citus.SysAdmin.Api.dll --urls http://${CITUS_API_HOST}:${CITUS_SYSADMIN_API_PORT}
Restart=always
RestartSec=5
SyslogIdentifier=citus-sysadmin-api

[Install]
WantedBy=multi-user.target
EOF
}

ensure_acme_webroot() {
  mkdir -p "${ACME_WEBROOT}/.well-known/acme-challenge"
  chown -R www-data:www-data "${ACME_WEBROOT}"
  chmod -R 755 "${ACME_WEBROOT}"
}

write_certbot_deploy_hook() {
  mkdir -p "$(dirname "${CERTBOT_DEPLOY_HOOK}")"
  cat > "${CERTBOT_DEPLOY_HOOK}" <<'EOF'
#!/usr/bin/env bash
set -Eeuo pipefail
systemctl reload nginx
EOF
  chmod 755 "${CERTBOT_DEPLOY_HOOK}"
}

certificate_live_dir() {
  load_env_file
  printf '/etc/letsencrypt/live/%s\n' "${CITUS_SERVER_NAME}"
}

certificate_files_exist() {
  local live_dir
  live_dir="$(certificate_live_dir)"
  [[ -f "${live_dir}/fullchain.pem" && -f "${live_dir}/privkey.pem" ]]
}

write_proxy_locations() {
  local target_file="$1"
  cat >> "${target_file}" <<EOF
  proxy_http_version 1.1;
  proxy_set_header Host \$host;
  proxy_set_header X-Real-IP \$remote_addr;
  proxy_set_header X-Forwarded-For \$proxy_add_x_forwarded_for;
  proxy_set_header X-Forwarded-Proto \$scheme;
  proxy_set_header Upgrade \$http_upgrade;
  proxy_set_header Connection \$connection_upgrade;
  proxy_read_timeout 300s;
  proxy_send_timeout 300s;
  proxy_cache_bypass \$http_upgrade;

  location = /health/accounting {
    proxy_pass http://${CITUS_API_HOST}:${CITUS_ACCOUNTING_API_PORT}/health;
  }

  location = /health/sysadmin {
    proxy_pass http://${CITUS_API_HOST}:${CITUS_SYSADMIN_API_PORT}/health;
  }

  location = /health/sysadmin-web {
    proxy_pass http://${CITUS_FRONTEND_HOST}:${CITUS_SYSADMIN_WEB_PORT}/sysadmin/system/health;
  }

  location = /sysadmin {
    return 302 /sysadmin/;
  }

  location /sysadmin/ {
    proxy_pass http://${CITUS_FRONTEND_HOST}:${CITUS_SYSADMIN_WEB_PORT};
  }

  location = /core {
    proxy_pass http://${CITUS_API_HOST}:${CITUS_SYSADMIN_API_PORT}/core;
  }

  location /core/ {
    proxy_pass http://${CITUS_API_HOST}:${CITUS_SYSADMIN_API_PORT};
  }

  location = /accounting {
    proxy_pass http://${CITUS_API_HOST}:${CITUS_ACCOUNTING_API_PORT}/accounting;
  }

  location /accounting/ {
    proxy_pass http://${CITUS_API_HOST}:${CITUS_ACCOUNTING_API_PORT};
  }

  location / {
    proxy_pass http://${CITUS_FRONTEND_HOST}:${CITUS_FRONTEND_PORT};
  }
EOF
}

write_nginx_config() {
  local mode="${1:-auto}"
  load_env_file
  log "Writing nginx reverse-proxy configuration."

  local tls_active="0"
  local certificate_dir=""
  if [[ "${mode}" != "http-only" ]] && is_truthy "${CITUS_ENABLE_HTTPS}" && certificate_files_exist; then
    tls_active="1"
    certificate_dir="$(certificate_live_dir)"
  fi

  cat > "${NGINX_SITE_PATH}" <<EOF
map \$http_upgrade \$connection_upgrade {
  default upgrade;
  '' close;
}
EOF

  if [[ "${tls_active}" == "1" ]]; then
    cat >> "${NGINX_SITE_PATH}" <<EOF

server {
  listen 80;
  listen [::]:80;
  server_name ${CITUS_SERVER_NAME};
  client_max_body_size 25m;

  location ^~ /.well-known/acme-challenge/ {
    root ${ACME_WEBROOT};
    default_type "text/plain";
    try_files \$uri =404;
  }
EOF

    if is_truthy "${CITUS_HTTPS_REDIRECT}"; then
      cat >> "${NGINX_SITE_PATH}" <<'EOF'
  location / {
    return 301 https://$host$request_uri;
  }
}
EOF
    else
      write_proxy_locations "${NGINX_SITE_PATH}"
      cat >> "${NGINX_SITE_PATH}" <<'EOF'
}
EOF
    fi

    cat >> "${NGINX_SITE_PATH}" <<EOF

server {
  listen 443 ssl http2;
  listen [::]:443 ssl http2;
  server_name ${CITUS_SERVER_NAME};
  client_max_body_size 25m;

  ssl_certificate ${certificate_dir}/fullchain.pem;
  ssl_certificate_key ${certificate_dir}/privkey.pem;

EOF
    write_proxy_locations "${NGINX_SITE_PATH}"
    cat >> "${NGINX_SITE_PATH}" <<'EOF'
}
EOF
  else
    cat >> "${NGINX_SITE_PATH}" <<EOF

server {
  listen 80;
  listen [::]:80;
  server_name ${CITUS_SERVER_NAME};
  client_max_body_size 25m;

  location ^~ /.well-known/acme-challenge/ {
    root ${ACME_WEBROOT};
    default_type "text/plain";
    try_files \$uri =404;
  }
EOF
    write_proxy_locations "${NGINX_SITE_PATH}"
    cat >> "${NGINX_SITE_PATH}" <<'EOF'
}
EOF
  fi

  ln -sfn "${NGINX_SITE_PATH}" "${NGINX_ENABLED_PATH}"
  rm -f /etc/nginx/sites-enabled/default
  nginx -t
}

reload_systemd_units() {
  log "Reloading systemd service definitions."
  systemctl daemon-reload
  systemctl enable citus-web.service citus-accounting-api.service citus-sysadmin-api.service citus-sysadmin-web.service
}

restart_nginx_service() {
  log "Restarting nginx."
  systemctl restart nginx
}

restart_application_services() {
  log "Restarting application services."
  systemctl restart citus-accounting-api.service
  systemctl restart citus-sysadmin-api.service
  systemctl restart citus-sysadmin-web.service
  systemctl restart citus-web.service
}

stop_application_services() {
  log "Stopping current application services."
  systemctl stop citus-web.service 2>/dev/null || true
  systemctl stop citus-sysadmin-web.service 2>/dev/null || true
  systemctl stop citus-accounting-api.service 2>/dev/null || true
  systemctl stop citus-sysadmin-api.service 2>/dev/null || true
}

wait_for_http() {
  local label="$1"
  local url="$2"
  local attempts="${3:-30}"

  local attempt
  for ((attempt = 1; attempt <= attempts; attempt++)); do
    if curl --silent --show-error --fail --max-time 5 "${url}" >/dev/null 2>&1; then
      log "${label} is healthy at ${url}."
      return 0
    fi
    sleep 2
  done

  fail "${label} did not become healthy at ${url}. Check journalctl -u ${label// /-}."
}

verify_runtime_health() {
  load_env_file
  wait_for_http "citus-accounting-api" "http://${CITUS_API_HOST}:${CITUS_ACCOUNTING_API_PORT}/health"
  wait_for_http "citus-sysadmin-api" "http://${CITUS_API_HOST}:${CITUS_SYSADMIN_API_PORT}/health"
  wait_for_http "citus-sysadmin-web" "http://${CITUS_FRONTEND_HOST}:${CITUS_SYSADMIN_WEB_PORT}/sysadmin/system/health"
  wait_for_http "citus-web" "http://${CITUS_FRONTEND_HOST}:${CITUS_FRONTEND_PORT}/system/health"
}

obtain_or_renew_tls_certificate() {
  load_env_file

  if ! is_truthy "${CITUS_ENABLE_HTTPS}"; then
    log "HTTPS automation disabled; nginx will remain on HTTP."
    return
  fi

  validate_https_configuration
  ensure_certbot
  ensure_acme_webroot
  write_certbot_deploy_hook
  write_nginx_config "http-only"
  restart_nginx_service

  log "Requesting or renewing the Let's Encrypt certificate for ${CITUS_SERVER_NAME}."
  certbot certonly \
    --webroot \
    -w "${ACME_WEBROOT}" \
    -d "${CITUS_SERVER_NAME}" \
    --email "${CITUS_CERTBOT_EMAIL}" \
    --agree-tos \
    --no-eff-email \
    --non-interactive \
    --keep-until-expiring
}

backup_datastores() {
  load_env_file
  local timestamp
  timestamp="$(date -u +%Y%m%dT%H%M%SZ)"
  mkdir -p "${BACKUP_DIR}"

  if command -v pg_dump >/dev/null 2>&1; then
    PGPASSWORD="${CITUS_DB_PASSWORD}" \
      pg_dump \
        -h "${CITUS_DB_HOST}" \
        -p "${CITUS_DB_PORT}" \
        -U "${CITUS_DB_USER}" \
        -Fc \
        -f "${BACKUP_DIR}/accounting-${timestamp}.dump" \
        "${CITUS_DB_NAME}"
    log "Backed up the accounting PostgreSQL database."
  fi
}

print_install_summary() {
  load_env_file
  local frontend_url
  frontend_url="$(get_public_frontend_url)"

  cat <<EOF

Deployment complete.

Citus.Business.Blazor:
  ${frontend_url}

SysAdmin:
  ${frontend_url%/}/sysadmin/

Reverse-proxied endpoints:
  /accounting -> accounting API
  /core       -> sysadmin API
  /sysadmin   -> sysadmin setup/admin UI
  /health/accounting
  /health/sysadmin
  /health/sysadmin-web

Local service ports:
  Frontend:      http://${CITUS_FRONTEND_HOST}:${CITUS_FRONTEND_PORT}
  SysAdmin UI:   http://${CITUS_FRONTEND_HOST}:${CITUS_SYSADMIN_WEB_PORT}/sysadmin
  Accounting:    http://${CITUS_API_HOST}:${CITUS_ACCOUNTING_API_PORT}
  SysAdmin:      http://${CITUS_API_HOST}:${CITUS_SYSADMIN_API_PORT}

Runtime files:
  Source:        ${SOURCE_DIR}
  Publish:       ${PUBLISH_DIR}
  Env file:      ${ENV_FILE}
  .NET root:     ${DOTNET_INSTALL_DIR}
  ACME webroot:  ${ACME_WEBROOT}

Important note:
  The backend PostgreSQL draft baseline is only applied automatically to an empty database.
  Citus.Business.Blazor currently uses CompanyAccess/bootstrap shell context; production identity hardening is still pending.
  Citus services auto-start: ${CITUS_AUTO_START}
  HTTPS enabled:             ${CITUS_ENABLE_HTTPS}
  Deployed version:          ${CITUS_APP_VERSION:-unknown}
EOF
}

persist_repo_version_to_env() {
  local repo_version
  repo_version="$(read_repo_version)"
  [[ -n "${repo_version}" ]] || return 0

  set_env_value "CITUS_APP_VERSION" "${repo_version}"
  export CITUS_APP_VERSION="${repo_version}"
}

install_main() {
  parse_runtime_args "$@"
  require_root
  require_ubuntu_24_04
  ensure_base_packages
  ensure_dotnet
  ensure_node
  ensure_app_user
  ensure_layout
  ensure_env_file
  ensure_env_defaults
  configure_runtime_preferences "install"
  load_env_file
  stop_application_services
  sync_source_tree
  ensure_postgres_database
  apply_backend_baseline_if_needed
  publish_backends
  bootstrap_platform_core
  write_systemd_units
  reload_systemd_units
  obtain_or_renew_tls_certificate
  write_nginx_config
  restart_nginx_service
  if is_truthy "${CITUS_AUTO_START}"; then
    restart_application_services
    verify_runtime_health
  else
    log "Application services were left stopped by operator choice."
  fi
  persist_repo_version_to_env
  print_install_summary
}

upgrade_main() {
  parse_runtime_args "$@"
  require_root
  require_ubuntu_24_04
  [[ -f "${ENV_FILE}" ]] || fail "Missing ${ENV_FILE}. Run install.sh before upgrade.sh."
  ensure_base_packages
  ensure_dotnet
  ensure_node
  ensure_app_user
  ensure_layout
  ensure_env_defaults
  configure_runtime_preferences "upgrade"
  load_env_file

  local repo_version installed_version
  repo_version="$(read_repo_version)"
  installed_version="$(trim_whitespace "${CITUS_APP_VERSION:-}")"

  log "Upgrade version check:"
  log "  Current installed version: ${installed_version:-unknown}"
  log "  Target repository version: ${repo_version:-unknown}"

  if [[ -n "${repo_version}" && -n "${installed_version}" && "${repo_version}" == "${installed_version}" ]]; then
    log "Current installed version already matches the target repository version. Skipping upgrade."
    return 0
  fi

  if [[ -n "${repo_version}" ]]; then
    log "Proceeding with upgrade: ${installed_version:-unknown} -> ${repo_version}"
  else
    log "Proceeding with upgrade using the repository working tree because no target version file was found."
  fi

  stop_application_services
  backup_datastores
  sync_source_tree
  ensure_postgres_database
  apply_backend_baseline_if_needed
  publish_backends
  bootstrap_platform_core
  write_systemd_units
  reload_systemd_units
  obtain_or_renew_tls_certificate
  write_nginx_config
  restart_nginx_service
  if is_truthy "${CITUS_AUTO_START}"; then
    restart_application_services
    verify_runtime_health
  else
    log "Application services were left stopped by operator choice."
  fi
  persist_repo_version_to_env
  log "Upgrade complete."
}
