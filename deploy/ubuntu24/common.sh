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
readonly ENV_DIR="/etc/citus"
readonly ENV_FILE="${ENV_DIR}/citus.env"
readonly NGINX_SITE_PATH="/etc/nginx/sites-available/citus.conf"
readonly NGINX_ENABLED_PATH="/etc/nginx/sites-enabled/citus.conf"
readonly SYSTEMD_DIR="/etc/systemd/system"
readonly ACME_WEBROOT="${CITUS_ACME_WEBROOT:-/var/www/certbot}"
readonly CERTBOT_DEPLOY_HOOK="/etc/letsencrypt/renewal-hooks/deploy/citus-nginx-reload.sh"

log() {
  printf '[%s] %s\n' "$(date -u +%Y-%m-%dT%H:%M:%SZ)" "$*"
}

fail() {
  echo "ERROR: $*" >&2
  exit 1
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

  : "${CITUS_FRONTEND_PORT:?Missing CITUS_FRONTEND_PORT}"
  : "${CITUS_ACCOUNTING_API_PORT:?Missing CITUS_ACCOUNTING_API_PORT}"
  : "${CITUS_SYSADMIN_API_PORT:?Missing CITUS_SYSADMIN_API_PORT}"
  : "${CITUS_FRONTEND_HOST:?Missing CITUS_FRONTEND_HOST}"
  : "${CITUS_API_HOST:?Missing CITUS_API_HOST}"
  : "${CITUS_DB_HOST:?Missing CITUS_DB_HOST}"
  : "${CITUS_DB_PORT:?Missing CITUS_DB_PORT}"
  : "${CITUS_DB_NAME:?Missing CITUS_DB_NAME}"
  : "${CITUS_DB_USER:?Missing CITUS_DB_USER}"
  : "${CITUS_DB_PASSWORD:?Missing CITUS_DB_PASSWORD}"
  : "${DATABASE_URL:?Missing DATABASE_URL}"
  : "${CITUS_ACCOUNTING_DB:?Missing CITUS_ACCOUNTING_DB}"
  : "${CITUS_SERVER_NAME:?Missing CITUS_SERVER_NAME}"

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

ensure_env_defaults() {
  mkdir -p "${ENV_DIR}"
  touch "${ENV_FILE}"
  append_env_if_missing "CITUS_ENABLE_HTTPS" "0"
  append_env_if_missing "CITUS_HTTPS_REDIRECT" "1"
  append_env_if_missing "CITUS_CERTBOT_EMAIL" ""
  append_env_if_missing "CITUS_AUTO_START" "1"
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
  local server_name="${CITUS_SERVER_NAME:-_}"
  local sqlite_path="${RUNTIME_DIR}/frontend/citus.sqlite"

  cat > "${ENV_FILE}" <<EOF
NODE_ENV=production
ASPNETCORE_ENVIRONMENT=Production
NEXT_TELEMETRY_DISABLED=1
CITUS_SERVER_NAME=${server_name}
CITUS_FRONTEND_HOST=${frontend_host}
CITUS_FRONTEND_PORT=${frontend_port}
CITUS_API_HOST=${api_host}
CITUS_ACCOUNTING_API_PORT=${accounting_port}
CITUS_SYSADMIN_API_PORT=${sysadmin_port}
CITUS_DB_HOST=${db_host}
CITUS_DB_PORT=${db_port}
CITUS_DB_NAME=${db_name}
CITUS_DB_USER=${db_user}
CITUS_DB_PASSWORD=${db_password}
DATABASE_URL=file:${sqlite_path}
CITUS_ACCOUNTING_DB=Host=${db_host};Port=${db_port};Database=${db_name};Username=${db_user};Password=${db_password};Pooling=true
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

configure_runtime_preferences() {
  ensure_env_defaults
  load_env_file

  local enable_https="${CITUS_ENABLE_HTTPS}"
  local https_redirect="${CITUS_HTTPS_REDIRECT}"
  local certbot_email="${CITUS_CERTBOT_EMAIL}"
  local auto_start="${CITUS_AUTO_START}"

  if is_interactive_session; then
    if supports_public_tls_server_name "${CITUS_SERVER_NAME}"; then
      local https_prompt
      if is_truthy "${enable_https}"; then
        https_prompt="Keep HTTPS enabled and request or renew a Let's Encrypt certificate for ${CITUS_SERVER_NAME}?"
      else
        https_prompt="Enable HTTPS and request a Let's Encrypt certificate for ${CITUS_SERVER_NAME}?"
      fi

      if prompt_yes_no "${https_prompt}" "$(default_prompt_choice "${enable_https}")"; then
        enable_https="1"
        certbot_email="$(prompt_nonempty_value "Let's Encrypt contact email:" "${certbot_email}")"
        if prompt_yes_no "Redirect plain HTTP traffic to HTTPS for ${CITUS_SERVER_NAME}?" "$(default_prompt_choice "${https_redirect}")"; then
          https_redirect="1"
        else
          https_redirect="0"
        fi
      else
        enable_https="0"
      fi
    else
      log "Skipping HTTPS certificate prompt because CITUS_SERVER_NAME=${CITUS_SERVER_NAME} is not a public DNS name."
      enable_https="0"
      https_redirect="1"
      certbot_email=""
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

  validate_https_configuration
}

apt_install() {
  local packages=("$@")
  DEBIAN_FRONTEND=noninteractive apt-get install -y "${packages[@]}"
}

ensure_base_packages() {
  log "Installing Ubuntu packages."
  apt-get update
  apt_install \
    ca-certificates \
    curl \
    git \
    rsync \
    xz-utils \
    sqlite3 \
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

ensure_dotnet() {
  log "Installing .NET 11 from the Ubuntu 24.04 package feeds."
  apt_install dotnet-sdk-11.0 aspnetcore-runtime-11.0
}

detect_node_arch() {
  case "$(dpkg --print-architecture)" in
    amd64) echo "x64" ;;
    arm64) echo "arm64" ;;
    *) fail "Unsupported architecture for the official Node.js binary tarball." ;;
  esac
}

