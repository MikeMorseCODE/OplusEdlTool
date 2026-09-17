#!/usr/bin/env bash
set -euo pipefail

FILE="MainWindow.axaml.cs"
BACKUP="${FILE}.before-linux-9008-$(date +%Y%m%d-%H%M%S)"

echo "===== BACKUP ====="
cp -av "$FILE" "$BACKUP"

echo
echo "===== PATCH Detect9008Port() ====="

python3 <<'PY'
from pathlib import Path

path = Path("MainWindow.axaml.cs")
src = path.read_text()

signature = "private string? Detect9008Port()"

start = src.find(signature)
if start < 0:
    raise SystemExit("ERROR: Detect9008Port() not found")

brace = src.find("{", start)
if brace < 0:
    raise SystemExit("ERROR: opening brace not found")

depth = 0
end = None

for i in range(brace, len(src)):
    c = src[i]

    if c == "{":
        depth += 1
    elif c == "}":
        depth -= 1

        if depth == 0:
            end = i + 1
            break

if end is None:
    raise SystemExit("ERROR: could not determine method boundary")

replacement = r'''private string? Detect9008Port()
        {
            try
            {
                if (OperatingSystem.IsLinux())
                {
                    const string usbRoot = "/sys/bus/usb/devices";

                    if (!Directory.Exists(usbRoot))
                        return null;

                    foreach (var deviceDir in Directory.EnumerateDirectories(usbRoot))
                    {
                        var vendorPath = Path.Combine(deviceDir, "idVendor");
                        var productPath = Path.Combine(deviceDir, "idProduct");

                        if (!File.Exists(vendorPath) || !File.Exists(productPath))
                            continue;

                        var vendor = File.ReadAllText(vendorPath).Trim();
                        var product = File.ReadAllText(productPath).Trim();

                        if (vendor.Equals("05c6", StringComparison.OrdinalIgnoreCase) &&
                            product.Equals("9008", StringComparison.OrdinalIgnoreCase))
                        {
                            var busPath = Path.Combine(deviceDir, "busnum");
                            var devPath = Path.Combine(deviceDir, "devnum");

                            var bus = File.Exists(busPath)
                                ? File.ReadAllText(busPath).Trim()
                                : "?";

                            var dev = File.Exists(devPath)
                                ? File.ReadAllText(devPath).Trim()
                                : "?";

                            return $"Qualcomm EDL 05c6:9008 [Bus {bus} Device {dev}]";
                        }
                    }

                    return null;
                }

                // Windows backend
                var toolsDir = FindToolsDir();

                if (toolsDir == null)
                    return null;

                var lsusb = Path.Combine(toolsDir, "lsusb.exe");

                if (!File.Exists(lsusb))
                    return null;

                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = lsusb,
                    WorkingDirectory = toolsDir,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };

                using var proc = System.Diagnostics.Process.Start(psi);

                if (proc == null)
                    return null;

                var output = proc.StandardOutput.ReadToEnd();

                proc.WaitForExit(2000);

                var m = Regex.Match(
                    output,
                    @"Qualcomm HS-USB QDLoader 9008 \(COM(?<n>\d+)\)"
                );

                if (m.Success)
                    return "COM" + m.Groups["n"].Value;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"9008 detection failed: {ex.Message}"
                );
            }

            return null;
        }'''

patched = src[:start] + replacement + src[end:]
path.write_text(patched)

print("PASS: Detect9008Port() replaced")
PY

echo
echo "===== VERIFY PATCH ====="
grep -n -A12 -B3 'private string? Detect9008Port' "$FILE" | head -40

echo
echo "===== BUILD ====="
/usr/bin/dotnet build

echo
echo "===== USB CHECK ====="
if grep -Rqs '^05c6$' /sys/bus/usb/devices/*/idVendor 2>/dev/null; then
    for d in /sys/bus/usb/devices/*; do
        [ -f "$d/idVendor" ] || continue
        [ -f "$d/idProduct" ] || continue

        VID="$(cat "$d/idVendor" 2>/dev/null || true)"
        PID="$(cat "$d/idProduct" 2>/dev/null || true)"

        if [ "$VID" = "05c6" ] && [ "$PID" = "9008" ]; then
            BUS="$(cat "$d/busnum" 2>/dev/null || echo '?')"
            DEV="$(cat "$d/devnum" 2>/dev/null || echo '?')"

            echo "PASS: Qualcomm EDL detected"
            echo "VID:PID = $VID:$PID"
            echo "Bus     = $BUS"
            echo "Device  = $DEV"
            echo "Sysfs   = $d"
        fi
    done
else
    echo "NOTE: 05c6:9008 not currently detected"
fi

echo
echo "===== LAUNCH ====="
exec /usr/bin/dotnet run --no-build
