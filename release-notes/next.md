### Added
- **Wake-on-LAN relays** for hosts in other networks (another subnet, behind a VPN), where a broadcast from HLC does not reach. A relay is either the network's gateway over SSH (OpenWrt or any Linux with `etherwake`) or any host there with an agent. The relay is chosen by the host's subnet automatically; `power.wolVia` on a host overrides it. Relays have their own grid in Power Control with Test, Setup (installs the HLC SSH key and `etherwake` on OpenWrt), Edit and Delete.
- **Agent: `POST /api/power/wol?mac=`** sends a magic packet into the agent's networks.

### Changed
- WOL from the UI, the API, MQTT and Automation goes through the relay when one applies; the message and the audit log say which way the packet went. An Automation `wake` step fails if the packet could not be sent.