ensure_nodejs() {
  local node_major="${CITUS_NODE_MAJOR:-22}"
  local node_arch
  node_arch="$(detect_node_arch)"
  local latest_series
  latest_series="$(curl -fsSL "https://nodejs.org/dist/latest-v${node_major}.x/SHASUMS256.txt")"
  local latest_version
  latest_version="$(printf '%s\n' "${latest_series}" | sed -n "s/.*node-\\(v[0-9.]*\\)-linux-${node_arch}\\.tar\\.xz/\\1/p" | head -n 1)"
  [[ -n "${latest_version}" ]] || fail "Unable to resolve the latest Node.js v${node_major}.x version."

  local archive="node-${latest_version}-linux-${node_arch}.tar.xz"
  local expected_sha
  expected_sha="$(printf '%s\n' "${latest_series}" | grep -F "  ${archive}" | awk '{print $1}')"
  [[ -n "${expected_sha}" ]] || fail "Unable to resolve the SHA256 for ${archive}."

  local install_dir="/usr/local/lib/nodejs/node-${latest_version}-linux-${node_arch}"
  if [[ ! -d "${install_dir}" ]]; then
    local archive_path="/tmp/${archive}"
    log "Installing Node.js ${latest_version}."
    mkdir -p /usr/local/lib/nodejs
    curl -fsSL "https://nodejs.org/dist/${latest_version}/${archive}" -o "${archive_path}"
    printf '%s  %s\n' "${expected_sha}" "${archive_path}" | sha256sum -c -
    tar -xJf "${archive_path}" -C /usr/local/lib/nodejs
    rm -f "${archive_path}"
  fi

  ln -sfn "${install_dir}" /usr/local/lib/nodejs/citus-node
  ln -sfn /usr/local/lib/nodejs/citus-node/bin/node /usr/local/bin/node
  ln -sfn /usr/local/lib/nodejs/citus-node/bin/npm /usr/local/bin/npm
  ln -sfn /usr/local/lib/nodejs/citus-node/bin/npx /usr/local/bin/npx
  if [[ -x /usr/local/lib/nodejs/citus-node/bin/corepack ]]; then
    ln -sfn /usr/local/lib/nodejs/citus-node/bin/corepack /usr/local/bin/corepack
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
  mkdir -p "${SOURCE_DIR}" "${PUBLISH_DIR}" "${RUNTIME_DIR}/frontend" "${BACKUP_DIR}"
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
DO \$\$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = :'citus_db_user') THEN
    EXECUTE format('CREATE ROLE %I LOGIN PASSWORD %L', :'citus_db_user', :'citus_db_password');
  ELSE
    EXECUTE format('ALTER ROLE %I WITH LOGIN PASSWORD %L', :'citus_db_user', :'citus_db_password');
  END IF;
