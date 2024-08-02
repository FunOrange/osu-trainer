using FsBeatmapProcessor;
using Newtonsoft.Json.Linq;
using osu_trainer.ODCalculators;
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace osu_trainer
{
    class DifficultyCalculator
    {
        static string tempBeatmapPath = "temp.osu";
        static Semaphore diffCalcInProgress;


        static DifficultyCalculator()
        {
            diffCalcInProgress = new Semaphore(1, 1);
        }

        // AR modifications: HR, DT, DTHR, BpmMultiplier
        public static decimal CalculateMultipliedAR(Beatmap map, decimal BpmMultiplier)
        {
            decimal newbpmMs = ApproachRateToMs(map.ApproachRate) / BpmMultiplier;
            decimal newbpmAR = MsToApproachRate(newbpmMs);
            return JunUtils.Clamp(newbpmAR, 0, 11);
        }
        private static decimal ApproachRateToMs(decimal approachRate)
        {
            if (approachRate <= 5)
            {
                return 1800.0M - approachRate * 120.0M;
            }
            else
            {
                decimal remainder = approachRate - 5;
                return 1200.0M - remainder * 150.0M;
            }
        }
        private static decimal MsToApproachRate(decimal ms)
        {
            decimal smallestDiff = 100000.0M; // initial value
            for (int AR = 0; AR <= 110; AR++)
            {
                var newDiff = Math.Abs(ApproachRateToMs(AR/10.0M) - ms);
                if (newDiff < smallestDiff)
                    smallestDiff = newDiff;
                else
                    return (AR - 1) / 10.0M;
            }
            return 300;
        }
        public static decimal CalculateMultipliedOD(Beatmap map, decimal BpmMultiplier)
        {
            decimal newbpmMs = OverallDifficultyToMs(map.OverallDifficulty, map.Mode)/ BpmMultiplier;
            decimal newbpmOD = MsToOverallDifficulty(newbpmMs, map.Mode);
            newbpmOD = (decimal)Math.Round(newbpmOD * 10.0M) / 10.0M;
            newbpmOD = JunUtils.Clamp(newbpmOD, 0, map.Mode == GameMode.Mania ? 10 : 11);
            return newbpmOD;
        }
        private static decimal OverallDifficultyToMs(decimal od, GameMode mode)
        {
            ODCalculator odCalculator = ODCalculator.GetCalculatorForGamemode(mode);
            return odCalculator.OverallDifficultyToMs(od);
        }



        private static decimal MsToOverallDifficulty(decimal ms, GameMode mode)
        {

            ODCalculator odCalculator = ODCalculator.GetCalculatorForGamemode(mode);
            return odCalculator.MsToOverallDifficulty(ms);
        }

        public static (decimal, decimal, decimal) CalculateStarRating(Beatmap map)
        {
            if (map == null)
                throw new NullReferenceException();
            diffCalcInProgress.WaitOne();
            if (map == null)
            {
                diffCalcInProgress.Release();
                throw new NullReferenceException();
            }
            map.Save(tempBeatmapPath);
            Process oppai = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = Path.Combine("binaries", "oppai.exe"),
                    Arguments = $"\"{tempBeatmapPath}\" -ojson",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8
                }
            };
            oppai.Start();
            string oppaiOutput = oppai.StandardOutput.ReadToEnd();
            oppai.WaitForExit();
            File.Delete(tempBeatmapPath);
            diffCalcInProgress.Release();

            // json parsing
            JObject oppaiData;
            try
            {
                oppaiData = JObject.Parse(oppaiOutput);
            }
            catch
            {
                return (0, 0, 0);
            }
            string errstr = oppaiData.GetValue("errstr").ToObject<string>();
            if (errstr != "no error")
            {
                // TODO: An error occurs when opening a non-osu!standard map (mania, taiko, etc)
                Console.WriteLine("Could not calculate difficulty");
                return (0, 0, 0);
            }
            decimal stars = oppaiData.GetValue("stars").ToObject<decimal>();
            decimal aimStars = oppaiData.GetValue("aim_stars").ToObject<decimal>();
            decimal speedStars = oppaiData.GetValue("speed_stars").ToObject<decimal>();


            return (stars, aimStars, speedStars);
        }
    }
}