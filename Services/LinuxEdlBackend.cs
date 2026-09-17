using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using LibUsbDotNet;
using LibUsbDotNet.LibUsb;
using LibUsbDotNet.Main;

namespace OplusEdlTool.Services
{
    /// <summary>
    /// Native Linux Qualcomm EDL/Sahara backend.
    ///
    /// Stage 1:
    ///   - Detect Qualcomm 05c6:9008
    ///   - Open libusb interface 0
    ///   - Receive Sahara HELLO
    ///   - Send HELLO_RESP in image-transfer mode
    ///   - Service READ_DATA / READ_DATA64
    ///   - Send programmer from disk
    ///   - Complete Sahara with DONE
    ///
    /// This stage does NOT implement Firehose storage commands yet.
    /// </summary>
    internal sealed class LinuxEdlBackend
    {
        private const int VendorId = 0x05c6;
        private const int ProductId = 0x9008;

        private const uint SaharaHello = 0x01;
        private const uint SaharaHelloResponse = 0x02;
        private const uint SaharaReadData = 0x03;
        private const uint SaharaEndOfImage = 0x04;
        private const uint SaharaDone = 0x05;
        private const uint SaharaDoneResponse = 0x06;
        private const uint SaharaReset = 0x07;
        private const uint SaharaReadData64 = 0x12;

        // "<?xm" interpreted little-endian.
        private const uint SaharaXml = 0x6d783f3c;

        private const uint SaharaModeWaitingForImage = 0;

        private readonly Action<string>? onLine;
        private readonly Action<int>? onPercent;

        public LinuxEdlBackend(
            Action<string>? onLine = null,
            Action<int>? onPercent = null)
        {
            this.onLine = onLine;
            this.onPercent = onPercent;
        }

        public bool Is9008Present()
        {
            const string usbRoot = "/sys/bus/usb/devices";

            if (!Directory.Exists(usbRoot))
                return false;

            try
            {
                foreach (var deviceDir in Directory.EnumerateDirectories(usbRoot))
                {
                    var vidPath = Path.Combine(deviceDir, "idVendor");
                    var pidPath = Path.Combine(deviceDir, "idProduct");

                    if (!File.Exists(vidPath) || !File.Exists(pidPath))
                        continue;

                    var vid = File.ReadAllText(vidPath).Trim();
                    var pid = File.ReadAllText(pidPath).Trim();

                    if (
                        vid.Equals("05c6", StringComparison.OrdinalIgnoreCase) &&
                        pid.Equals("9008", StringComparison.OrdinalIgnoreCase)
                    )
                    {
                        return true;
                    }
                }
            }
            catch
            {
            }

            return false;
        }

        public Task<bool> SendProgrammerAsync(string programmerPath)
        {
            return Task.Run(() => SendProgrammer(programmerPath));
        }

