#!/usr/bin/env bash
set -euo pipefail

FILE="Services/LinuxEdlBackend.cs"
BACKUP="${FILE}.before-usb-init-$(date +%Y%m%d-%H%M%S)"

cp -av "$FILE" "$BACKUP"

python3 <<'PY'
from pathlib import Path

p = Path("Services/LinuxEdlBackend.cs")
s = p.read_text()

if "using System.Threading;" not in s:
    s = s.replace(
        "using System.Threading.Tasks;",
        "using System.Threading;\nusing System.Threading.Tasks;"
    )

old = '''                Log("[Linux/Sahara] Opening 05c6:9008...");
                device.Open();

                if (device.Configs.Count == 0 ||
                    device.Configs[0].Interfaces.Count == 0)
'''

new = '''                Log("[Linux/Sahara] Opening 05c6:9008...");
                device.Open();

                Log(
                    $"[Linux/Sahara] USB configs reported: {device.Configs.Count}"
                );

                //
                // Linux libusb whole-device access expects the desired
                // configuration to be selected before claiming an interface.
                //
                try
                {
                    device.SetConfiguration(1);
                    Log("[Linux/Sahara] USB configuration 1 selected.");
                }
                catch (Exception ex)
                {
                    Log(
                        $"[Linux/Sahara] SetConfiguration(1) warning: " +
                        $"{ex.GetType().Name}: {ex.Message}"
                    );
                }

                //
                // Give the Qualcomm USB device a moment to settle after
                // configuration selection before claiming interface 0.
                //
                Thread.Sleep(150);

                if (device.Configs.Count == 0 ||
                    device.Configs[0].Interfaces.Count == 0)
'''

if old not in s:
    raise SystemExit("ERROR: device.Open() block not found")

s = s.replace(old, new, 1)

old2 = '''                device.ClaimInterface(claimedInterface);

                // Your OnePlus 11 exposed:
'''

new2 = '''                device.ClaimInterface(claimedInterface);

                Log(
                    $"[Linux/Sahara] Interface {claimedInterface} claimed."
                );

                Thread.Sleep(100);

                // Your OnePlus 11 exposed:
'''

if old2 not in s:
    raise SystemExit("ERROR: ClaimInterface block not found")

s = s.replace(old2, new2, 1)

old3 = '''            if (error != Error.Success)
            {
                throw new IOException(
                    $"USB read failed: {error}"
                );
            }
'''

new3 = '''            if (error != Error.Success)
            {
                throw new IOException(
                    $"USB read failed: {error}; " +
                    $"transferred={transferred}"
                );
            }
'''

if old3 in s:
    s = s.replace(old3, new3, 1)

p.write_text(s)
print("PASS: Linux USB initialization patched")
PY

echo
echo "===== VERIFY ====="
grep -n -A35 -B5 'USB configs reported' "$FILE"

echo
echo "===== BUILD ====="
/usr/bin/dotnet build

echo
echo "===== DONE ====="
echo "Re-enter 9008 fresh, then:"
echo
echo "  /usr/bin/dotnet run --no-build"
