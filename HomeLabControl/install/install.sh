#!/bin/sh
# Установка / обновление HomeLabControl (без Docker) как службы systemd. Запускать из папки сборки:
#   sudo sh install.sh                                   # обновить установленный / новая установка в /srv/homeLabControl, порт 8208
#   sudo sh install.sh --path /opt/hlc --port 8080
#   sudo sh install.sh --admin admin --password 'secret123'
#   sudo sh install.sh --uninstall                       # служба и программа; config/ остаётся
#
# Уже установленный HLC (unit-файл службы) находится сам: путь берётся из WorkingDirectory.
# Не копируются и не меняются: config/ (конфиг, пользователи, ключи — если уже есть) и appsettings.Local.json.
# appsettings.Local.json создаётся при новой установке: порт (Urls) и, если заданы, HLC_ADMIN_USER / HLC_ADMIN_PASSWORD
# (администратор, который создаётся / восстанавливается при каждом старте — как в docker-compose).
set -e

INSTALL_PATH=
SERVICE=homeLabControl
PORT=
ADMIN_USER=
ADMIN_PASSWORD=
UNINSTALL=0

while [ $# -gt 0 ]; do
    case "$1" in
        --path) INSTALL_PATH="$2"; shift 2 ;;
        --service) SERVICE="$2"; shift 2 ;;
        --port) PORT="$2"; shift 2 ;;
        --admin) ADMIN_USER="$2"; shift 2 ;;
        --password) ADMIN_PASSWORD="$2"; shift 2 ;;
        --uninstall) UNINSTALL=1; shift ;;
        *) echo "Unknown option: $1"; exit 1 ;;
    esac
done

[ "$(id -u)" -eq 0 ] || { echo "Run as root: sudo sh $0 $*"; exit 1; }

EXE=HomeLabControl
UNIT=/etc/systemd/system/$SERVICE.service
SRC=$(cd "$(dirname "$0")" && pwd)

# Уже установленный HLC: путь из unit-файла службы
if [ -z "$INSTALL_PATH" ] && [ -f "$UNIT" ]; then
    INSTALL_PATH=$(sed -n 's/^WorkingDirectory=//p' "$UNIT" | head -n 1)
    [ -z "$INSTALL_PATH" ] || echo "Installed HomeLabControl found: service $SERVICE in $INSTALL_PATH"
fi
INSTALL_PATH=${INSTALL_PATH:-/srv/homeLabControl}
LOCAL="$INSTALL_PATH/appsettings.Local.json"

# ─── Удаление ────────────────────────────────────────────────────────────────

if [ "$UNINSTALL" = 1 ]; then
    systemctl disable --now "$SERVICE" 2>/dev/null || true
    rm -f "$UNIT"
    systemctl daemon-reload
    # Всё, кроме config/ (конфиг, пользователи, SSH-ключ деплоя) и appsettings.Local.json
    find "$INSTALL_PATH" -mindepth 1 -maxdepth 1 ! -name config ! -name appsettings.Local.json -exec rm -rf {} +
    echo "Service $SERVICE removed. Kept: $INSTALL_PATH/config and appsettings.Local.json (delete by hand if not needed)."
    exit 0
fi

# ─── Проверки ────────────────────────────────────────────────────────────────

[ -f "$SRC/$EXE" ] || { echo "$EXE not found next to the script ($SRC) — run install.sh from the HomeLabControl build folder"; exit 1; }

# .NET на Linux без libssl не запускается
HAS_SSL=0
for f in /usr/lib/libssl.so* /usr/lib64/libssl.so* /usr/lib/*/libssl.so* /lib/*/libssl.so*; do
    [ -e "$f" ] && HAS_SSL=1 && break
done
if [ "$HAS_SSL" = 0 ]; then
    echo "ERROR: libssl not found — install it first: apt install libssl3 | dnf install openssl-libs | apk add libssl3"
    exit 1
fi
command -v ssh-keygen >/dev/null 2>&1 || echo "WARNING: ssh-keygen not found — agent deploy and backup keys need it: apt install openssh-client"

