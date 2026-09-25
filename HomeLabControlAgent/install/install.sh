#!/bin/sh
# Установка / обновление HomeLabControlAgent как службы systemd. Запускать из папки сборки:
#   sudo sh install.sh                                   # обновить установленный / новая установка в /srv/homeLabControlAgent
#   sudo sh install.sh --path /opt/hlca --port 8118
#   sudo sh install.sh --generate-keys                   # включить ключи API у агента, который работал без них
#   sudo sh install.sh --uninstall
#
# Уже установленный агент (unit-файл службы) находится сам: путь берётся из WorkingDirectory.
# Файлы копируются в --path, кроме appsettings.Local.json и profiles.json (настройки машины).
# Ключи API (homelabcontrol и homeassistant) создаются при новой установке или с --generate-keys;
# существующие ключи не меняются никогда. В конце ключи печатаются: homelabcontrol вписать в HLC
# (Config -> Agents -> Add existing), homeassistant — в rest_command HA (заголовок X-Api-Key).
set -e

INSTALL_PATH=
SERVICE=homeLabControlAgent
PORT=
GENERATE_KEYS=0
UNINSTALL=0

while [ $# -gt 0 ]; do
    case "$1" in
        --path) INSTALL_PATH="$2"; shift 2 ;;
        --service) SERVICE="$2"; shift 2 ;;
        --port) PORT="$2"; shift 2 ;;
        --generate-keys) GENERATE_KEYS=1; shift ;;
        --uninstall) UNINSTALL=1; shift ;;
        *) echo "Unknown option: $1"; exit 1 ;;
    esac
done

[ "$(id -u)" -eq 0 ] || { echo "Run as root: sudo sh $0 $*"; exit 1; }

EXE=HomeLabControlAgent
UNIT=/etc/systemd/system/$SERVICE.service

# Уже установленный агент: путь из unit-файла службы
if [ -z "$INSTALL_PATH" ] && [ -f "$UNIT" ]; then
    INSTALL_PATH=$(sed -n 's/^WorkingDirectory=//p' "$UNIT" | head -n 1)
    [ -z "$INSTALL_PATH" ] || echo "Installed agent found: service $SERVICE in $INSTALL_PATH"
fi
INSTALL_PATH=${INSTALL_PATH:-/srv/homeLabControlAgent}
LOCAL="$INSTALL_PATH/appsettings.Local.json"
IS_UPDATE=0
{ [ -f "$UNIT" ] || [ -f "$INSTALL_PATH/$EXE" ]; } && IS_UPDATE=1
SRC=$(cd "$(dirname "$0")" && pwd)

# Тот же формат, что у HomeLabControlAgent --generate-key: 32 случайных байта, base64url.
# Без запуска агента: на хосте может не быть libssl, нужной .NET для криптографии.
new_key() { head -c 32 /dev/urandom | base64 | tr -d '\n=' | tr '+/' '-_'; }

# ─── Удаление ────────────────────────────────────────────────────────────────

if [ "$UNINSTALL" = 1 ]; then
    systemctl disable --now "$SERVICE" 2>/dev/null || true
    rm -f "$UNIT"
    systemctl daemon-reload
    rm -rf "$INSTALL_PATH"
    echo "Service $SERVICE and $INSTALL_PATH removed. Remove the host from HLC: Config -> Agents -> trash icon."
    exit 0
fi

# ─── Файлы ───────────────────────────────────────────────────────────────────

[ -f "$SRC/$EXE" ] || { echo "$EXE not found next to the script ($SRC) — run install.sh from the agent build folder"; exit 1; }

# .NET на Linux без libssl не запускается ("No usable version of libssl was found")
HAS_SSL=0
for f in /usr/lib/libssl.so* /usr/lib64/libssl.so* /usr/lib/*/libssl.so* /lib/*/libssl.so*; do
    [ -e "$f" ] && HAS_SSL=1 && break
done
if [ "$HAS_SSL" = 0 ]; then
    echo "ERROR: libssl not found — install it first: apt install libssl3 | dnf install openssl-libs | apk add libssl3"
    exit 1