        private bool SendProgrammer(string programmerPath)
        {
            if (!File.Exists(programmerPath))
            {
                Log($"[Linux/Sahara] Programmer not found: {programmerPath}");
                return false;
            }

            var programmer = File.ReadAllBytes(programmerPath);

            if (programmer.Length == 0)
            {
                Log("[Linux/Sahara] Programmer file is empty.");
                return false;
            }

            Log(
                $"[Linux/Sahara] Programmer: " +
                $"{Path.GetFileName(programmerPath)} " +
                $"({programmer.Length:N0} bytes)"
            );

            Log("[Linux/Sahara/Auth] Inspecting signed ELF/MBNv7 stages...");
            var authStages = SaharaProgrammerInspector.Inspect(programmer);

            foreach (var stage in authStages)
                Log("[Linux/Sahara/Auth] " + stage);

            if (authStages.Count == 0)
            {
                Log(
                    "[Linux/Sahara/Auth] No Qualcomm MBNv7 hash segments " +
                    "were recognized in this programmer."
                );
            }

            using var context = new UsbContext();
            using var devices = context.List();

            var device = devices.FirstOrDefault(
                d => d.VendorId == VendorId &&
                     d.ProductId == ProductId
            );

            if (device == null)
            {
                Log("[Linux/Sahara] Qualcomm 05c6:9008 not found.");
                return false;
            }

            var claimedInterface = -1;

            try
            {
                Log("[Linux/Sahara] Opening 05c6:9008...");
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
                {
                    throw new IOException(
                        "No USB configuration/interface found."
                    );
                }

                claimedInterface =
                    device.Configs[0].Interfaces[0].Number;

                Log(
                    $"[Linux/Sahara] Claiming interface " +
                    $"{claimedInterface}"
                );

                device.ClaimInterface(claimedInterface);

                Log(
                    $"[Linux/Sahara] Interface {claimedInterface} claimed."
                );

                Thread.Sleep(100);

                // Your OnePlus 11 exposed:
                //
                //     0x81 BULK IN
                //     0x01 BULK OUT
                //
                var reader = device.OpenEndpointReader(
                    ReadEndpointID.Ep01,
                    4096
                );

                var writer = device.OpenEndpointWriter(
                    WriteEndpointID.Ep01
                );

                Log(
                    "[Linux/Sahara] USB transport ready " +
                    "(IN=0x81 OUT=0x01)"
                );

                onPercent?.Invoke(0);

                SaharaAuthStage? pendingAuthStage = null;
                var passedAuthStages = new HashSet<(int Elf, int Ph)>();

                while (true)
                {
                    var packet = ReadPacket(reader);

                    if (packet.Length < 8)
                    {
                        throw new IOException(
                            $"Short Sahara packet: {packet.Length}"
                        );
                    }

                    var command = ReadU32(packet, 0);
                    var packetLength = ReadU32(packet, 4);

                    Log(
                        $"[Linux/Sahara] RX cmd=0x{command:x2} " +
                        $"len={packetLength}"
                    );

                    switch (command)
                    {
                        case SaharaHello:
                        {
                            HandleHello(packet, writer);
                            break;
                        }

                        case SaharaReadData:
                        {
                            if (packet.Length < 20)
                                throw new IOException(
                                    "Malformed SAHARA_READ_DATA packet."
                                );

                            var imageId = ReadU32(packet, 8);
                            var offset = ReadU32(packet, 12);
                            var length = ReadU32(packet, 16);

                            Log(
                                $"[Linux/Sahara] READ_DATA " +
                                $"image={imageId} " +
                                $"offset=0x{offset:x} " +
                                $"length=0x{length:x}"
                            );

                            MarkPendingAuthPassed(
                                ref pendingAuthStage,
                                passedAuthStages,
                                offset,
                                length
                            );

                            var authStage = FindAuthStage(
                                authStages,
                                offset,
                                length
                            );

                            if (authStage.HasValue)
                            {
                                pendingAuthStage = authStage;
                                Log(
                                    "[Linux/Sahara/Auth] Target requested " +
                                    "signed auth stage: " +
                                    authStage.Value
                                );
                            }

                            SendProgrammerRange(
                                writer,
                                programmer,
                                offset,
                                length
                            );

                            UpdateProgress(
                                programmer.LongLength,
                                (long)offset + length
                            );

                            break;
                        }

                        case SaharaReadData64:
                        {
                            if (packet.Length < 32)
                                throw new IOException(
                                    "Malformed SAHARA_READ_DATA64 packet."
                                );

                            var imageId = ReadU64(packet, 8);
                            var offset = ReadU64(packet, 16);
                            var length = ReadU64(packet, 24);

                            Log(
                                $"[Linux/Sahara] READ_DATA64 " +
                                $"image={imageId} " +
                                $"offset=0x{offset:x} " +
                                $"length=0x{length:x}"
                            );

                            MarkPendingAuthPassed(
                                ref pendingAuthStage,
                                passedAuthStages,
                                offset,
                                length
                            );

                            var authStage = FindAuthStage(
                                authStages,
                                offset,
                                length
                            );

                            if (authStage.HasValue)
                            {
                                pendingAuthStage = authStage;
                                Log(
                                    "[Linux/Sahara/Auth] Target requested " +
                                    "signed auth stage: " +
                                    authStage.Value
                                );
                            }

                            SendProgrammerRange(
                                writer,
                                programmer,
                                offset,
                                length
                            );

                            var completed =
                                offset > long.MaxValue ||
                                length > long.MaxValue ||
                                offset + length > long.MaxValue
                                    ? programmer.LongLength
                                    : (long)(offset + length);

                            UpdateProgress(
                                programmer.LongLength,
                                completed
                            );

                            break;
                        }

                        case SaharaEndOfImage:
                        {
                            if (packet.Length < 16)
                                throw new IOException(
                                    "Malformed SAHARA_END_OF_IMAGE packet."
                                );

                            var imageId = ReadU32(packet, 8);
                            var status = ReadU32(packet, 12);

                            var statusName =
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

                                if (status == 0x21)
                                {
                                    Log(
                                        "[Linux/Sahara/Auth] HASH_TABLE_AUTH_FAILURE " +
                                        "was returned after the target consumed the " +
                                        "signed hash/authentication segment."
                                    );
                                    Log(
                                        "[Linux/Sahara/Auth] This is a pre-execution " +
                                        "authentication failure: programmer runtime " +
                                        "initialization (DT/DDR/UFS/Firehose) has not " +
                                        "started yet."
                                    );
                                    SaharaAuthStage? rejectedStage = null;

                                    if (pendingAuthStage.HasValue)
                                    {
                                        rejectedStage = pendingAuthStage.Value;

                                        Log(
                                            "[Linux/Sahara/Auth] Rejected stage: " +
                                            rejectedStage.Value
                                        );

                                        if (rejectedStage.Value.SoftwareId == 0x03)
                                        {
                                            Log(
                                                "[Linux/Sahara/Auth] Correlated failure: " +
                                                "SW_ID 0x03 DEVICE-PROGRAMMER " +
                                                "authentication policy."
                                            );
                                        }
                                    }
                                    else
                                    {
                                        Log(
                                            "[Linux/Sahara/Auth] No exact MBNv7 auth " +
                                            "range correlation was available for the " +
                                            "last target request."
                                        );
                                    }

                                    LogAuthStateSummary(
                                        authStages,
                                        passedAuthStages,
                                        rejectedStage,
                                        status,
                                        statusName
                                    );
                                }

                                return false;
                            }

                            if (pendingAuthStage.HasValue)
                            {
                                var accepted = pendingAuthStage.Value;
                                passedAuthStages.Add(
                                    (accepted.ElfIndex, accepted.ProgramHeaderIndex)
                                );
                                Log(
                                    "[Linux/Sahara/Auth] AUTH PASSED: " +
                                    ShortStageName(accepted)
                                );
                                pendingAuthStage = null;
                            }

                            LogAuthStateSummary(
                                authStages,
                                passedAuthStages,
                                null,
                                0,
                                "SUCCESS"
                            );

                            Log("[Linux/Sahara] Sending DONE...");
                            WriteExact(writer, BuildDonePacket());
                            break;
                        }

                        case SaharaDoneResponse:
                        {
                            uint status =
                                packet.Length >= 12
                                    ? ReadU32(packet, 8)
                                    : 0xffffffff;

                            Log(
                                $"[Linux/Sahara] DONE_RESP " +
                                $"status=0x{status:x}"
                            );

                            onPercent?.Invoke(100);

                            Log(
                                "[Linux/Sahara] Programmer upload complete."
                            );

                            return true;
                        }

                        case SaharaReset:
                        {
                            Log(
                                "[Linux/Sahara] Device requested reset."
                            );
                            return false;
                        }

                        case SaharaXml:
                        {
                            Log(
                                "[Linux/Sahara] Firehose XML detected; " +
                                "loader is already running."
                            );

                            onPercent?.Invoke(100);
                            return true;
                        }

                        default:
                        {
                            Log(
                                $"[Linux/Sahara] Unsupported packet " +
                                $"0x{command:x}"
                            );

                            Log(HexDump(packet));
                            return false;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log(
                    $"[Linux/Sahara] ERROR: " +
                    $"{ex.GetType().Name}: {ex.Message}"
                );

                return false;
            }
            finally
            {
                try
                {
                    if (claimedInterface >= 0)
                        device.ReleaseInterface(claimedInterface);
                }
                catch
                {
                }

                try
                {
                    device.Close();
                }
                catch
                {
                }
            }
        }

        private void HandleHello(
            byte[] packet,
            UsbEndpointWriter writer)
        {
            if (packet.Length < 24)
                throw new IOException(
                    "Malformed SAHARA_HELLO packet."
                );

            var version = ReadU32(packet, 8);
            var compatible = ReadU32(packet, 12);
            var maxLength = ReadU32(packet, 16);
            var mode = ReadU32(packet, 20);

            Log(
                $"[Linux/Sahara] HELLO " +
                $"version={version} " +
                $"compatible={compatible} " +
                $"maxlen={maxLength} " +
                $"mode={mode}"
            );

            //
            // Qualcomm QSaharaServer v3 behavior:
            //   version=3, version_supported=3, status=0, mode=image TX,
            //   reserved words=1,2,3,4,5,6.
            //
            // Keep a legacy fallback for older targets rather than forcing v3
            // onto a device that advertises an older protocol.
            //
            var response = new byte[48];
            var hostVersion = version >= 3 ? 3u : 2u;
            var hostCompatible = version >= 3 ? 3u : 1u;

            WriteU32(response, 0, SaharaHelloResponse);
            WriteU32(response, 4, 48);
            WriteU32(response, 8, hostVersion);
            WriteU32(response, 12, hostCompatible);
            WriteU32(response, 16, 0);
            WriteU32(
                response,
                20,
                SaharaModeWaitingForImage
            );

            if (version >= 3)
            {
                WriteU32(response, 24, 1);
                WriteU32(response, 28, 2);
                WriteU32(response, 32, 3);
                WriteU32(response, 36, 4);
                WriteU32(response, 40, 5);
                WriteU32(response, 44, 6);
            }

            Log(
                $"[Linux/Sahara] TX HELLO_RESP version={hostVersion} " +
                $"compatible={hostCompatible} mode=WaitingForImage"
            );

            WriteExact(writer, response);
        }

        private static SaharaAuthStage? FindAuthStage(
            IReadOnlyList<SaharaAuthStage> stages,
            ulong offset,
            ulong length)
        {
            foreach (var stage in stages)
            {
                if (stage.MatchesRange(offset, length))
                    return stage;
            }

            return null;
        }

        private void MarkPendingAuthPassed(
            ref SaharaAuthStage? pendingAuthStage,
            HashSet<(int Elf, int Ph)> passedAuthStages,
            ulong nextOffset,
            ulong nextLength)
        {
            if (!pendingAuthStage.HasValue)
                return;

            var pending = pendingAuthStage.Value;

            // A repeated request for the same auth segment is not proof that
            // authentication succeeded. Any different subsequent READ request
            // means the target advanced beyond that auth decision.
            if (pending.MatchesRange(nextOffset, nextLength))
                return;

            passedAuthStages.Add(
                (pending.ElfIndex, pending.ProgramHeaderIndex)
            );

            Log(
                "[Linux/Sahara/Auth] AUTH PASSED: " +
                ShortStageName(pending)
            );

            pendingAuthStage = null;
        }

        private void LogAuthStateSummary(
            IReadOnlyList<SaharaAuthStage> stages,
            HashSet<(int Elf, int Ph)> passedAuthStages,
            SaharaAuthStage? rejectedStage,
            uint status,
            string statusName)
        {
            if (stages.Count == 0)
                return;

            Log("[Linux/Sahara/Auth] ===== AUTH STATE SUMMARY =====");

            foreach (var stage in stages)
            {
                var key = (stage.ElfIndex, stage.ProgramHeaderIndex);

                if (rejectedStage.HasValue &&
                    rejectedStage.Value.ElfIndex == stage.ElfIndex &&
                    rejectedStage.Value.ProgramHeaderIndex == stage.ProgramHeaderIndex)
                {
                    Log(
                        "[Linux/Sahara/Auth] " +
                        ShortStageName(stage) +
                        $" AUTH FAILED 0x{status:x} ({statusName})"
                    );
                }
                else if (passedAuthStages.Contains(key))
                {
                    Log(
                        "[Linux/Sahara/Auth] " +
                        ShortStageName(stage) +
                        " AUTH PASSED"
                    );
                }
                else
                {
                    Log(
                        "[Linux/Sahara/Auth] " +
                        ShortStageName(stage) +
                        " NOT REACHED"
                    );
                }
            }

            Log("[Linux/Sahara/Auth] ==============================");
        }

        private static string ShortStageName(SaharaAuthStage stage)
        {
            return
                $"ELF{stage.ElfIndex}/PH{stage.ProgramHeaderIndex} " +
                $"SW_ID=0x{stage.SoftwareId:x} ({stage.SoftwareIdName}) " +
                $"ARB={stage.AntiRollbackVersion}";
        }

        private void SendProgrammerRange(
            UsbEndpointWriter writer,
            byte[] programmer,
            ulong offset,
            ulong requestedLength)
        {
            if (offset > (ulong)programmer.LongLength)
            {
                throw new IOException(
                    $"Device requested offset 0x{offset:x} " +
                    $"past programmer size 0x{programmer.LongLength:x}."
                );
            }

            if (requestedLength > int.MaxValue)
            {
                throw new IOException(
                    $"Sahara requested an unreasonable block: " +
                    $"{requestedLength} bytes."
                );
            }

            if (
                requestedLength >
                (ulong)programmer.LongLength - offset
            )
            {
                throw new IOException(
                    $"Device requested programmer range " +
                    $"0x{offset:x}+0x{requestedLength:x}, " +
                    $"file size is 0x{programmer.LongLength:x}."
                );
            }

            var sourceOffset = checked((int)offset);
            var remaining = checked((int)requestedLength);

            while (remaining > 0)
            {
                //
                // Keep individual host writes reasonably sized.
                //
                var count = Math.Min(
                    remaining,
                    1024 * 1024
                );

                var span = programmer.AsSpan(
                    sourceOffset,
                    count
                );

                var error = writer.Write(
                    span,
                    15000,
                    out var written
                );

                if (error != Error.Success)
                {
                    throw new IOException(
                        $"USB write failed: {error}"
                    );
                }

                if (written <= 0)
                {
                    throw new IOException(
                        "USB write returned zero bytes."
                    );
                }

                sourceOffset += written;
                remaining -= written;
            }

            Log(
                $"[Linux/Sahara] TX complete " +
                $"offset=0x{offset:x} " +
                $"length=0x{requestedLength:x}"
            );
        }

        private static byte[] ReadPacket(
            UsbEndpointReader reader)
        {
            var buffer = new byte[4096];

            var error = reader.Read(
                buffer,
                60000,
                out var transferred
            );

            if (error != Error.Success)
            {
                throw new IOException(
                    $"USB read failed: {error}; " +
                    $"transferred={transferred}"
                );
            }

            if (transferred <= 0)
            {
                throw new IOException(
                    "USB read returned no data."
                );
            }

            Array.Resize(ref buffer, transferred);
            return buffer;
        }

        private static void WriteExact(
            UsbEndpointWriter writer,
            byte[] data)
        {
            var offset = 0;

            while (offset < data.Length)
            {
                var error = writer.Write(
                    data,
                    offset,
                    data.Length - offset,
                    10000,
                    out var written
                );

                if (error != Error.Success)
                {
                    throw new IOException(
                        $"USB write failed: {error}"
                    );
                }

                if (written <= 0)
                {
                    throw new IOException(
                        "USB write returned zero bytes."
                    );
                }

                offset += written;
            }
        }

        private static byte[] BuildDonePacket()
        {
            var packet = new byte[8];

            WriteU32(packet, 0, SaharaDone);
            WriteU32(packet, 4, 8);

            return packet;
        }

        private void UpdateProgress(
            long total,
            long complete)
        {
            if (total <= 0)
                return;

            var percent = (int)Math.Clamp(
                complete * 100L / total,
                0,
                100
            );

            onPercent?.Invoke(percent);
        }

        private void Log(string message)
        {
            onLine?.Invoke(message);
        }

        private static uint ReadU32(
            byte[] buffer,
            int offset)
        {
            return BinaryPrimitives.ReadUInt32LittleEndian(
                buffer.AsSpan(offset, 4)
            );
        }

        private static ulong ReadU64(
            byte[] buffer,
            int offset)
        {
            return BinaryPrimitives.ReadUInt64LittleEndian(
                buffer.AsSpan(offset, 8)
            );
        }

        private static void WriteU32(
            byte[] buffer,
            int offset,
            uint value)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                buffer.AsSpan(offset, 4),
                value
            );
        }

        private static string GetSaharaStatusName(uint status)
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

        private static string HexDump(byte[] data)
        {
            return BitConverter
                .ToString(data)
                .Replace("-", " ");
        }
    }
}
