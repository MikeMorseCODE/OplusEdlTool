#!/usr/bin/env bash
set -euo pipefail

FILE="Services/LinuxEdlBackend.cs"
BACKUP="${FILE}.before-sahara-recovery-$(date +%Y%m%d-%H%M%S)"

echo "===== BACKUP ====="
cp -av "$FILE" "$BACKUP"

python3 <<'PY'
from pathlib import Path

p = Path("Services/LinuxEdlBackend.cs")
s = p.read_text()

# ---------------------------------------------------------
# Add RESET_RESP constant
# ---------------------------------------------------------

old = '''        private const uint SaharaReset = 0x07;
        private const uint SaharaReadData64 = 0x12;
'''

new = '''        private const uint SaharaReset = 0x07;
        private const uint SaharaResetResponse = 0x08;
        private const uint SaharaReadData64 = 0x12;
'''

if old in s:
    s = s.replace(old, new, 1)
elif "SaharaResetResponse" not in s:
    raise SystemExit("ERROR: could not add SaharaResetResponse constant")

# ---------------------------------------------------------
# Make ReadPacket timeout configurable
# ---------------------------------------------------------

old = '''        private static byte[] ReadPacket(
            UsbEndpointReader reader)
        {
            var buffer = new byte[4096];

            var error = reader.Read(
                buffer,
                10000,
                out var transferred
            );
'''

new = '''        private static byte[] ReadPacket(
            UsbEndpointReader reader,
            int timeout = 10000)
        {
            var buffer = new byte[4096];

            var error = reader.Read(
                buffer,
                timeout,
                out var transferred
            );
'''

if old in s:
    s = s.replace(old, new, 1)
elif "int timeout = 10000" not in s:
    raise SystemExit("ERROR: could not patch ReadPacket timeout")

# ---------------------------------------------------------
# Replace rejection path with reset/recovery
# ---------------------------------------------------------

old_variants = [
'''                            if (status != 0)
                            {
                                Log(
                                    "[Linux/Sahara] Device rejected " +
                                    $"programmer, Sahara status=0x{status:x}"
                                );

                                return false;
                            }
''',

'''                            if (status != 0)
                            {
                                Log(
                                    "[Linux/Sahara] Device rejected " +
                                    $"programmer: 0x{status:x} " +
                                    $"({statusName})"
                                );

                                return false;
                            }
'''
]

replacement = '''                            if (status != 0)
                            {
                                Log(
                                    "[Linux/Sahara] Device rejected " +
                                    $"programmer, Sahara status=0x{status:x}"
                                );

                                TryResetSahara(
                                    reader,
                                    writer
                                );

                                return false;
                            }
'''

found = False

for old in old_variants:
    if old in s:
        s = s.replace(old, replacement, 1)
        found = True
       

eof