fi

systemctl stop "$SERVICE" 2>/dev/null || true
mkdir -p "$INSTALL_PATH"

if [ "$SRC" != "$(cd "$INSTALL_PATH" && pwd)" ]; then
    find "$SRC" -mindepth 1 -maxdepth 1 ! -name appsettings.Local.json ! -name profiles.json \
        -exec cp -a {} "$INSTALL_PATH/" \;
    echo "Files copied to $INSTALL_PATH"
fi
# Распакованный на Windows архив теряет бит исполнения
chmod +x "$INSTALL_PATH/$EXE" "$INSTALL_PATH/createdump" 2>/dev/null || true

# ─── Ключи и порт: appsettings.Local.json ────────────────────────────────────

if [ -f "$LOCAL" ]; then
    echo "appsettings.Local.json exists — keys kept"
    [ -z "$PORT" ] || echo "WARNING: set \"ServicePort\": $PORT in $LOCAL manually and restart: systemctl restart $SERVICE"
elif [ "$IS_UPDATE" = 1 ] && [ "$GENERATE_KEYS" = 0 ]; then
    # Агент работал без ключей: новые ключи сломали бы HA и HLC, которые ходят без них
    echo "WARNING: this agent has no API keys - the API stays OPEN as before."
    echo "         To enable keys run: sudo sh install.sh --generate-keys, then put the keys into HLC and HA."
else
    HLC_KEY=$(new_key)
    HA_KEY=$(new_key)
    {
        echo "{"
        [ -z "$PORT" ] || echo "  \"ServicePort\": $PORT,"
        echo "  \"Auth\": {"
        echo "    \"ApiKeys\": {"
        echo "      \"homelabcontrol\": \"$HLC_KEY\","
        echo "      \"homeassistant\": \"$HA_KEY\""
        echo "    }"
        echo "  }"
        echo "}"
    } > "$LOCAL"
    chmod 600 "$LOCAL"
    echo "appsettings.Local.json created with new API keys"
fi

SERVICE_PORT=
[ -f "$LOCAL" ] && SERVICE_PORT=$(sed -n 's/.*"ServicePort": *\([0-9]*\).*/\1/p' "$LOCAL" | head -n 1)
SERVICE_PORT=${SERVICE_PORT:-${PORT:-8117}}

# ─── Служба ──────────────────────────────────────────────────────────────────

cat > "$UNIT" <<EOF
[Unit]
Description=HomeLab Control Agent
After=network-online.target
Wants=network-online.target

[Service]
Type=notify
WorkingDirectory=$INSTALL_PATH
ExecStart=$INSTALL_PATH/$EXE
Restart=always
RestartSec=10

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable "$SERVICE" >/dev/null 2>&1
systemctl restart "$SERVICE"

# ─── Проверка ────────────────────────────────────────────────────────────────

INFO=
if command -v curl >/dev/null 2>&1; then
    for i in 1 2 3 4 5 6 7 8 9 10 11 12 13 14 15; do
        INFO=$(curl -fs "http://localhost:$SERVICE_PORT/api/agent/info" 2>/dev/null) && break
        sleep 1
    done
fi

echo
if [ -n "$INFO" ]; then
    echo "Agent is running: http://$(hostname):$SERVICE_PORT (Swagger) — $(echo "$INFO" | sed -n 's/.*"version":"\([^"]*\)".*/v\1/p')"
else
    echo "Service: $(systemctl is-active "$SERVICE"). Log: journalctl -u $SERVICE -n 50"
fi

[ -f "$LOCAL" ] || exit 0
echo
echo "HLC key  (Config -> Agents -> Add existing): $(sed -n 's/.*"homelabcontrol": *"\([^"]*\)".*/\1/p' "$LOCAL")"
echo "HA key   (rest_command header X-Api-Key):    $(sed -n 's/.*"homeassistant": *"\([^"]*\)".*/\1/p' "$LOCAL")"
