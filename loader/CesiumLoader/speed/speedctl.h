// Per-process loopback UDP channel, discovered via CESIUM_SPEED_PORT.
// Requests (ASCII, <= 511 bytes): "get <id>" or "set <operation-id> <speed> [attempt-id]".
// Retries reuse operation-id but use a fresh attempt-id, echoed as response id.
// id: positive signed-64-bit decimal, increasing per client socket; speed: [1,100].
// Response: protocol=1/id/revision/ok/version/speed/base/active/hooks key=value lines.
// Mutations and response snapshots share the control-plane lock, never a clock-hook lock.
#pragma once
#include <string>

std::wstring speedctl_dir(); // Legacy path API; no transport files are created/read.
void speedctl_start(const std::wstring& dir, double base_speed);
bool speedctl_set_speed(double speed);

namespace cesium::speed {
using ::speedctl_dir;
using ::speedctl_start;
using ::speedctl_set_speed;
}
