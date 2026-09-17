using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Security.Cryptography;

namespace OplusEdlTool.Services
{
    internal readonly record struct SaharaAuthStage(
        int ElfIndex,
        int ProgramHeaderIndex,
        uint SoftwareId,
        string SoftwareIdName,
        uint AntiRollbackVersion,
        uint MrcIndex,
        uint[] SocHwVersions,
        uint OemId,
        uint OemProductId,
        uint Flags,
        uint HashAlgorithm,
        ulong SegmentStart,
        ulong SegmentLength,
        string Sha384)
    {
        public bool MatchesRange(ulong offset, ulong length) =>
            SegmentStart == offset && SegmentLength == length;

        public override string ToString()
        {
            var soc = SocHwVersions.Length == 0
                ? "none"
                : string.Join(",", Array.ConvertAll(
                    SocHwVersions,
                    value => $"0x{value:x}"
                ));

            return
                $"ELF{ElfIndex}/PH{ProgramHeaderIndex} " +
                $"SW_ID=0x{SoftwareId:x} ({SoftwareIdName}) " +
                $"ARB={AntiRollbackVersion} MRC={MrcIndex} " +
                $"SoC={soc} OEM=0x{OemId:x} PID=0x{OemProductId:x} " +
                $"flags=0x{Flags:x8} hash_alg={HashAlgorithm} " +
                $"auth=0x{SegmentStart:x}+0x{SegmentLength:x} " +
                $"SHA384={Sha384}";
        }
    }

    /// <summary>
    /// Read-only inspection of Qualcomm ELF/MBNv7 authentication metadata.
    /// This code never modifies, transforms, or resigns the programmer.
    /// </summary>
    internal static class SaharaProgrammerInspector
    {
        private const uint QualcommHashSegmentFlag = 0x02000000;
        private const uint QualcommSegmentTypeMask = 0x0F000000;

        public static IReadOnlyList<SaharaAuthStage> Inspect(byte[] image)
        {
            var stages = new List<SaharaAuthStage>();

            if (image == null || image.Length < 4)
                return stages;

            var elfIndex = 0;

            for (var baseOffset = 0; baseOffset <= image.Length - 4; baseOffset++)
            {
                if (image[baseOffset] != 0x7f ||
                    image[baseOffset + 1] != (byte)'E' ||
                    image[baseOffset + 2] != (byte)'L' ||
                    image[baseOffset + 3] != (byte)'F')
                {
                    continue;
                }

                if (baseOffset + 6 > image.Length || image[baseOffset + 5] != 1)
                    continue;

                var elfClass = image[baseOffset + 4];

                if (!TryGetProgramHeaderLayout(
                        image,
                        baseOffset,
                        elfClass,
                        out var phoff,
                        out var phentsize,
                        out var phnum))
                {
                    continue;
                }

                for (var phIndex = 0; phIndex < phnum; phIndex++)
                {
                    var ph64 =
                        (ulong)baseOffset +
                        phoff +
                        (ulong)phIndex * (ulong)phentsize;

                    if (ph64 > int.MaxValue)
                        continue;

                    var ph = (int)ph64;

                    if (!TryReadProgramHeader(
                            image,
                            ph,
                            elfClass,
                            out var type,
                            out var flags,
                            out var fileOffset,
                            out var fileSize))
                    {
                        continue;
                    }

                    if (type != 0 ||
                        (flags & QualcommSegmentTypeMask) != QualcommHashSegmentFlag ||
                        fileSize < 40)
                    {
                        continue;
                    }

                    var segmentStart64 = (ulong)baseOffset + fileOffset;
                    var segmentEnd64 = segmentStart64 + fileSize;

                    if (segmentStart64 > int.MaxValue ||
                        segmentEnd64 > (ulong)image.LongLength ||
                        segmentEnd64 > int.MaxValue)
                    {
                        continue;
                    }

                    var segmentStart = (int)segmentStart64;
                    var segmentLength = (int)fileSize;

                    if (!TryParseMbnV7(
                            image,
                            segmentStart,
                            segmentLength,
                            out var info))
                    {
                        continue;
                    }

                    var digest = SHA384.HashData(
                        image.AsSpan(segmentStart, segmentLength)
                    );

                    stages.Add(
                        new SaharaAuthStage(
                            elfIndex,
                            phIndex,
                            info.SoftwareId,
                            GetSoftwareIdName(info.SoftwareId),
                            info.AntiRollbackVersion,
                            info.MrcIndex,
                            info.SocHwVersions,
                            info.OemId,
                            info.OemProductId,
                            info.Flags,
                            info.HashAlgorithm,
                            (ulong)segmentStart,
                            (ulong)segmentLength,
                            Convert.ToHexString(digest).ToLowerInvariant()
                        )
                    );
                }

                elfIndex++;
            }

            return stages;
        }

        public static IEnumerable<string> Describe(byte[] image)
        {
            foreach (var stage in Inspect(image))
                yield return stage.ToString();
        }