END
\$\$;
SQL

  if ! sudo -u postgres psql -tAc "SELECT 1 FROM pg_database WHERE datname = '${CITUS_DB_NAME}'" | grep -q 1; then
    sudo -u postgres createdb --owner="${CITUS_DB_USER}" "${CITUS_DB_NAME}"
  fi
}

frontend_sqlite_path() {
  load_env_file
  printf '%s\n' "${DATABASE_URL#file:}"
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

seed_frontend_if_empty() {
  local sqlite_path
  sqlite_path="$(frontend_sqlite_path)"

  local user_table_exists
  user_table_exists="$(sqlite3 "${sqlite_path}" "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'User';" 2>/dev/null || echo "0")"
  if [[ "${user_table_exists}" != "1" ]]; then
    return
  fi

  local user_count
  user_count="$(sqlite3 "${sqlite_path}" "SELECT COUNT(*) FROM User;" 2>/dev/null || echo "0")"
  if [[ "${user_count}" != "0" ]]; then
    log "Frontend database already contains users; skipping prisma seed."
    return
  fi

  log "Seeding the frontend SQLite database."
  (
    cd "${SOURCE_DIR}"
    export DATABASE_URL
    npm run db:seed
  )
}

build_frontend() {
  load_env_file

  local sqlite_path
  sqlite_path="$(frontend_sqlite_path)"
  mkdir -p "$(dirname "${sqlite_path}")"
  touch "${sqlite_path}"
  chown -R "${APP_USER}:${APP_GROUP}" "${RUNTIME_DIR}"

  log "Installing frontend dependencies."
  (
    cd "${SOURCE_DIR}"
    export DATABASE_URL
    export NODE_ENV=production
    export NEXT_TELEMETRY_DISABLED=1
    npm ci
    npx prisma generate
    npx prisma db push
  )
}

build_frontend_production_bundle() {
  load_env_file
  log "Building the Next.js production bundle."
  (
    cd "${SOURCE_DIR}"
    export DATABASE_URL
    export NODE_ENV=production
    export NEXT_TELEMETRY_DISABLED=1
    npm run build
  )

  chown -R "${APP_USER}:${APP_GROUP}" \
    "${SOURCE_DIR}/.next" \
    "${SOURCE_DIR}/node_modules" \
    "${RUNTIME_DIR}"
}

publish_backends() {
  load_env_file

  log "Publishing .NET services."
  mkdir -p \
    "${PUBLISH_DIR}/accounting-api" \
    "${PUBLISH_DIR}/sysadmin-api" \
    "${PUBLISH_DIR}/consoleapp"

  dotnet publish "${SOURCE_DIR}/backend/src/Citus.Accounting.Api/Citus.Accounting.Api.csproj" -c Release -o "${PUBLISH_DIR}/accounting-api"
  dotnet publish "${SOURCE_DIR}/backend/src/Citus.SysAdmin.Api/Citus.SysAdmin.Api.csproj" -c Release -o "${PUBLISH_DIR}/sysadmin-api"
  dotnet publish "${SOURCE_DIR}/backend/src/Citus.ConsoleApp/Citus.ConsoleApp.csproj" -c Release -o "${PUBLISH_DIR}/consoleapp"

  chown -R "${APP_USER}:${APP_GROUP}" "${PUBLISH_DIR}"
}

bootstrap_platform_core() {
  load_env_file
  log "Bootstrapping the platform metadata registry."
  (
    cd "${PUBLISH_DIR}/consoleapp"
    export CITUS_ACCOUNTING_DB
    dotnet ./Citus.ConsoleApp.dll bootstrap-core
  )
}

write_systemd_units() {
  load_env_file
  log "Writing systemd service units."

  cat > "${SYSTEMD_DIR}/citus-web.service" <<EOF
[Unit]
Description=Citus Next.js frontend
After=network.target
Wants=network.target

[Service]
Type=simple
User=${APP_USER}
Group=${APP_GROUP}
WorkingDirectory=${SOURCE_DIR}
EnvironmentFile=${ENV_FILE}
Environment=PATH=/usr/local/bin:/usr/bin:/bin
ExecStart=/usr/local/bin/npm start -- --hostname ${CITUS_FRONTEND_HOST} --port ${CITUS_FRONTEND_PORT}
Restart=always
RestartSec=5
KillSignal=SIGINT
SyslogIdentifier=citus-web

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
Environment=PATH=/usr/bin:/bin
ExecStart=/usr/bin/dotnet ${PUBLISH_DIR}/accounting-api/Citus.Accounting.Api.dll --urls http://${CITUS_API_HOST}:${CITUS_ACCOUNTING_API_PORT}
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
Environment=PATH=/usr/bin:/bin
ExecStart=/usr/bin/dotnet ${PUBLISH_DIR}/sysadmin-api/Citus.SysAdmin.Api.dll --urls http://${CITUS_API_HOST}:${CITUS_SYSADMIN_API_PORT}
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

  location = /health/accounting {
    proxy_pass http://${CITUS_API_HOST}:${CITUS_ACCOUNTING_API_PORT}/health;
  }

  location = /health/sysadmin {
    proxy_pass http://${CITUS_API_HOST}:${CITUS_SYSADMIN_API_PORT}/health;
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
  systemctl enable citus-web.service citus-accounting-api.service citus-sysadmin-api.service
}

restart_nginx_service() {
  log "Restarting nginx."
  systemctl restart nginx
}

restart_application_services() {
  log "Restarting application services."
  systemctl restart citus-accounting-api.service
  systemctl restart citus-sysadmin-api.service
  systemctl restart citus-web.service
}

stop_application_services() {
  log "Stopping current application services."
  systemctl stop citus-web.service 2>/dev/null || true
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
  wait_for_http "citus-web" "http://${CITUS_FRONTEND_HOST}:${CITUS_FRONTEND_PORT}/"
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

  local sqlite_path
  sqlite_path="$(frontend_sqlite_path)"
  if [[ -f "${sqlite_path}" ]]; then
    cp -a "${sqlite_path}" "${BACKUP_DIR}/frontend-${timestamp}.sqlite"
    log "Backed up the frontend SQLite database."
  fi

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
  local sqlite_path
  sqlite_path="$(frontend_sqlite_path)"
  local public_ip
  public_ip="$(hostname -I 2>/dev/null | awk '{print $1}')"
  local frontend_url="http://${public_ip}/"

  if is_truthy "${CITUS_ENABLE_HTTPS}" && certificate_files_exist; then
    frontend_url="https://${CITUS_SERVER_NAME}/"
  fi

  cat <<EOF

Deployment complete.

Frontend:
  ${frontend_url}

Reverse-proxied endpoints:
  /accounting -> accounting API
  /core       -> sysadmin API
  /health/accounting
  /health/sysadmin

Local service ports:
  Frontend:      http://${CITUS_FRONTEND_HOST}:${CITUS_FRONTEND_PORT}
  Accounting:    http://${CITUS_API_HOST}:${CITUS_ACCOUNTING_API_PORT}
  SysAdmin:      http://${CITUS_API_HOST}:${CITUS_SYSADMIN_API_PORT}

Runtime files:
  Source:        ${SOURCE_DIR}
  Publish:       ${PUBLISH_DIR}
  Frontend DB:   ${sqlite_path}
  Env file:      ${ENV_FILE}
  ACME webroot:  ${ACME_WEBROOT}

Seeded frontend login on first install:
  Email:         owner@example.com
  Username:      owner
  Password:      password123

Important note:
  The frontend schema is applied with \`prisma db push\`.
  The backend PostgreSQL draft baseline is only applied automatically to an empty database.
  Citus services auto-start: ${CITUS_AUTO_START}
  HTTPS enabled:             ${CITUS_ENABLE_HTTPS}
EOF
}

install_main() {
  require_root
  require_ubuntu_24_04
  ensure_base_packages
  ensure_dotnet
  ensure_nodejs
  ensure_app_user
  ensure_layout
  ensure_env_file
  ensure_env_defaults
  configure_runtime_preferences
  load_env_file
  sync_source_tree
  ensure_postgres_database
  apply_backend_baseline_if_needed
  build_frontend
  seed_frontend_if_empty
  build_frontend_production_bundle
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
  print_install_summary
}

upgrade_main() {
  require_root
  require_ubuntu_24_04
  [[ -f "${ENV_FILE}" ]] || fail "Missing ${ENV_FILE}. Run install.sh before upgrade.sh."
  ensure_base_packages
  ensure_dotnet
  ensure_nodejs
  ensure_app_user
  ensure_layout
  ensure_env_defaults
  configure_runtime_preferences
  load_env_file
  stop_application_services
  backup_datastores
  sync_source_tree
  ensure_postgres_database
  apply_backend_baseline_if_needed
  build_frontend
  build_frontend_production_bundle
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
  log "Upgrade complete."
}
