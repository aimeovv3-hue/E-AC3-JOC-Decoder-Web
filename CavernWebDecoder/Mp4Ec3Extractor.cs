using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

public static class Mp4Ec3Extractor
{
    /// <summary>
    /// Checks if the file is an ISOBMFF (.m4a / .mp4) container.
    /// If Cavern's AudioReader doesn't open the specific fragmented/unfragmented M4A directly,
    /// or if it contains an 'ec-3' sample table, this helper extracts the raw E-AC-3 bitstream
    /// so EnhancedAC3Reader can always decode it seamlessly.
    /// </summary>
    public static byte[] ExtractIfMp4(byte[] data, string fileName, bool logEnabled, Action<string, bool> log)
    {
        if (data.Length < 16) return data;

        // Check for 'ftyp' box at offset 4
        if (data[4] == 'f' && data[5] == 't' && data[6] == 'y' && data[7] == 'p')
        {
            if (logEnabled)
                log("[MP4Demux] Detected ISOBMFF (.m4a/.mp4) container. Scanning for E-AC-3 JOC syncframes...", false);

            byte[]? extracted = ExtractEc3SyncFramesFromMdat(data);
            if (extracted != null && extracted.Length > 0)
            {
                if (logEnabled)
                    log($"[MP4Demux] Extracted {extracted.Length} bytes of raw E-AC-3 bitstream from container.", false);
                return extracted;
            }
        }
        return data;
    }

    private static byte[]? ExtractEc3SyncFramesFromMdat(byte[] data)
    {
        using MemoryStream output = new MemoryStream();
        int offset = 0;
        bool foundMdat = false;

        while (offset + 8 <= data.Length)
        {
            uint size32 = ReadUInt32BE(data, offset);
            string type = Encoding.ASCII.GetString(data, offset + 4, 4);
            long boxSize = size32;
            int headerSize = 8;

            if (size32 == 1 && offset + 16 <= data.Length)
            {
                boxSize = (long)ReadUInt64BE(data, offset + 8);
                headerSize = 16;
            }
            else if (size32 == 0)
            {
                boxSize = data.Length - offset;
            }

            if (boxSize < headerSize || offset + boxSize > data.Length)
                break;

            if (type == "mdat")
            {
                foundMdat = true;
                int payloadStart = offset + headerSize;
                int payloadEnd = (int)(offset + boxSize);

                // Verify AC-3 / E-AC-3 syncword (0x0B 0x77)
                if (payloadStart + 2 <= payloadEnd && data[payloadStart] == 0x0B && data[payloadStart + 1] == 0x77)
                {
                    output.Write(data, payloadStart, payloadEnd - payloadStart);
                }
            }

            offset += (int)boxSize;
        }

        if (foundMdat && output.Length > 0)
            return output.ToArray();

        return null;
    }

    private static uint ReadUInt32BE(byte[] b, int i) =>
        ((uint)b[i] << 24) | ((uint)b[i + 1] << 16) | ((uint)b[i + 2] << 8) | b[i + 3];

    private static ulong ReadUInt64BE(byte[] b, int i) =>
        ((ulong)ReadUInt32BE(b, i) << 32) | ReadUInt32BE(b, i + 4);
}