        private static bool TryGetProgramHeaderLayout(
            byte[] image,
            int baseOffset,
            byte elfClass,
            out ulong phoff,
            out int phentsize,
            out int phnum)
        {
            phoff = 0;
            phentsize = 0;
            phnum = 0;

            try
            {
                if (elfClass == 1)
                {
                    if (baseOffset + 46 > image.Length)
                        return false;

                    phoff = ReadU32(image, baseOffset + 28);
                    phentsize = ReadU16(image, baseOffset + 42);
                    phnum = ReadU16(image, baseOffset + 44);
                }
                else if (elfClass == 2)
                {
                    if (baseOffset + 58 > image.Length)
                        return false;

                    phoff = ReadU64(image, baseOffset + 32);
                    phentsize = ReadU16(image, baseOffset + 54);
                    phnum = ReadU16(image, baseOffset + 56);
                }
                else
                {
                    return false;
                }

                if (phentsize <= 0 || phnum <= 0)
                    return false;

                var end =
                    (ulong)baseOffset +
                    phoff +
                    (ulong)phentsize * (ulong)phnum;

                return end <= (ulong)image.LongLength;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryReadProgramHeader(
            byte[] image,
            int ph,
            byte elfClass,
            out uint type,
            out uint flags,
            out ulong fileOffset,
            out ulong fileSize)
        {
            type = 0;
            flags = 0;
            fileOffset = 0;
            fileSize = 0;

            try
            {
                if (elfClass == 1)
                {
                    if (ph + 32 > image.Length)
                        return false;

                    type = ReadU32(image, ph);
                    fileOffset = ReadU32(image, ph + 4);
                    fileSize = ReadU32(image, ph + 16);
                    flags = ReadU32(image, ph + 24);
                    return true;
                }

                if (elfClass == 2)
                {
                    if (ph + 56 > image.Length)
                        return false;

                    type = ReadU32(image, ph);
                    flags = ReadU32(image, ph + 4);
                    fileOffset = ReadU64(image, ph + 8);
                    fileSize = ReadU64(image, ph + 32);
                    return true;
                }
            }
            catch
            {
            }

            return false;
        }

        private static bool TryParseMbnV7(
            byte[] image,
            int segmentStart,
            int segmentLength,
            out MbnV7Info info)
        {
            info = default;

            try
            {
                if (segmentLength < 40)
                    return false;

                if (ReadU32(image, segmentStart + 4) != 7)
                    return false;

                var commonMetadataSize = ReadU32(image, segmentStart + 8);
                var qtiMetadataSize = ReadU32(image, segmentStart + 12);
                var oemMetadataSize = ReadU32(image, segmentStart + 16);

                if (commonMetadataSize < 24 || oemMetadataSize < 224)
                    return false;

                var commonStart = checked(segmentStart + 40);
                var oemStart64 =
                    (ulong)commonStart +
                    commonMetadataSize +
                    qtiMetadataSize;

                if (oemStart64 > int.MaxValue)
                    return false;

                var oemStart = (int)oemStart64;
                var segmentEnd = checked(segmentStart + segmentLength);

                if ((ulong)oemStart + oemMetadataSize > (ulong)segmentEnd)
                    return false;

                var softwareId = ReadU32(image, commonStart + 8);
                var hashAlgorithm = ReadU32(image, commonStart + 16);
                var antiRollbackVersion = ReadU32(image, oemStart + 8);
                var mrcIndex = ReadU32(image, oemStart + 12);

                var soc = new List<uint>(12);
                for (var i = 0; i < 12; i++)
                {
                    var value = ReadU32(image, oemStart + 16 + i * 4);
                    if (value != 0)
                        soc.Add(value);
                }

                info = new MbnV7Info(
                    softwareId,
                    hashAlgorithm,
                    antiRollbackVersion,
                    mrcIndex,
                    soc.ToArray(),
                    ReadU32(image, oemStart + 136),
                    ReadU32(image, oemStart + 140),
                    ReadU32(image, oemStart + 220)
                );

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string GetSoftwareIdName(uint softwareId)
        {
            return softwareId switch
            {
                0x03 => "DEVICE-PROGRAMMER",
                0x25 => "XBL-CONFIG",
                0x35 => "TME-FW",
                0x36 => "XBL-SC",
                _ => "unknown"
            };
        }

        private static ushort ReadU16(byte[] image, int offset) =>
            BinaryPrimitives.ReadUInt16LittleEndian(image.AsSpan(offset, 2));

        private static uint ReadU32(byte[] image, int offset) =>
            BinaryPrimitives.ReadUInt32LittleEndian(image.AsSpan(offset, 4));

        private static ulong ReadU64(byte[] image, int offset) =>
            BinaryPrimitives.ReadUInt64LittleEndian(image.AsSpan(offset, 8));

        private readonly record struct MbnV7Info(
            uint SoftwareId,
            uint HashAlgorithm,
            uint AntiRollbackVersion,
            uint MrcIndex,
            uint[] SocHwVersions,
            uint OemId,
            uint OemProductId,
            uint Flags
        );
    }
}