if [ -n "$ADMIN_USER$ADMIN_PASSWORD" ]; then
    [ -n "$ADMIN_USER" ] && [ "${#ADMIN_PASSWORD}" -ge 8 ] || { echo "--admin and --password go together, password at least 8 characters"; exit 1; }
    case "$ADMIN_USER$ADMIN_PASSWORD" in *\"*|*\\*) echo "Admin name / password must not contain \" or \\"; exit 1 ;; esac
fi

# ─── Файлы ───────────────────────────────────────────────────────────────────

systemctl stop "$SERVICE" 2>/dev/null || true
mkdir -p "$INSTALL_PATH"

if [ "$SRC" != "$(cd "$INSTALL_PATH" && pwd)" ]; then
    find "$SRC" -mindepth 1 -maxdepth 1 ! -name appsettings.Local.json ! -name config -exec cp -a {} "$INSTALL_PATH/" \;
    # config/ из сборки (пример конфига) — только при первой установке
    if [ ! -f "$INSTALL_PATH/config/HomeLabControl.yaml" ] && [ -d "$SRC/config" ]; then
        mkdir -p "$INSTALL_PATH/config"
        cp -a "$SRC/config/." "$INSTALL_PATH/config/"
        echo "Sample config copied to $INSTALL_PATH/config — edit it in the UI: Config -> YAML"
    fi
    echo "Files copied to $INSTALL_PATH"
fi
# Распакованный на Windows архив теряет бит исполнения; агенты для деплоя — тоже
chmod +x "$INSTALL_PATH/$EXE" "$INSTALL_PATH/createdump" "$INSTALL_PATH/agent/linux-x64/HomeLabControlAgent" 2>/dev/null || true

# ─── Порт и администратор: appsettings.Local.json ────────────────────────────

if [ -f "$LOCAL" ]; then
    echo "appsettings.Local.json exists — kept"
    [ -z "$PORT$ADMIN_USER" ] || echo "WARNING: --port / --admin are not applied to an existing $LOCAL — edit it by hand and run: systemctl restart $SERVICE"
else
    {
        echo "{"
        [ -z "$ADMIN_USER" ] || echo "  \"HLC_ADMIN_USER\": \"$ADMIN_USER\","
        [ -z "$ADMIN_USER" ] || echo "  \"HLC_ADMIN_PASSWORD\": \"$ADMIN_PASSWORD\","
        echo "  \"Urls\": \"http://0.0.0.0:${PORT:-8208}\""
        echo "}"
    } > "$LOCAL"
    chmod 600 "$LOCAL"
    echo "appsettings.Local.json created"
fi

SERVICE_PORT=$(sed -n 's/.*"Urls": *"[^"]*:\([0-9]*\)".*/\1/p' "$LOCAL" | head -n 1)
SERVICE_PORT=${SERVICE_PORT:-8208}

# ─── Служба ──────────────────────────────────────────────────────────────────

sed -e "s#/srv/homeLabControl#$INSTALL_PATH#g" -e '/^#/d' "$SRC/homelabcontrol.service" > "$UNIT"
systemctl daemon-reload
systemctl enable "$SERVICE" >/dev/null 2>&1
systemctl restart "$SERVICE"

# ─── Проверка ────────────────────────────────────────────────────────────────

CODE=
if command -v curl >/dev/null 2>&1; then
    for i in $(seq 1 30); do
        CODE=$(curl -s -o /dev/null -w '%{http_code}' "http://localhost:$SERVICE_PORT/login" 2>/dev/null) || true
        case "$CODE" in 2*|3*) break ;; esac
        sleep 1
    done
fi

echo
case "$CODE" in
    2*|3*) echo "HomeLabControl is running: http://$(hostname):$SERVICE_PORT" ;;
    *) echo "Service: $(systemctl is-active "$SERVICE"). Log: journalctl -u $SERVICE -n 50" ;;
esac
[ -n "$ADMIN_USER" ] || grep -q HLC_ADMIN_USER "$LOCAL" || echo "No admin configured: the first visit opens /setup to create one."
