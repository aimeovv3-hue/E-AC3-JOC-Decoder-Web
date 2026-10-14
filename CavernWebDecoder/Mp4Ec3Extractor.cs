using System;
using System.IO;
using System.Text;

public static class Mp4Ec3Extractor
{
    /// <summary>
    /// Detects ISOBMFF (.m4a / .mp4) containers and extracts all valid E-AC-3 / AC-3 syncframes (0x0B77).
    /// Handles standard mdat, fragmented moof+mdat, and mdat boxes containing cover art before audio chunks.
    /// </summary>
    public static byte[] ExtractIfMp4(byte[] data, string fileName, bool logEnabled, Action<string, bool> log)
    {
        if (data.Length < 16) return data;

        // If already starts with AC-3 / E-AC-3 syncword (0x0B 0x77), RIFF ('RIFF'), or Matroska (0x1A 0x45 0xDF 0xA3), return as-is
        if ((data[0] == 0x0B && data[1] == 0x77) ||
            (data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F') ||
            (data[0] == 0x1A && data[1] == 0x45 && data[2] == 0xDF && data[3] == 0xA3))
        {
            return data;
        }

        bool isIsobmff = (data[4] == 'f' && data[5] == 't' && data[6] == 'y' && data[7] == 'p') ||
                         fileName.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase) ||
                         fileName.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase);

        if (isIsobmff)
        {
            if (logEnabled)
                log("[Cavernize Demuxer] Detected M4A/MP4 container. Extracting E-AC-3 JOC frames...", false);

            byte[]? extracted = ExtractVerifiedEc3Frames(data, logEnabled, log);
            if (extracted != null && extracted.Length > 0)
            {
                return extracted;
            }
        }

        return data;
    }

    private static byte[]? ExtractVerifiedEc3Frames(byte[] data, bool logEnabled, Action<string, bool> log)
    {
        using MemoryStream output = new MemoryStream();
        int frameCount = 0;

        // First try scanning inside 'mdat' boxes; if top-level box walk fails, scan the full buffer
        int offset = 0;
        bool scannedMdat = false;

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
                scannedMdat = true;
                int start = offset + headerSize;
                int end = (int)(offset + boxSize);
                frameCount += ScanRangeForEc3SyncFrames(data, start, end, output);
            }

            offset += (int)boxSize;
        }

        if (!scannedMdat || output.Length == 0)
        {
            output.SetLength(0);
            frameCount = ScanRangeForEc3SyncFrames(data, 0, data.Length, output);
        }

        if (output.Length > 0)
        {
            if (logEnabled)
                log($"[Cavernize Demuxer] Extracted {frameCount} E-AC-3 syncframes ({output.Length:N0} bytes).", false);
            return output.ToArray();
        }

        return null;
    }

    /// <summary>
    /// Walks a byte range and extracts contiguous or chunked E-AC-3 syncframes using ATSC A/52B frame length headers.
    /// </summary>
    private static int ScanRangeForEc3SyncFrames(byte[] data, int start, int end, MemoryStream output)
    {
        int count = 0;
        int i = start;

        while (i + 6 <= end)
        {
            if (data[i] == 0x0B && data[i + 1] == 0x77)
            {
                int frameSize = GetEc3FrameSize(data, i, end);
                if (frameSize > 0)
                {
                    // Verify that either the next frame also starts with 0x0B77, or we already locked onto the stream
                    bool nextIsSync = (i + frameSize + 2 <= end && data[i + frameSize] == 0x0B && data[i + frameSize + 1] == 0x77) ||
                                      (i + frameSize == end) ||
                                      count > 0;

                    if (nextIsSync)
                    {
                        output.Write(data, i, frameSize);
                        count++;
                        i += frameSize;
                        continue;
                    }
                }
            }
            i++;
        }

        return count;
    }

    private static int GetEc3FrameSize(byte[] data, int offset, int maxEnd)
    {
        if (offset + 6 > maxEnd) return -1;

        int bsid = (data[offset + 5] >> 3) & 0x1F;
        // E-AC-3 uses bsid = 16 (or 11..16)
        if (bsid >= 11 && bsid <= 16)
        {
            int frmsiz = ((data[offset + 2] & 0x07) << 8) | data[offset + 3];
            int frameBytes = (frmsiz + 1) * 2;
            int fscod = (data[offset + 4] >> 6) & 0x03;

            if (frameBytes >= 32 && frameBytes <= 4096 && offset + frameBytes <= maxEnd && fscod <= 3)
            {
                return frameBytes;
            }
        }
        return -1;
    }

    private static uint ReadUInt32BE(byte[] b, int i) =>
        ((uint)b[i] << 24) | ((uint)b[i + 1] << 16) | ((uint)b[i + 2] << 8) | b[i + 3];

    private static ulong ReadUInt64BE(byte[] b, int i) =>
        ((ulong)ReadUInt32BE(b, i) << 32) | ReadUInt32BE(b, i + 4);
}
