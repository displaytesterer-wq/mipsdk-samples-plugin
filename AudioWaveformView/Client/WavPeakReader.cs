using System;
using System.IO;
using System.Text;

namespace AudioWaveformView.Client
{
    // Simple PCM / IEEE-float RIFF WAVE reader for rendering an amplitude envelope.
    // Does not decode compressed WAV codecs. No network or camera dependency.
    internal static class WavPeakReader
    {
        public static double[] ReadPeaks(string path, int buckets = 600)
        {
            using (var fs = File.OpenRead(path))
            using (var br = new BinaryReader(fs, Encoding.ASCII))
            {
                if (new string(br.ReadChars(4)) != "RIFF")
                    throw new InvalidDataException("Not a RIFF WAV.");
                br.ReadUInt32();
                if (new string(br.ReadChars(4)) != "WAVE")
                    throw new InvalidDataException("Not a WAVE file.");
                ushort format = 0, channels = 0, bits = 0, blockAlign = 0;
                long dataStart = -1, dataBytes = 0;
                while (fs.Position + 8 <= fs.Length)
                {
                    string id = new string(br.ReadChars(4));
                    uint size = br.ReadUInt32();
                    long next = fs.Position + size + (size % 2);
                    if (next > fs.Length) break;
                    if (id == "fmt " && size >= 16)
                    {
                        format = br.ReadUInt16();
                        channels = br.ReadUInt16();
                        br.ReadUInt32(); // sample rate
                        br.ReadUInt32(); // bytes per second
                        blockAlign = br.ReadUInt16();
                        bits = br.ReadUInt16();
                        // WAVE_FORMAT_EXTENSIBLE uses PCM or float subformat GUID.
                        if (format == 0xFFFE && size >= 40)
                        {
                            br.ReadUInt16(); // cbSize
                            br.ReadUInt16(); // valid bits
                            br.ReadUInt32(); // channel mask
                            format = br.ReadUInt16(); // subformat GUID's first 2 bytes
                        }
                    }
                    if (id == "data") { dataStart = fs.Position; dataBytes = size; }
                    fs.Position = next;
                }
                if (dataStart < 0 || blockAlign == 0 || channels == 0 || bits == 0)
                    throw new InvalidDataException("Missing WAV data/format.");
                if (format != 1 && format != 3)
                    throw new NotSupportedException("Waveform requires PCM or IEEE-float WAV (format " + format + ").");
                if (!(bits == 8 || bits == 16 || bits == 24 || bits == 32))
                    throw new NotSupportedException("Unsupported WAV sample size: " + bits);
                long frames = dataBytes / blockAlign;
                var result = new double[buckets];
                fs.Position = dataStart;
                int bytesPerSample = bits / 8;
                for (long frame = 0; frame < frames; frame++)
                {
                    int bucket = (int)Math.Min(buckets - 1, (double)frame * buckets / Math.Max(1, frames));
                    double peak = 0;
                    for (int c = 0; c < channels; c++)
                    {
                        double value;
                        if (format == 3 && bits == 32)
                            value = Math.Abs(br.ReadSingle());
                        else if (bits == 8)
                            value = Math.Abs((br.ReadByte() - 128.0) / 128.0);
                        else if (bits == 16)
                            value = Math.Abs(br.ReadInt16() / 32768.0);
                        else if (bits == 24)
                        {
                            int a = br.ReadByte(), b = br.ReadByte(), d = br.ReadByte();
                            int sample = a | (b << 8) | (d << 16);
                            if ((sample & 0x800000) != 0) sample |= unchecked((int)0xFF000000);
                            value = Math.Abs(sample / 8388608.0);
                        }
                        else if (bits == 32)
                            value = Math.Abs(br.ReadInt32() / 2147483648.0);
                        else throw new NotSupportedException("Unsupported sample format.");
                        if (value > peak) peak = value;
                    }
                    if (peak > result[bucket]) result[bucket] = Math.Min(1.0, peak);
                    // Respect block alignment if WAV contains per-frame padding.
                    int consumed = channels * bytesPerSample;
                    if (blockAlign > consumed) fs.Position += blockAlign - consumed;
                }
                return result;
            }
        }
    }
}
