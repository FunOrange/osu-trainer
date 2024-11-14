using System;
using System.Diagnostics;
using System.IO;
using NAudio.Wave;
using NAudio.Lame;
using NAudio.Vorbis;
using FFMpegCore;
using FFMpegCore.Enums;

namespace osu_trainer
{
    internal class SongSpeedChanger
    {
        public static void GenerateAudioFile(string inFile, string outFile, decimal effectiveMultiplier, bool changePitch = false, bool preDT = false, bool highQuality = false)
        {
            decimal DTCompensatedMultiplier = effectiveMultiplier / 1.5M;

            string ext = Path.GetExtension(inFile);
            string temp1 = Path.Combine(Guid.NewGuid().ToString() + ext); // audio copy
            string temp2 = Path.Combine(Guid.NewGuid().ToString() + ".wav"); // decoded wav
            string temp3 = Path.Combine(Guid.NewGuid().ToString() + ".wav"); // stretched file

            File.Copy(inFile, temp1);

            // proper determination of file formats (at least more proper then file extensions
            
            uint OggHeader = 0x4F676753; // <- Required Ogg Header
            uint Mp3Header = 0x49443303; // <- Rare MP3 Header

            uint BigHeader, LittleHeader;
            using (var reader = new BinaryReader(File.Open(temp1, FileMode.Open, FileAccess.Read)))
            {
                byte[] bytes = reader.ReadBytes(4);

                BigHeader = BitConverter.ToUInt32(bytes,0);

                Array.Reverse(bytes);
                LittleHeader = BitConverter.ToUInt32(bytes,0);

            }

            if (BigHeader == OggHeader || LittleHeader == OggHeader)
            {
                using (VorbisWaveReader vorbis = new VorbisWaveReader(temp1))
                    WaveFileWriter.CreateWaveFile(temp2, vorbis.ToWaveProvider16());
            }
            // this is useless kinda
            else if (BigHeader == Mp3Header || LittleHeader == Mp3Header)
            {
                using (Mp3FileReader mp3 = new Mp3FileReader(temp1))
                using (WaveStream wav = WaveFormatConversionStream.CreatePcmStream(mp3))
                    WaveFileWriter.CreateWaveFile(temp2, wav);
            }
            else
            {
                try
                {
                    using (Mp3FileReader mp3 = new Mp3FileReader(temp1))
                    using (WaveStream wav = WaveFormatConversionStream.CreatePcmStream(mp3))
                        WaveFileWriter.CreateWaveFile(temp2, wav);
                }
                catch
                {
                    throw new Exception($"audio file not supported: {ext}");
                }
            }
            

            // stretch (or speed up) wav
            string quick = highQuality ? "" : "-quick";
            string naa = highQuality ? "" : "-naa";

            decimal multiplier = preDT ? DTCompensatedMultiplier : effectiveMultiplier;
            string tempo = $"-tempo={(multiplier - 1) * 100}";

            decimal cents = (decimal)(1200.0 * Math.Log((double)effectiveMultiplier) / Math.Log(2));
            decimal semitones = cents / 100.0M;
            string pitch = changePitch ? $"-pitch={semitones}" : "";

            Process soundstretch = new Process();
            soundstretch.StartInfo.FileName = Path.Combine("binaries", "soundstretch.exe");
            soundstretch.StartInfo.Arguments = $"\"{temp2}\" \"{temp3}\" {quick} {naa} {tempo} {pitch}";
            Console.WriteLine(soundstretch.StartInfo.Arguments);
            soundstretch.StartInfo.UseShellExecute = false;
            soundstretch.StartInfo.CreateNoWindow = true;
            soundstretch.Start();
            soundstretch.WaitForExit();


            // wav => ogg for better timing alignment and preservation of quality (might create bloat but its w/e)
            FFMpegArguments
                .FromFileInput(temp3)
                .OutputToFile(outFile, true, options => options
                    .WithAudioCodec(AudioCodec.LibVorbis)
                    .WithAudioSamplingRate(44100)
                    .WithAudioBitrate(highQuality ? AudioQuality.Good : AudioQuality.Normal) // 128kbps <> 192kbps
                    )
                .ProcessSynchronously();

            // Clean up
            try
            {
                File.Delete(temp1);
                File.Delete(temp2);
                File.Delete(temp3);
            }
            catch { } // don't shit the bed if we can't delete temp files
        }
    }
}