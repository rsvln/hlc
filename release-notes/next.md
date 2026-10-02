### New
- **Agent resources**: `GET /api/system` — CPU %, memory, free space of every disk, network rates (sampled every 5 s). The same values are exported in `/metrics` (`hlca_cpu_percent`, `hlca_memory_*_bytes`, `hlca_disk_*_bytes`, `hlca_network_*_bytes_per_second`).
- **Host page — resources**: CPU and memory chart, disk space bars, network rates (agent 2.0.9+).
