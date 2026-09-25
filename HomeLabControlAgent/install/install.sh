#!/bin/sh
# Установка / обновление HomeLabControlAgent как службы systemd. Запускать из папки сборки:
#   sudo sh install.sh                                   # /srv/homeLabControlAgent, порт 8117
#   sudo sh install.sh --path /opt/hlca --port 8118
#   sudo sh install.sh --uninstall
#
# Файлы копируются в --path, кроме appsettings.Local.json и profiles.json (настройки машины).
# appsettings.Local.json создаётся только при первой установке — с новыми ключами API
# homelabcontrol и homeassistant. В конце ключи печатаются: homelabcontrol вписать в HLC
# (Config -> Agents -> Add existing), homeassistant — в rest_command HA (заголовок X-Api-Key).
set -e

INSTALL_PATH=/srv/homeLabControlAgent
SERVICE=homeLabControlAgent
PORT=
UNINSTALL=0

while [ $# -gt 0 ]; do
    case "$1" in
        --path) INSTALL_PATH="$2"; shift 2 ;;
        --service) SERVICE="$2"; shift 2 ;;
        --port) PORT="$2"; shift 2 ;;
        --uninstall) UNINSTALL=1; shift ;;
        *) echo "Unknown option: $1"; exit 1 ;;
    esac
done

[ "$(id -u)" -eq 0 ] || { echo "Run as root: sudo sh $0 $*"; exit 1; }

EXE=HomeLabControlAgent
UNIT=/etc/systemd/system/$SERVICE.service
LOCAL="$INSTALL_PATH/appsettings.Local.json"
SRC=$(cd "$(dirname "$0")" && pwd)

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
else
    HLC_KEY=$("$INSTALL_PATH/$EXE" --generate-key)
    HA_KEY=$("$INSTALL_PATH/$EXE" --generate-key)
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

SERVICE_PORT=$(sed -n 's/.*"ServicePort": *\([0-9]*\).*/\1/p' "$LOCAL" | head -n 1)
SERVICE_PORT=${SERVICE_PORT:-8117}

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

echo
echo "HLC key  (Config -> Agents -> Add existing): $(sed -n 's/.*"homelabcontrol": *"\([^"]*\)".*/\1/p' "$LOCAL")"
echo "HA key   (rest_command header X-Api-Key):    $(sed -n 's/.*"homeassistant": *"\([^"]*\)".*/\1/p' "$LOCAL")"
