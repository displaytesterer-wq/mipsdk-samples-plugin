using System;
using System.IO;
using System.Text;

namespace AutoWavExport
{
    // Checks RIFF/WAVE structure, chunk boundaries, format and nonempty audio.
    // This is NOT evidence of uninterrupted recording for the entire requested interval.
    internal static class WavCheck
    {
        public static bool Validate(string path, out string reason)
        {
            reason = "";
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var br = new BinaryReader(fs, Encoding.ASCII))
                {
                    if (fs.Length < 44) { reason = "too short"; return false; }
                    if (Encoding.ASCII.GetString(br.ReadBytes(4)) != "RIFF") { reason = "RIFF missing"; return false; }
                    uint declared = br.ReadUInt32();
                    if (declared != fs.Length - 8) { reason = "RIFF size mismatch"; return false; }
                    if (Encoding.ASCII.GetString(br.ReadBytes(4)) != "WAVE") { reason = "WAVE missing"; return false; }
                    bool fmt = false, data = false;
                    while (fs.Position + 8 <= fs.Length)
                    {
                        string name = Encoding.ASCII.GetString(br.ReadBytes(4));
                        uint length = br.ReadUInt32();
                        long next = fs.Position + (long)length + (length % 2);
                        if (next > fs.Length) { reason = "truncated chunk"; return false; }
                        if (name == "fmt " && length >= 16)
                        {
                            ushort audioFormat = br.ReadUInt16();
                            ushort channels = br.ReadUInt16();
                            uint sampleRate = br.ReadUInt32();
                            br.ReadUInt32(); br.ReadUInt16(); ushort bits = br.ReadUInt16();
                            if (audioFormat == 0 || channels == 0 || sampleRate == 0 || bits == 0)
                            { reason = "invalid format"; return false; }
                            fmt = true;
                        }
                        if (name == "data" && length > 0) data = true;
                        fs.Position = next;
                    }
                    if (!fmt || !data) { reason = "missing fmt or nonempty data chunk"; return false; }
                    if (fs.Position != fs.Length) { reason = "unparsed trailing bytes"; return false; }
                    return true;
                }
            }
            catch (Exception ex) { reason = ex.Message; return false; }
        }
    }
}
