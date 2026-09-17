#!/usr/bin/env bash
set -euo pipefail

FILE="Services/LinuxEdlBackend.cs"
BACKUP="${FILE}.before-sahara-status-$(date +%Y%m%d-%H%M%S)"

echo "===== BACKUP ====="
cp -av "$FILE" "$BACKUP"

echo
echo "===== PATCH SAHARA STATUS DECODER ====="

python3 <<'PY'
from pathlib import Path

p = Path("Services/LinuxEdlBackend.cs")
s = p.read_text()

old = '''                            Log(
                                $"[Linux/Sahara] END_OF_IMAGE " +
                                $"image={imageId} status=0x{status:x}"
                            );

                            if (status != 0)
                            {
                                Log(
                                    "[Linux/Sahara] Device rejected " +
                                    $"programmer, Sahara status=0x{status:x}"
                                );

                                return false;
                            }
'''

new = '''                            var statusName =
                                GetSaharaStatusName(status);

                            Log(
                                $"[Linux/Sahara] END_OF_IMAGE " +
                                $"image={imageId} " +
                                $"status=0x{status:x} ({statusName})"
                            );

                            if (status != 0)
                            {
                                Log(
                                    "[Linux/Sahara] Device rejected " +
                                    $"programmer: 0x{status:x} " +
                                    $"({statusName})"
                                );

                                return false;
                            }
'''

if old not in s:
    raise SystemExit(
        "ERROR: END_OF_IMAGE logging block not found"
    )

s = s.replace(old, new, 1)

marker = '''        private static string HexDump(byte[] data)
'''

method = '''        private static string GetSaharaStatusName(uint status)
        {
            return status switch
            {
                0x00 => "SUCCESS",
                0x01 => "INVALID_CMD",
                0x02 => "PROTOCOL_MISMATCH",
                0x03 => "INVALID_TARGET_PROTOCOL",
                0x04 => "INVALID_HOST_PROTOCOL",
                0x05 => "INVALID_PACKET_SIZE",
                0x06 => "UNEXPECTED_IMAGE_ID",
                0x07 => "INVALID_HEADER_SIZE",
                0x08 => "INVALID_DATA_SIZE",
                0x09 => "INVALID_IMAGE_TYPE",
                0x0A => "INVALID_TX_LENGTH",
                0x0B => "INVALID_RX_LENGTH",
                0x0C => "GENERAL_TX_RX_ERROR",
                0x0D => "READ_DATA_ERROR",
                0x0E => "UNSUPPORTED_NUM_PHDRS",
                0x0F => "INVALID_PHDR_SIZE",
                0x10 => "MULTIPLE_SHARED_SEG",
                0x11 => "UNINIT_PHDR_LOC",
                0x12 => "INVALID_DEST_ADDR",
                0x13 => "INVALID_IMG_HDR_DATA_SIZE",
                0x14 => "INVALID_ELF_HDR",
                0x15 => "UNKNOWN_HOST_ERROR",
                0x16 => "TIMEOUT_RX",
                0x17 => "TIMEOUT_TX",
                0x18 => "INVALID_HOST_MODE",
                0x19 => "INVALID_MEMORY_READ",
                0x1A => "INVALID_DATA_SIZE_REQUEST",
                0x1B => "MEMORY_DEBUG_NOT_SUPPORTED",
                0x1C => "INVALID_MODE_SWITCH",
                0x1D => "CMD_EXEC_FAILURE",
                0x1E => "EXEC_CMD_INVALID_PARAM",
                0x1F => "EXEC_CMD_UNSUPPORTED",
                0x20 => "EXEC_DATA_INVALID_CLIENT_CMD",
                0x21 => "HASH_TABLE_AUTH_FAILURE",
                0x22 => "HASH_VERIFICATION_FAILURE",
                0x23 => "HASH_TABLE_NOT_FOUND",
                0x24 => "TARGET_INIT_FAILURE",
                0x25 => "IMAGE_AUTH_FAILURE",
                0x26 => "INVALID_IMG_HASH_TABLE_SIZE",
                _ => $"UNKNOWN_0x{status:X}"
            };
        }

'''

if "GetSaharaStatusName(uint status)" not in s:
    if marker not in s:
        raise SystemExit(
            "ERROR: insertion point HexDump() not found"
        )

    s = s.replace(
        marker,
        method + marker,
        1
    )

p.write_text(s)

print("PASS: Sahara status decoder installed")
PY

echo
echo "===== VERIFY ====="

grep -n -A35 \
    'GetSaharaStatusName' \
    "$FILE" | tail -45

echo
echo "===== BUILD ====="

/usr/bin/dotnet build

echo
echo "===== RESULT ====="

if [ $? -eq 0 ]; then
    echo "PASS: build completed"
else
    echo "FAIL: build failed"
    exit 1
fi

echo
echo "Launch with:"
echo
echo "  /usr/bin/dotnet run --no-build"
