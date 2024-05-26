using FsBeatmapProcessor;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace osu_trainer
{
    public enum EditorState
    {
        NOT_READY,
        READY,
        GENERATING_BEATMAP
    }

    public enum BadBeatmapReason
    {
        NO_BEATMAP_LOADED,
        ERROR_LOADING_BEATMAP,
        EMPTY_MAP
    }

    public class BeatmapEditor
    {
        private MainForm mainform;
        public BadBeatmapReason NotReadyReason;

        public Beatmap OriginalBeatmap;
        public Beatmap NewBeatmap;

        public decimal StarRating { get; set; }
        public decimal AimRating { get; set; }
        public decimal SpeedRating { get; set; }
        private decimal lockedHP = 0M;
        private decimal lockedCS = 0M;
        private decimal lockedAR = 0M;
        private decimal lockedOD = 0M;
        private int lockedBpm = 200;

        private class ConcurrentRequest
        {
            private static int globalRequestCounter = -1;
            public int RequestNumber { get; set; }

            public ConcurrentRequest()
            {
                RequestNumber = ++globalRequestCounter;
            }
        }

        private class BeatmapRequest : ConcurrentRequest
        {
            public string Name { get; set; }

            public BeatmapRequest(string name) : base()
            {
                Name = name;
            }
        }

        private List<BeatmapRequest> mapChangeRequests = new List<BeatmapRequest>();
        private BeatmapRequest completedBeatmapRequest = null;
        private ConcurrentRequest completedDiffCalcRequest = null;
        private bool serviceBeatmapRequestLocked = false;  // mutex for serviceBeatmapChangeRequest()
        private bool serviceDiffCalcRequestLocked = false;  // mutex for serviceDiffCalcRequest()
        private List<ConcurrentRequest> diffCalcRequests = new List<ConcurrentRequest>();

        // User Profiles
        public UserProfile[] UserProfiles = {
            new UserProfile("Profile 1"),
            new UserProfile("Profile 2"),
            new UserProfile("Profile 3"),
            new UserProfile("Profile 4")};

        // public getters only
        // to set, call set methods
        public bool HpIsLocked { get; private set; } = false;

        public bool CsIsLocked { get; private set; } = false;
        public bool ArIsLocked { get; private set; } = false;
        public bool OdIsLocked { get; private set; } = false;
        public bool BpmIsLocked { get; private set; } = false;
        public bool ScaleAR { get; private set; } = true;
        public bool ScaleOD { get; private set; } = true;
        internal EditorState State { get; private set; }
        public decimal BpmRate { get; set; } = 1.0M;
        
        // Toggles
        public bool ForceHardrockCirclesize { get; private set; }
        public bool NoSpinners { get; private set; }
        public bool ChangePitch { get; private set; }
        public bool HighQualityMp3s { get; private set; }

        public BeatmapEditor(MainForm f)
        {
            mainform = f;

            // Load previously saved settings
            HpIsLocked = Properties.Settings.Default.HPLockedState;
            CsIsLocked = Properties.Settings.Default.CSLockedState;
            ArIsLocked = Properties.Settings.Default.ARLockedState;
            OdIsLocked = Properties.Settings.Default.ODLockedState;
            ScaleAR = ArIsLocked ? false : true;
            ScaleOD = OdIsLocked ? false : true;

            lockedHP = Properties.Settings.Default.LockedHPSetting;
            lockedCS = Properties.Settings.Default.LockedCSSetting;
            lockedAR = Properties.Settings.Default.LockedARSetting;
            lockedOD = Properties.Settings.Default.LockedODSetting;

            BpmIsLocked = Properties.Settings.Default.BpmLockedState;
            if (BpmIsLocked)
                lockedBpm = Properties.Settings.Default.LockedBpmSetting;
            else
                BpmRate = Properties.Settings.Default.BpmRate;

            ChangePitch = Properties.Settings.Default.ChangePitch;
            NoSpinners = Properties.Settings.Default.NoSpinners;
            HighQualityMp3s = Properties.Settings.Default.HighQualityMp3s;

            LoadProfilesFromDisk();

            SetState(EditorState.NOT_READY);
            NotReadyReason = BadBeatmapReason.NO_BEATMAP_LOADED;
        }

        public void SaveSettings()
        {
            Properties.Settings.Default.LockedHPSetting = lockedHP;
            Properties.Settings.Default.LockedCSSetting = lockedCS;
            Properties.Settings.Default.LockedARSetting = lockedAR;
            Properties.Settings.Default.LockedODSetting = lockedOD;

            Properties.Settings.Default.HPLockedState = HpIsLocked;
            Properties.Settings.Default.CSLockedState = CsIsLocked;
            Properties.Settings.Default.ARLockedState = ArIsLocked;
            Properties.Settings.Default.ODLockedState = OdIsLocked;

            Properties.Settings.Default.BpmLockedState = BpmIsLocked;
            if (BpmIsLocked)
                Properties.Settings.Default.LockedBpmSetting = lockedBpm;
            else
                Properties.Settings.Default.BpmRate = BpmRate;

            Properties.Settings.Default.ChangePitch = ChangePitch;
            Properties.Settings.Default.NoSpinners = NoSpinners;
            Properties.Settings.Default.HighQualityMp3s = HighQualityMp3s;
            Properties.Settings.Default.Save();
        }

        public event EventHandler StateChanged;

        public event EventHandler BeatmapSwitched;

        public event EventHandler BeatmapModified;

        public event EventHandler ControlsModified;

        public void ForceEventStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

        public void ForceEventBeatmapSwitched() => BeatmapSwitched?.Invoke(this, EventArgs.Empty);

        public void ForceEventBeatmapModified() => BeatmapModified?.Invoke(this, EventArgs.Empty);

        public void ForceEventControlsModified() => ControlsModified?.Invoke(this, EventArgs.Empty);

        private Beatmap BeatmapConstructorWrapper(string beatmapFilename)
        {
            Beatmap newBeatmap = new Beatmap(beatmapFilename);
            if (newBeatmap.ApproachRate == -1M)
            {
                newBeatmap.ApproachRate = newBeatmap.OverallDifficulty; // i can't believe this is how old maps used to work...
            }
            return newBeatmap;
        }
        public void GenerateBeatmap()
        {
            if (State != EditorState.READY)
                return;

            SetState(EditorState.GENERATING_BEATMAP);

            bool compensateForDT = (NewBeatmap.ApproachRate > 10 || NewBeatmap.OverallDifficulty > 10);

            // Set metadata
            Beatmap exportBeatmap = new Beatmap(NewBeatmap);
            ModifyBeatmapMetadata(exportBeatmap, BpmRate, ChangePitch, compensateForDT);

            // Slow down map by 1.5x
            if (compensateForDT)
            {
                exportBeatmap.ApproachRate      = DifficultyCalculator.CalculateMultipliedAR(NewBeatmap, 1 / 1.5M);
                exportBeatmap.OverallDifficulty = DifficultyCalculator.CalculateMultipliedOD(NewBeatmap, 1 / 1.5M);
                decimal compensatedRate = (NewBeatmap.Bpm / OriginalBeatmap.Bpm) / 1.5M;
                exportBeatmap.SetRate(compensatedRate);
            }

            // remove spinners
            if (NoSpinners)
                exportBeatmap.RemoveSpinners();

            // Generate new mp3
            var audioFilePath = Path.Combine(JunUtils.GetBeatmapDirectoryName(OriginalBeatmap), exportBeatmap.AudioFilename);
            var newMp3 = "";
            if (!File.Exists(audioFilePath))
            {
                string inFile = Path.Combine(Path.GetDirectoryName(OriginalBeatmap.Filename), OriginalBeatmap.AudioFilename);
                string outFile = exportBeatmap.AudioFilename;

                SongSpeedChanger.GenerateAudioFile(inFile, outFile, BpmRate, ChangePitch, compensateForDT, HighQualityMp3s);
                newMp3 = outFile;

                // take note of this mp3 in a text file so we can clean it up later
                string mp3ManifestFile = GetMp3ListFilePath();
                List<string> manifest = File.ReadAllLines(mp3ManifestFile).ToList();
                string beatmapFolder = Path.GetDirectoryName(exportBeatmap.Filename).Replace(Properties.Settings.Default.SongsFolder + "\\", "");
                string mp3RelativePath = Path.Combine(beatmapFolder, exportBeatmap.AudioFilename);
                manifest.Add(mp3RelativePath + " | " + exportBeatmap.Filename);
                File.WriteAllLines(mp3ManifestFile, manifest);
            }
            // save .osu to temp location (do not directly put into any song folder)
            exportBeatmap.Filename = Path.GetFileName(exportBeatmap.Filename);
            exportBeatmap.Save();

            // create and execute osz
            AddNewBeatmapToSongFolder(Path.GetDirectoryName(OriginalBeatmap.Filename), exportBeatmap.Filename, newMp3);

            // clean up .osu
            File.Delete(exportBeatmap.Filename);

            // post
            SetState(EditorState.READY);
        }

        private void AddNewBeatmapToSongFolder(string songFolder, string newBeatmapFile, string newMp3)
        {
            // 1. Create osz (just a regular zip file with file ext. renamed to .osz)
            string outputOsz = Path.GetFileNameWithoutExtension(songFolder) + ".osz";
            if (File.Exists(outputOsz))
                File.Delete(outputOsz);
            try
            {
                ZipFile.CreateFromDirectory(songFolder, outputOsz);
            }
            catch (Exception e)
            {
                MessageBox.Show($"Failed to create {outputOsz} {Environment.NewLine}{Environment.NewLine}{e.Message}", "Error");
            }
            // 2. Add new files to zip/osz
            using (ZipArchive archive = ZipFile.Open(outputOsz, ZipArchiveMode.Update))
            {
                archive.CreateEntryFromFile(newBeatmapFile, Path.GetFileName(newBeatmapFile));
                if (newMp3 != "")
                {
                    archive.CreateEntryFromFile(newMp3, Path.GetFileName(newMp3));
                    File.Delete(newMp3);
                }
            }
            // 3. Run the .osz
            Process proc = new Process();
            proc.StartInfo.FileName = outputOsz;
            proc.StartInfo.UseShellExecute = true;
            try
            {
                proc.Start();
            }
            catch
            {
                MessageBox.Show("There was an error opening the generated .osz file. This is probably because .osz files have not been configured to open with osu!.exe on this system." + Environment.NewLine + Environment.NewLine +
                    "To fix this, download any map from the website, right click the .osz file, click properties, beside Opens with... click Change..., and select osu!. " +
                    "You'll know the problem is fixed when you can double click .osz files to open them with osu!", "Error");
            }
        }

        private string MatchGroup(string text, string re, int group)
        {
            foreach (Match m in Regex.Matches(text, re))
                return m.Groups[group].Value;
            return "";
        }

        private string GetMp3ListFilePath()
        {
            string manifest = Path.Combine(Properties.Settings.Default.SongsFolder, "modified_mp3_list.txt");
            if (!File.Exists(manifest))
            {
                FileStream fs = File.Open(manifest, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
                fs.Close();
                fs.Dispose();
            }
            return manifest;
        }

        public List<string> GetUnusedMp3s()
        {
            // read manifest file
            List<string> lines = new List<string>();
            string mp3ManifestFile = GetMp3ListFilePath();

            if (!File.Exists(mp3ManifestFile))
                return new List<string>();

            using (var reader = File.OpenText(mp3ManifestFile))
            {
                string line = "";
                while ((line = reader.ReadLine()) != null)
                    lines.Add(line);
            }

            // convert that shit into a dictionary
            var mp3Dict = new Dictionary<string, List<string>>();
            string pattern = @"(.+) \| (.+)";
            string parseMp3(string line) => MatchGroup(line, pattern, 1);
            string parseOsu(string line) => MatchGroup(line, pattern, 2);

            // create dictionary keys
            lines
                .Select(line => parseMp3(line)).ToList()
                .Distinct()
                .ToList()
                .ForEach(mp3 => mp3Dict.Add(mp3, new List<string>()));

            // populate dictionary values
            foreach ((string mp3, string osu) in lines.Select(line => (parseMp3(line), parseOsu(line))))
                mp3Dict[mp3].Add(osu);

            // find all keys where none of the associated beatmaps exist, but the mp3 still exists
            bool noFilesExist(bool acc, string file) => acc && !File.Exists(file);
            return lines
                .Select(line => parseMp3(line))
                .Where(mp3 => mp3Dict[mp3].Aggregate(true, noFilesExist))
                .Where(mp3 => File.Exists(JunUtils.FullPathFromSongsFolder(mp3)))
                .ToList();
        }

        public void CleanUpManifestFile()
        {
            // read file
            string mp3ManifestFile = GetMp3ListFilePath();
            List<string> lines = File.ReadAllText(mp3ManifestFile).Split(new[] { Environment.NewLine }, StringSplitOptions.None).ToList();
            string pattern = @"(.+) \| (.+)";
            string parseMp3(string line) => MatchGroup(line, pattern, 1);

            // filter out lines whose mp3s no longer exist
            List<string> keepLines = new List<string>();
            foreach (string line in lines)
            {
                var relMp3 = parseMp3(line);
                var absMp3 = JunUtils.FullPathFromSongsFolder(relMp3);
                if (File.Exists(absMp3))
                    keepLines.Add(line);
            }

            // write to file
            File.WriteAllText(mp3ManifestFile, String.Join(Environment.NewLine, keepLines));
        }

        public void RequestBeatmapLoad(string beatmapPath)
        {
            mapChangeRequests.Add(new BeatmapRequest(beatmapPath));

            // acquire mutually exclusive entry into this method
            if (!serviceBeatmapRequestLocked)
                ServiceBeatmapChangeRequest();
            else return; // this method is already being run in another async "thread"
        }

        private async void ServiceBeatmapChangeRequest()
        {
            // acquire mutually exclusive entry into this method
            serviceBeatmapRequestLocked = true;

            Beatmap candidateOriginalBeatmap = null, candidateNewBeatmap = null;
            while (completedBeatmapRequest == null || completedBeatmapRequest.RequestNumber != mapChangeRequests.Last().RequestNumber)
            {
                completedBeatmapRequest = mapChangeRequests.Last();
                candidateOriginalBeatmap = await Task.Run(() => LoadBeatmap(mapChangeRequests.Last().Name));

                if (candidateOriginalBeatmap != null)
                {
                    candidateNewBeatmap = new Beatmap(candidateOriginalBeatmap);
                }

                // if a new request came in, invalidate candidate beatmap and service the new request
            }

            // no new requests, we can commit to using this beatmap
            OriginalBeatmap = candidateOriginalBeatmap;
            NewBeatmap = candidateNewBeatmap;
            if (OriginalBeatmap == null)
            {
                SetState(EditorState.NOT_READY);
                NotReadyReason = BadBeatmapReason.ERROR_LOADING_BEATMAP;
                BeatmapSwitched?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                if (OriginalBeatmap.HitObjectCount == 0)
                {
                    SetState(EditorState.NOT_READY);
                    NotReadyReason = BadBeatmapReason.EMPTY_MAP;
                    BeatmapSwitched?.Invoke(this, EventArgs.Empty);
                }
                else
                {
                    // Apply multiplier
                    if (BpmIsLocked)
                        SetBpm(lockedBpm);
                    NewBeatmap.SetRate(BpmRate);

                    // Apply bpm scaled settings
                    if (ScaleAR) NewBeatmap.ApproachRate = DifficultyCalculator.CalculateMultipliedAR(candidateOriginalBeatmap, BpmRate);
                    if (ScaleOD) NewBeatmap.OverallDifficulty = DifficultyCalculator.CalculateMultipliedOD(candidateOriginalBeatmap, BpmRate);

                    // Apply locked settings to new map
                    if (HpIsLocked) NewBeatmap.HPDrainRate = lockedHP;
                    if (CsIsLocked) NewBeatmap.CircleSize = lockedCS;
                    if (ArIsLocked)
                    {
                        NewBeatmap.ApproachRate = lockedAR;
                    }
                    if (OdIsLocked) NewBeatmap.OverallDifficulty = lockedOD;

                    // Apply Hardrock
                    if (ForceHardrockCirclesize) NewBeatmap.CircleSize = OriginalBeatmap.CircleSize * 1.3M;

                    SetState(EditorState.READY);
                    RequestDiffCalc();
                    BeatmapSwitched?.Invoke(this, EventArgs.Empty);
                    BeatmapModified?.Invoke(this, EventArgs.Empty);
                }
            }
            ControlsModified?.Invoke(this, EventArgs.Empty);
            serviceBeatmapRequestLocked = false;
        }

        public void RequestDiffCalc()
        {
            diffCalcRequests.Add(new ConcurrentRequest());

            // acquire mutually exclusive entry into this method
            if (!serviceDiffCalcRequestLocked)
                ServiceDiffCalcRequest();
            else return; // this method is already being run in another async "thread"
        }

        private async void ServiceDiffCalcRequest()
        {
            // acquire mutually exclusive entry into this method
            serviceDiffCalcRequestLocked = true;

            decimal stars = 0.0M, aim = 0.0M, speed = 0.0M;
            while (completedDiffCalcRequest == null || completedDiffCalcRequest.RequestNumber != diffCalcRequests.Last().RequestNumber)
            {
                completedDiffCalcRequest = diffCalcRequests.Last();
                try
                {
                    (stars, aim, speed) = await Task.Run(() => DifficultyCalculator.CalculateStarRating(NewBeatmap));
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                    Console.WriteLine("lol asdfasdf;lkjasdf");
                    (stars, aim, speed) = (0, 0, 0);
                }
                // if a new request came in, invalidate the diffcalc result and service the new request
            }
            // we serviced the last request, so we can commit to the diffcalc result
            StarRating = stars;
            AimRating = aim;
            SpeedRating = speed;
            BeatmapModified?.Invoke(this, EventArgs.Empty);

            serviceDiffCalcRequestLocked = false;
        }

        public (decimal, decimal, decimal) GetOriginalBpmData() => GetBpmData(OriginalBeatmap);

        public (decimal, decimal, decimal) GetNewBpmData() => GetBpmData(NewBeatmap);

        private (decimal, decimal, decimal) GetBpmData(Beatmap map) => (map?.Bpm ?? 0, map?.MinBpm ?? 0, map?.MaxBpm ?? 0);

        private void SetState(EditorState s)
        {
            State = s;
            Action action = () => StateChanged?.Invoke(this, EventArgs.Empty);

            if (mainform.InvokeRequired)
                mainform.Invoke(action);
            else
                action.Invoke();
        }

        public void SetHP(decimal value)
        {
            if (State != EditorState.READY)
                return;

            NewBeatmap.HPDrainRate = value;
            if (HpIsLocked)
                lockedHP = value;
            BeatmapModified?.Invoke(this, EventArgs.Empty);
        }

        public void SetCS(decimal value)
        {
            if (State != EditorState.READY)
                return;

            ForceHardrockCirclesize = false;

            NewBeatmap.CircleSize = value;
            if (CsIsLocked)
                lockedCS = value;
            RequestDiffCalc();
            BeatmapModified?.Invoke(this, EventArgs.Empty);
            ControlsModified?.Invoke(this, EventArgs.Empty);
        }

        public void SetAR(decimal value)
        {
            if (State != EditorState.READY)
                return;

            NewBeatmap.ApproachRate = value;
            if (ArIsLocked)
                lockedAR = value;

            ScaleAR = false;
            BeatmapModified?.Invoke(this, EventArgs.Empty);
            ControlsModified?.Invoke(this, EventArgs.Empty);
        }

        public void ToggleArLock()
        {
            ArIsLocked = !ArIsLocked;
            if (ArIsLocked)
            {
                ScaleAR = false;
                lockedAR = NewBeatmap.ApproachRate;
            }
            else
            {
                SetScaleAR(true);
            }
            ControlsModified?.Invoke(this, EventArgs.Empty);
        }

        public void SetScaleAR(bool value)
        {
            ScaleAR = value;

            if (State == EditorState.NOT_READY)
                return;

            // not applicable for taiko or mania
            if (ScaleAR && NewBeatmap.Mode != GameMode.Taiko && NewBeatmap.Mode != GameMode.Mania)
            {
                NewBeatmap.ApproachRate = DifficultyCalculator.CalculateMultipliedAR(OriginalBeatmap, BpmRate);
                BeatmapModified?.Invoke(this, EventArgs.Empty);
            }
            ArIsLocked = false;
            ControlsModified?.Invoke(this, EventArgs.Empty);
        }

        public void SetScaleOD(bool value)
        {
            ScaleOD = value;

            if (State == EditorState.NOT_READY)
                return;

            if (ScaleOD)
            {
                NewBeatmap.OverallDifficulty = DifficultyCalculator.CalculateMultipliedOD(OriginalBeatmap, BpmRate);
                BeatmapModified?.Invoke(this, EventArgs.Empty);
            }
            OdIsLocked = false;
            ControlsModified?.Invoke(this, EventArgs.Empty);
        }

        public void SetOD(decimal value)
        {
            if (State != EditorState.READY)
                return;

            ForceHardrockCirclesize = false;

            NewBeatmap.OverallDifficulty = value;
            if (OdIsLocked)
                lockedOD = value;

            ScaleOD = false;
            BeatmapModified?.Invoke(this, EventArgs.Empty);
            ControlsModified?.Invoke(this, EventArgs.Empty);
        }

        public void ToggleHpLock()
        {
            HpIsLocked = !HpIsLocked;
            if (HpIsLocked)
            {
                lockedHP = NewBeatmap.HPDrainRate;
            }
            else
            {
                NewBeatmap.HPDrainRate = OriginalBeatmap.HPDrainRate;
                BeatmapModified?.Invoke(this, EventArgs.Empty);
            }
            ControlsModified?.Invoke(this, EventArgs.Empty);
        }

        public void ToggleCsLock()
        {
            CsIsLocked = !CsIsLocked;
            ForceHardrockCirclesize = false;
            if (CsIsLocked)
            {
                lockedCS = NewBeatmap.CircleSize;
            }
            else
            {
                NewBeatmap.CircleSize = OriginalBeatmap.CircleSize;
                BeatmapModified?.Invoke(this, EventArgs.Empty);
            }
            ControlsModified?.Invoke(this, EventArgs.Empty);
        }

        public void ToggleBpmLock()
        {
            BpmIsLocked = !BpmIsLocked;
            if (BpmIsLocked && NewBeatmap != null)
            {
                lockedBpm = (int)NewBeatmap.Bpm;
                BeatmapModified?.Invoke(this, EventArgs.Empty);
            }
            ControlsModified?.Invoke(this, EventArgs.Empty);
        }

        public void ToggleOdLock()
        {
            OdIsLocked = !OdIsLocked;
            ForceHardrockCirclesize = false;
            if (OdIsLocked)
            {
                ScaleOD = false;
                lockedOD = NewBeatmap.OverallDifficulty;
            }
            else
            {
                SetScaleOD(true);
            }
            ControlsModified?.Invoke(this, EventArgs.Empty);
        }

        public void SetBpmMultiplier(decimal multiplier)
        {
            if (BpmIsLocked)
            {
                int bpm = (int)(OriginalBeatmap.Bpm * multiplier);
                lockedBpm = bpm;
            }
            ApplyBpmMultiplier(multiplier);
        }

        public void SetBpm(int bpm)
        {
            if (BpmIsLocked)
                lockedBpm = bpm;

            decimal originalBpm = GetOriginalBpmData().Item1;
            if (originalBpm == 0)
                return;

            decimal newMultiplier = bpm / originalBpm;
            newMultiplier = JunUtils.Clamp(newMultiplier, 0.1M, 5.0M);
            ApplyBpmMultiplier(newMultiplier);
        }

        private void ApplyBpmMultiplier(decimal multiplier)
        {
            if (BpmRate == multiplier)
                return;
            if (multiplier < 0.1M)
                BeatmapModified?.Invoke(this, EventArgs.Empty); // reject this value and revert view

            BpmRate = multiplier;

            // make no changes
            if (State == EditorState.NOT_READY)
                return;

            // scale AR (not applicable for taiko or mania)
            if (ScaleAR && !ArIsLocked && NewBeatmap.Mode != GameMode.Taiko && NewBeatmap.Mode != GameMode.Mania)
                NewBeatmap.ApproachRate = DifficultyCalculator.CalculateMultipliedAR(OriginalBeatmap, BpmRate);

            // scale OD
            if (ScaleOD && !OdIsLocked)
            {
                NewBeatmap.OverallDifficulty = GetScaledOD();
                if (ForceHardrockCirclesize)
                    NewBeatmap.OverallDifficulty = JunUtils.Clamp(GetScaledOD() * 1.4M, 0M, 10M);
            }

            // modify beatmap timing
            NewBeatmap.SetRate(BpmRate);

            RequestDiffCalc();
            BeatmapModified?.Invoke(this, EventArgs.Empty);
        }

        public void ToggleChangePitchSetting()
        {
            ChangePitch = !ChangePitch;
            ControlsModified?.Invoke(this, EventArgs.Empty);
        }

        public void ToggleNoSpinners()
        {
            NoSpinners = !NoSpinners;
            ControlsModified?.Invoke(this, EventArgs.Empty);
            BeatmapModified?.Invoke(this, EventArgs.Empty);
        }
        internal void ToggleHrEmulation()
        {
            ForceHardrockCirclesize = !ForceHardrockCirclesize;
            CsIsLocked = false;
            if (ForceHardrockCirclesize)
            {
                NewBeatmap.CircleSize = OriginalBeatmap.CircleSize * 1.3M;
            }
            else
            {
                NewBeatmap.CircleSize = OriginalBeatmap.CircleSize;
            }
            RequestDiffCalc();
            ControlsModified?.Invoke(this, EventArgs.Empty);
            BeatmapModified?.Invoke(this, EventArgs.Empty);
        }

        public GameMode? GetMode()
        {
            return OriginalBeatmap?.Mode;
        }

        public decimal GetScaledAR() => DifficultyCalculator.CalculateMultipliedAR(OriginalBeatmap, BpmRate);

        public decimal GetScaledOD() => DifficultyCalculator.CalculateMultipliedOD(OriginalBeatmap, BpmRate);

        public bool NewMapIsDifferent()
        {
            return (
                NewBeatmap.HPDrainRate != OriginalBeatmap.HPDrainRate ||
                NewBeatmap.CircleSize != OriginalBeatmap.CircleSize ||
                NewBeatmap.ApproachRate != OriginalBeatmap.ApproachRate ||
                NewBeatmap.OverallDifficulty != OriginalBeatmap.OverallDifficulty ||
                Math.Abs(BpmRate - 1.0M) > 0.001M ||
                NoSpinners
            );
        }

        // return the new beatmap object if success
        // return null on failure
        private Beatmap LoadBeatmap(string beatmapPath)
        {
            // test if the beatmap is valid before committing to using it
            Beatmap retMap;
            try
            {
                retMap = BeatmapConstructorWrapper(beatmapPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"An error occurred while reading {beatmapPath}");
                Console.WriteLine("Exception Type: " + ex.GetType().Name);
                Console.WriteLine("Message: " + ex.Message);
                Console.WriteLine("Stack Trace: " + ex.StackTrace);

                OriginalBeatmap = null;
                NewBeatmap = null;
                return null;
            }
            // Check if beatmap was loaded successfully
            if (!retMap.Valid || retMap.Filename == null || retMap.Title == null)
            {
                Console.WriteLine("Bad .osu file format");
                return null;
            }

            // Check if this map was generated by osu-trainer
            if (retMap.Tags.Contains("osutrainer"))
            {
                // Try to find original unmodified version
                foreach (string diff in Directory.GetFiles(Path.GetDirectoryName(retMap.Filename), "*.osu"))
                {
                    Beatmap map = BeatmapConstructorWrapper(diff);
                    // FIXME: Earlier beatmaps will not contain a beatmap ID tag, and BeatmapID will be 0.
                    // What is the method for determining the original diff in this case?
                    if (!map.Tags.Contains("osutrainer") && map.BeatmapID == retMap.BeatmapID)
                    {
                        retMap = map;
                        break;
                    }
                }
            }

            return retMap;
        }

        // OUT: beatmap.Version
        // OUT: beatmap.Filename
        // OUT: beatmap.AudioFilename
        // OUT: beatmap.Tags
        private void ModifyBeatmapMetadata(Beatmap map, decimal multiplier, bool changePitch = false, bool preDT = false)
        {
            // Difficulty Name and AudioFilename
            if (preDT)
            {
                string bpm = map.Bpm.ToString("0");
                map.Version += $" {multiplier:0.##}x ({bpm}bpm)";
                map.AudioFilename = $"{Path.GetFileNameWithoutExtension(map.AudioFilename)} {multiplier:0.000}x withDT";
                if (changePitch && Math.Abs(multiplier - 1M) > 0.001M)
                    map.AudioFilename += $" (pitch {(multiplier < 1 ? "lowered" : "raised")})";
                map.AudioFilename += ".mp3";
            }
            else if (Math.Abs(multiplier - 1M) > 0.001M)
            {
                string bpm = map.Bpm.ToString("0");
                map.Version += $" {multiplier:0.##}x ({bpm}bpm)";
                map.AudioFilename = $"{Path.GetFileNameWithoutExtension(map.AudioFilename)} {multiplier:0.000}x";
                if (changePitch)
                    map.AudioFilename += $" (pitch {(multiplier < 1 ? "lowered" : "raised")})";
                map.AudioFilename += ".mp3";
            }
            map.AudioFilename = map.AudioFilename.Trim();

            // Difficulty Name - Difficulty Settings
            string HPCSAROD = "";
            if (NewBeatmap.HPDrainRate != OriginalBeatmap.HPDrainRate)
                HPCSAROD += $" HP{NewBeatmap.HPDrainRate:0.#}";

            if (   OriginalBeatmap.Mode != GameMode.Taiko
                && OriginalBeatmap.Mode != GameMode.Mania
                && NewBeatmap.CircleSize != OriginalBeatmap.CircleSize)
                HPCSAROD += $" CS{NewBeatmap.CircleSize:0.#}";

            if (   OriginalBeatmap.Mode != GameMode.Taiko
                && OriginalBeatmap.Mode != GameMode.Mania
                && (NewBeatmap.ApproachRate != GetScaledAR() || NewBeatmap.ApproachRate > 10M))
                HPCSAROD += $" AR{NewBeatmap.ApproachRate:0.#}";

            if (NewBeatmap.OverallDifficulty != GetScaledOD() || NewBeatmap.OverallDifficulty > 10M)
                HPCSAROD += $" OD{NewBeatmap.OverallDifficulty:0.#}";

            map.Version += HPCSAROD;

            //if (NoSpinners)
            //    map.Version += " nospin";

            // Beatmap File Name
            string artist  = JunUtils.NormalizeText(map.Artist);
            string title   = JunUtils.NormalizeText(map.Title);
            string creator = JunUtils.NormalizeText(map.Creator);
            string diff    = JunUtils.NormalizeText(map.Version);
            map.Filename   = Path.GetDirectoryName(map.Filename) + $"\\{artist} - {title} ({creator}) [{diff}].osu";

            // make this map searchable in the in-game menus
            var TagsWithOsutrainer = map.Tags;
            TagsWithOsutrainer.Add("osutrainer");
            map.Tags = TagsWithOsutrainer; // need to assign like this because Tags is an immutable list
        }

        // dominant, min, max

        public void ResetBeatmap()
        {
            if (State != EditorState.READY)
                return;
            NewBeatmap.HPDrainRate = OriginalBeatmap.HPDrainRate;
            NewBeatmap.CircleSize = OriginalBeatmap.CircleSize;
            NewBeatmap.ApproachRate = OriginalBeatmap.ApproachRate;
            NewBeatmap.OverallDifficulty = OriginalBeatmap.OverallDifficulty;
            HpIsLocked = false;
            CsIsLocked = false;
            ArIsLocked = false;
            OdIsLocked = false;
            BpmIsLocked = false;
            ForceHardrockCirclesize = false;
            ScaleAR = true;
            ScaleOD = true;
            BpmRate = 1.0M;
            NewBeatmap.SetRate(1.0M);
            RequestDiffCalc();
            ControlsModified?.Invoke(this, EventArgs.Empty);
            BeatmapModified?.Invoke(this, EventArgs.Empty);
        }

#region User Profile Management
        const string UserProfilesFile = "userprofiles.txt";
        public void SaveProfilesToDisk()
        {
            try
            {
                using (var writer = new StreamWriter(UserProfilesFile, false))
                {
                    foreach (var profile in UserProfiles)
                    {
                        writer.WriteLine(profile.Name);
                        writer.WriteLine(profile.HpIsLocked);
                        writer.WriteLine(profile.CsIsLocked);
                        writer.WriteLine(profile.ArIsLocked);
                        writer.WriteLine(profile.OdIsLocked);
                        writer.WriteLine(profile.lockedHP);
                        writer.WriteLine(profile.lockedCS);
                        writer.WriteLine(profile.lockedAR);
                        writer.WriteLine(profile.lockedOD);
                        writer.WriteLine(profile.ScaleAR);
                        writer.WriteLine(profile.ScaleOD);
                        writer.WriteLine(profile.ForceHardrockCirclesize);
                        writer.WriteLine(profile.ChangePitch);
                        writer.WriteLine(profile.NoSpinners);
                        writer.WriteLine(profile.BpmIsLocked);
                        writer.WriteLine(profile.lockedBpm);
                        writer.WriteLine(profile.BpmMultiplier);
                    }
                }
            }
            catch (UnauthorizedAccessException)
            {
                MessageBox.Show("Permission to write to version.txt was denied. Check your antivirus, or try running osu trainer with administrator priveledges.");
            }
            catch (Exception e)
            {
                MessageBox.Show("Unable to save profiles." + Environment.NewLine + Environment.NewLine + e.Message);
            }
        }
        public void LoadProfilesFromDisk()
        {
            if (!File.Exists(UserProfilesFile))
                return;
            try
            {
                var lines = File.ReadAllLines(UserProfilesFile);
                for (int i = 0; i < UserProfiles.Length; i++)
                {
                    int offset = i * 17;
                    UserProfiles[i].Name = lines[offset + 0];
                    UserProfiles[i].HpIsLocked = lines[offset + 1] == "True";
                    UserProfiles[i].CsIsLocked = lines[offset + 2] == "True";
                    UserProfiles[i].ArIsLocked = lines[offset + 3] == "True";
                    UserProfiles[i].OdIsLocked = lines[offset + 4] == "True";
                    UserProfiles[i].lockedHP = decimal.Parse(lines[offset + 5]);
                    UserProfiles[i].lockedCS = decimal.Parse(lines[offset + 6]);
                    UserProfiles[i].lockedAR = decimal.Parse(lines[offset + 7]);
                    UserProfiles[i].lockedOD = decimal.Parse(lines[offset + 8]);
                    UserProfiles[i].ScaleAR = lines[offset + 9] == "True";
                    UserProfiles[i].ScaleOD = lines[offset + 10] == "True";
                    UserProfiles[i].ForceHardrockCirclesize = lines[offset + 11] == "True";
                    UserProfiles[i].ChangePitch = lines[offset + 12] == "True";
                    UserProfiles[i].NoSpinners = lines[offset + 13] == "True";
                    UserProfiles[i].BpmIsLocked = lines[offset + 14] == "True";
                    UserProfiles[i].lockedBpm = int.Parse(lines[offset + 15]);
                    UserProfiles[i].BpmMultiplier = decimal.Parse(lines[offset + 16]);
                }
            }
            catch
            {
                // load empty profiles
                for (int i = 0; i < UserProfiles.Length; i++)
                    UserProfiles[i] = new UserProfile($"Profile {i + 1}");
            }
        }
        public void SaveProfile(int whichProfile)
        {
            int i = whichProfile;
            UserProfiles[i].HpIsLocked = HpIsLocked;
            UserProfiles[i].CsIsLocked = CsIsLocked;
            UserProfiles[i].ArIsLocked = ArIsLocked;
            UserProfiles[i].OdIsLocked = OdIsLocked;
            UserProfiles[i].lockedHP = lockedHP;
            UserProfiles[i].lockedCS = lockedCS;
            UserProfiles[i].lockedAR = lockedAR;
            UserProfiles[i].lockedOD = lockedOD;
            // If HP/CS/AR/OD has been changed, save it as locked regardless of locked state
            // After testing this is just the way I expected it to work
            // However, if scale AR/OD is selected, locked setting must be off
            if (NewBeatmap.HPDrainRate != OriginalBeatmap.HPDrainRate)
            {
                UserProfiles[i].HpIsLocked = true;
                UserProfiles[i].lockedHP = NewBeatmap.HPDrainRate;
            }
            if (NewBeatmap.CircleSize != OriginalBeatmap.CircleSize)
            {
                UserProfiles[i].CsIsLocked = true;
                UserProfiles[i].lockedCS = NewBeatmap.CircleSize;
            }
            if (ScaleAR)
            {
                UserProfiles[i].ScaleAR = true;
                UserProfiles[i].ArIsLocked = false;
            } else if (NewBeatmap.ApproachRate != OriginalBeatmap.ApproachRate)
            {
                UserProfiles[i].ScaleAR = false;
                UserProfiles[i].ArIsLocked = true;
                UserProfiles[i].lockedAR = NewBeatmap.ApproachRate;
            }
            if (ScaleOD)
            {
                UserProfiles[i].ScaleOD = true;
                UserProfiles[i].OdIsLocked = false;
            } else if (NewBeatmap.OverallDifficulty != OriginalBeatmap.OverallDifficulty)
            {
                UserProfiles[i].ScaleOD = false;
                UserProfiles[i].OdIsLocked = true;
                UserProfiles[i].lockedOD = NewBeatmap.OverallDifficulty;
            }

            UserProfiles[i].ForceHardrockCirclesize = ForceHardrockCirclesize;
            UserProfiles[i].ChangePitch = ChangePitch;
            UserProfiles[i].NoSpinners = NoSpinners;

            UserProfiles[i].BpmIsLocked = BpmIsLocked;
            UserProfiles[i].lockedBpm = lockedBpm;
            UserProfiles[i].BpmMultiplier = BpmRate;
        }
        public void RenameProfile(int whichProfile, string name)
        {
            UserProfiles[whichProfile].Name = name;
            ControlsModified?.Invoke(this, EventArgs.Empty);
        }
        public void LoadProfile(int whichProfile)
        {
            int i = whichProfile;

            // locked settings:
            if (UserProfiles[i].HpIsLocked)
            {
                HpIsLocked = true;
                lockedHP = UserProfiles[i].lockedHP;
                NewBeatmap.HPDrainRate = UserProfiles[i].lockedHP;
            }
            else
            {
                HpIsLocked = false;
                NewBeatmap.HPDrainRate = OriginalBeatmap.HPDrainRate;
            }
            if (UserProfiles[i].CsIsLocked)
            {
                CsIsLocked = true;
                lockedCS = UserProfiles[i].lockedCS;
                NewBeatmap.CircleSize = UserProfiles[i].lockedCS;
            }
            else
            {
                CsIsLocked = false;
                NewBeatmap.CircleSize = OriginalBeatmap.CircleSize;
            }
            if (UserProfiles[i].ArIsLocked)
            {
                ArIsLocked = true;
                lockedAR = UserProfiles[i].lockedAR;
                NewBeatmap.ApproachRate = UserProfiles[i].lockedAR;
            }
            else
            {
                ArIsLocked = false;
                NewBeatmap.ApproachRate = OriginalBeatmap.ApproachRate;
            }
            if (UserProfiles[i].OdIsLocked)
            {
                OdIsLocked = true;
                lockedOD = UserProfiles[i].lockedOD;
                NewBeatmap.OverallDifficulty = UserProfiles[i].lockedOD;
            }
            else
            {
                OdIsLocked = false;
                NewBeatmap.OverallDifficulty = OriginalBeatmap.OverallDifficulty;
            }

            ForceHardrockCirclesize = UserProfiles[i].ForceHardrockCirclesize;
            if (ForceHardrockCirclesize) NewBeatmap.CircleSize = OriginalBeatmap.CircleSize * 1.3M;
            ChangePitch = UserProfiles[i].ChangePitch;
            NoSpinners = UserProfiles[i].NoSpinners;

            if (UserProfiles[i].BpmIsLocked)
            {
                // Load Exact BPM
                BpmIsLocked = true;
                SetBpm(UserProfiles[i].lockedBpm);
            }
            else
            {
                // Load BPM Multipier
                BpmIsLocked = false;
                ApplyBpmMultiplier(UserProfiles[i].BpmMultiplier);
            }

            // Scaled settings
            ScaleAR = UserProfiles[i].ScaleAR;
            ScaleOD = UserProfiles[i].ScaleOD;
            if (ScaleAR) NewBeatmap.ApproachRate = DifficultyCalculator.CalculateMultipliedAR(OriginalBeatmap, BpmRate);
            if (ScaleOD) NewBeatmap.OverallDifficulty = DifficultyCalculator.CalculateMultipliedOD(OriginalBeatmap, BpmRate);

            RequestDiffCalc();
            BeatmapModified?.Invoke(this, EventArgs.Empty);
            ControlsModified?.Invoke(this, EventArgs.Empty);
        }

        internal void ToggleHighQualityMp3s()
        {
            HighQualityMp3s = !HighQualityMp3s;
            ControlsModified?.Invoke(this, EventArgs.Empty);
        }

#endregion
    }
}
