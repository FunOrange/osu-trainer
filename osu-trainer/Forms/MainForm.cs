using FsBeatmapProcessor;
using osu_trainer.Controls;
using osu_trainer.Forms;
using OsuMemoryDataProvider;
using OsuMemoryDataProvider.OsuMemoryModels.Direct;
using ProcessMemoryDataFinder.API;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Text;
using System.IO;
using System.Linq;
using System.Media;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Utilities;

namespace osu_trainer
{
    public partial class MainForm : Form
    {
        // Beatmap
        private string userSongsFolder = null;

        private StructuredOsuMemoryReader osuReader;

        // Common Control Lists
        private List<Label> dumbLabels;
        private List<TextBox> diffDisplays;
        private List<OptionSlider> diffSliders;
        private List<OsuCheckBox> checkControls;

        // Hotkeys
        private globalKeyboardHook kbhook;
        private bool hooked = true;
        private List<Keys> Hotkeys;

        // Single object instances
        private readonly SoundPlayer sound = new SoundPlayer();
        private readonly BeatmapEditor editor;

        // other
        private string previousBeatmapRead;

        private bool? gameLoaded = null;
        private bool mapSelectScreen = false;

        public MainForm()
        {
            Directory.SetCurrentDirectory(Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location));
            InitializeComponent();
            Height = 493;

            // Read version number from version.txt
            try
            {
                string[] lines = File.ReadAllLines("version.txt");
                foreach (string line in lines)
                {
                    string attribute = line.Split(':')[0].Trim();
                    string value = line.Split(':')[1].Trim();
                    if (attribute == "current version")
                    {
                        this.Text = $"osu trainer {value}"; // set window title
                    }
                }
            }
            catch {}

            // init control lists
            dumbLabels = new List<Label>
            {
                hplabel, cslabel, arlabel, odlabel,
                BpmMultiplierLabel, OriginalBpmLabel, NewBpmLabel
            };
            diffDisplays = new List<TextBox>
            {
                HPDisplay, CSDisplay, ARDisplay, ODDisplay
            };
            diffSliders = new List<OptionSlider>
            {
                HPSlider, CSSlider, ARSlider, ODSlider
            };
            checkControls = new List<OsuCheckBox>
            {
                NoSpinnersCheck, HRCheck, ScaleARCheck, ScaleODCheck, ChangePitchCheck, highQualityMp3Check
            };

            ApplyFonts();

            // load user settings
            if (Directory.Exists(Properties.Settings.Default.SongsFolder))
                userSongsFolder = Properties.Settings.Default.SongsFolder;

            // load hotkeys
            Hotkeys = new List<Keys>();
            Hotkeys.Add(Properties.Settings.Default.HotkeyCreateMap);
            Hotkeys.Add(Properties.Settings.Default.HotkeyProfile1);
            Hotkeys.Add(Properties.Settings.Default.HotkeyProfile2);
            Hotkeys.Add(Properties.Settings.Default.HotkeyProfile3);
            Hotkeys.Add(Properties.Settings.Default.HotkeyProfile4);

            // Init object instances
            osuReader = new StructuredOsuMemoryReader();
            editor = new BeatmapEditor(this);

            // Add event handlers (observers)
            editor.StateChanged += ToggleDumbLabels;
            editor.StateChanged += TogglePrettyButtons;
            editor.StateChanged += ToggleHpCsArOdDisplay;
            editor.StateChanged += ToggleBpmInputControls;
            editor.StateChanged += ToggleBpmDisplay;
            editor.StateChanged += ToggleLockButtons;
            editor.StateChanged += UpdateProfiles;
            middlePanel.MaximumSize = new Size(999, 999);
            BottomPanel.MaximumSize = new Size(999, 999);
            editor.StateChanged += RearrangeLayout;
            editor.BeatmapSwitched += UpdateSongDisplay;
            editor.BeatmapModified += UpdateBpmDisplay;
            editor.BeatmapModified += UpdateHpCsArOdDisplay;
            editor.BeatmapModified += UpdateDifficultyDisplay;
            editor.BeatmapModified += TogglePrettyButtons;
            editor.BeatmapModified += UpdateRateInputControls;
            editor.ControlsModified += UpdateLockButtons;
            editor.ControlsModified += UpdateCheckBoxes;
            editor.ControlsModified += UpdateProfiles;

            // Install keyboard hooks
            // Notice: The only purpose of the keyboard hook is for shortcuts. Nothing else!
            ApplyHotkeys();

            // need controls to show up as initially disabled
            editor.ForceEventStateChanged();
            editor.ForceEventBeatmapSwitched();
            editor.ForceEventControlsModified();

            BeatmapUpdateTimer.Start();
            OsuRunningTimer.Start();
            formAnimationTimer.Start();

            Focus();
        }

        private void ApplyHotkeys()
        {
            // restart keyboard hook with new hotkeys
            if (kbhook != null)
                kbhook.unhook();
            kbhook = new globalKeyboardHook();
            kbhook.HookedKeys = Hotkeys;
            kbhook.KeyDown += new KeyEventHandler(CreateMapHotkeyHandler);
            kbhook.KeyDown += new KeyEventHandler(ProfileHotkeyHandler);
            GenerateMapButton.Subtext = $"Ctrl+Shift+{Hotkeys[0]}";
        }

        private void ApplyFonts()
        {
            var comforta = Program.FontCollection.Families[0];

            foreach (var textbox in diffDisplays)
                textbox.Font = new Font(comforta, 11, FontStyle.Bold);
            foreach (var label in dumbLabels)
                label.Font = new Font(comforta, 10, FontStyle.Bold);
            foreach (var check in checkControls)
                check.Font = new Font(comforta, 9, FontStyle.Bold);
            updatesCheck.Font = new Font(comforta, 9, FontStyle.Bold);

            BpmMultiplierTextBox.Font = new Font(comforta, 11, FontStyle.Bold);
            OriginalBpmTextBox.Font = new Font(comforta, 10, FontStyle.Bold);
            NewBpmTextBox.Font = new Font(comforta, 10, FontStyle.Bold);

            OriginalBpmRangeTextBox.Font = new Font(comforta, 9, FontStyle.Bold);
            NewBpmRangeTextBox.Font = new Font(comforta, 9, FontStyle.Bold);
        }

#region Callbacks for updating GUI controls

        private void UpdateRateInputControls(object sender, EventArgs e)
        {
            BpmMultiplierTextBox.Text = editor.BpmRate.ToString("0.00");
        }

        private void UpdateSongDisplay(object sender, EventArgs e)
        {
            switch (editor.State)
            {
                case EditorState.NOT_READY:
                    switch (editor.NotReadyReason)
                    {
                        case BadBeatmapReason.NO_BEATMAP_LOADED:
                            break;

                        case BadBeatmapReason.ERROR_LOADING_BEATMAP:
                            SongDisplay.Artist = "failed to load beatmap";
                            SongDisplay.Title = "";
                            SongDisplay.Difficulty = "";
                            SongDisplay.Cover = null;
                            break;

                        case BadBeatmapReason.EMPTY_MAP:
                            SongDisplay.Artist = editor.OriginalBeatmap.Artist;
                            SongDisplay.Title = editor.OriginalBeatmap.Title;
                            SongDisplay.Difficulty = "empty map";
                            SongDisplay.Cover = GetSongBackground(editor.NewBeatmap);
                            break;

                        default:
                            break;
                    }
                    break;

                case EditorState.READY:
                case EditorState.GENERATING_BEATMAP:
                    SongDisplay.Artist = editor.OriginalBeatmap.Artist;
                    SongDisplay.Title = editor.OriginalBeatmap.Title;
                    SongDisplay.Difficulty = editor.OriginalBeatmap.Version;
                    SongDisplay.Cover = GetSongBackground(editor.NewBeatmap);
                    break;
            }
        }

        private Image GetSongBackground(Beatmap beatmap)
        {
            if (string.IsNullOrWhiteSpace(beatmap.Background))
                return Properties.Resources.nobg;

            string imageAbsolutePath = Path.Combine(Path.GetDirectoryName(beatmap.Filename), beatmap.Background);
            if (!File.Exists(imageAbsolutePath))
                return Properties.Resources.nobg;

            return Image.FromFile(imageAbsolutePath);
        }

        private void ToggleDumbLabels(object sender, EventArgs e)
        {
            Color labelColor = Colors.PaleBlue;
            switch (editor.State)
            {
                case EditorState.NOT_READY:
                    labelColor = Colors.Disabled;
                    break;

                case EditorState.READY:
                case EditorState.GENERATING_BEATMAP:
                    labelColor = Colors.PaleBlue;
                    break;
            }

            foreach (var label in dumbLabels)
                label.ForeColor = labelColor;
        }

        private void ToggleBpmDisplay(object sender, EventArgs e)
        {
            switch (editor.State)
            {
                case EditorState.NOT_READY:
                    OriginalBpmRangeTextBox.Visible = false;
                    NewBpmRangeTextBox.Visible = false;
                    NewBpmTextBox.Enabled = false;
                    NewBpmTextBox.BackColor = Colors.Disabled;
                    OriginalBpmTextBox.Enabled = false;
                    OriginalBpmTextBox.BackColor = Colors.Disabled;
                    break;

                case EditorState.READY:
                case EditorState.GENERATING_BEATMAP:
                    OriginalBpmTextBox.Enabled = true;
                    OriginalBpmTextBox.BackColor = Colors.ReadOnlyBg;
                    NewBpmTextBox.Enabled = true;
                    NewBpmTextBox.BackColor = Colors.TextBoxBg;
                    break;
            }
        }

        private void UpdateBpmDisplay(object sender, EventArgs e)
        {
            switch (editor.State)
            {
                case EditorState.NOT_READY:
                    break;

                case EditorState.READY:
                case EditorState.GENERATING_BEATMAP:
                    (decimal oldbpm, decimal oldmin, decimal oldmax) = editor.GetOriginalBpmData();
                    decimal newbpm, newmin, newmax;
                    if (System.Math.Abs(editor.BpmRate - 1.0M) > 0.001M)
                        (newbpm, newmin, newmax) = editor.GetNewBpmData();
                    else
                        (newbpm, newmin, newmax) = (oldbpm, oldmin, oldmax);

                    // bpm textboxes
                    OriginalBpmTextBox.Text = System.Math.Round(oldbpm).ToString("0");
                    NewBpmTextBox.Text = System.Math.Round(newbpm).ToString("0");
                    if (newbpm > oldbpm + 0.001M)
                        NewBpmTextBox.ForeColor = Colors.AccentRed;
                    else if (newbpm < oldbpm - 0.001M)
                        NewBpmTextBox.ForeColor = Colors.Easier;
                    else
                        NewBpmTextBox.ForeColor = Colors.TextBoxFg;

                    // bpm range
                    OriginalBpmRangeTextBox.Text = $"({System.Math.Round(oldmin).ToString("0")} - {System.Math.Round(oldmax).ToString("0")})";
                    NewBpmRangeTextBox.Text = $"({System.Math.Round(newmin).ToString("0")} - {System.Math.Round(newmax).ToString("0")})";
                    OriginalBpmRangeTextBox.Visible = (oldmin != oldmax);
                    NewBpmRangeTextBox.Visible = (oldmin != oldmax);

                    // bpm slider
                    BpmSlider.Value = editor.BpmRate;

                    break;
            }
        }

        private void ToggleLockButtons(object sender, EventArgs e)
        {
            bool not_ready = (editor.State == EditorState.NOT_READY);
            foreach (var check in checkControls)
                check.Enabled = not_ready ? false : true;
            highQualityMp3Check.ForeColor = not_ready ? highQualityMp3Check.DisabledColor : Color.FromArgb(167, 154, 233);
        }
        private void UpdateLockButtons(object sender, EventArgs e)
        {
            // change checked state without raising any events
            HPLockCheck.CheckedChanged -= HpLockCheck_CheckedChanged;
            CSLockCheck.CheckedChanged -= CsLockCheck_CheckedChanged;
            ARLockCheck.CheckedChanged -= ArLockCheck_CheckedChanged;
            ODLockCheck.CheckedChanged -= OdLockCheck_CheckedChanged;
            BpmLockCheck.CheckedChanged -= BpmLockCheck_CheckedChanged;

            HPLockCheck.Checked = editor.HpIsLocked;
            CSLockCheck.Checked = editor.CsIsLocked;
            ARLockCheck.Checked = editor.ArIsLocked;
            ODLockCheck.Checked = editor.OdIsLocked;
            BpmLockCheck.Checked = editor.BpmIsLocked;

            HPLockCheck.CheckedChanged += HpLockCheck_CheckedChanged;
            CSLockCheck.CheckedChanged += CsLockCheck_CheckedChanged;
            ARLockCheck.CheckedChanged += ArLockCheck_CheckedChanged;
            ODLockCheck.CheckedChanged += OdLockCheck_CheckedChanged;
            BpmLockCheck.CheckedChanged += BpmLockCheck_CheckedChanged;
        }
        private bool isCheckForUpdatesEnabled()
        {
            try
            {
                foreach (string line in File.ReadAllLines("version.txt"))
                {
                    string key = line.Split(':')[0].Trim().ToLower();
                    string value = line.Split(':')[1].Trim().ToLower();
                    if (key == "check for updates" && new string[]{"no", "false"}.Contains(value))
                        return false;
                }
            }
            catch { return true; }
            return true;
        }

        private void UpdateCheckBoxes(object sender, EventArgs e)
        {
            var enabled = editor.State != EditorState.NOT_READY;
            foreach (var check in checkControls)
            {
                check.Enabled = enabled;
                check.ForeColor = enabled ? Colors.PaleBlue : Colors.Disabled;
            }

            // change checked state without raising any events
            NoSpinnersCheck.CheckedChanged         -= NoSpinnerCheckBox_CheckedChanged;
            HRCheck.CheckedChanged                 -= HRCheck_CheckedChanged;
            ChangePitchCheck.CheckedChanged        -= ChangePitchButton_CheckedChanged;
            ScaleODCheck.CheckedChanged            -= ScaleODCheck_CheckedChanged;
            ScaleARCheck.CheckedChanged            -= ScaleARCheck_CheckedChanged;
            highQualityMp3Check.CheckedChanged     -= highQualityCheckBox_CheckedChanged;
            updatesCheck.CheckedChanged            -= updatesCheck_CheckedChanged;

            NoSpinnersCheck.Checked            = editor.NoSpinners;
            HRCheck.Checked                    = editor.ForceHardrockCirclesize;
            ChangePitchCheck.Checked           = editor.ChangePitch;
            ScaleODCheck.Checked               = editor.ScaleOD;
            ScaleARCheck.Checked               = editor.ScaleAR;
            highQualityMp3Check.Checked        = editor.HighQualityMp3s;
            updatesCheck.Checked               = isCheckForUpdatesEnabled();

            NoSpinnersCheck.CheckedChanged         += NoSpinnerCheckBox_CheckedChanged;
            HRCheck.CheckedChanged                 += HRCheck_CheckedChanged;
            ChangePitchCheck.CheckedChanged        += ChangePitchButton_CheckedChanged;
            ScaleODCheck.CheckedChanged            += ScaleODCheck_CheckedChanged;
            ScaleARCheck.CheckedChanged            += ScaleARCheck_CheckedChanged;
            highQualityMp3Check.CheckedChanged     += highQualityCheckBox_CheckedChanged;
            updatesCheck.CheckedChanged            += updatesCheck_CheckedChanged;
        }

        private void ToggleHpCsArOdDisplay(object sender, EventArgs e)
        {
            bool enabled = (editor.State != EditorState.NOT_READY);
            HPSlider.Enabled = enabled;
            HPDisplay.Enabled = enabled ? true : false;
            HPDisplay.BackColor = enabled ? Colors.ReadOnlyBg : SystemColors.ControlDark;
            HPDisplay.ForeColor = Colors.ReadOnlyFg;
            HPDisplay.Font = new Font(HPDisplay.Font, FontStyle.Bold);

            enabled = (editor.State != EditorState.NOT_READY) && (editor.GetMode() == GameMode.osu || editor.GetMode() == GameMode.CatchtheBeat);
            CSSlider.Enabled = enabled;
            CSDisplay.Enabled = enabled ? true : false;
            CSDisplay.BackColor = enabled ? Colors.ReadOnlyBg : SystemColors.ControlDark;
            CSDisplay.ForeColor = Colors.ReadOnlyFg;
            CSDisplay.Font = new Font(CSDisplay.Font, FontStyle.Bold);

            enabled = (editor.State != EditorState.NOT_READY) && (editor.GetMode() == GameMode.osu || editor.GetMode() == GameMode.CatchtheBeat);
            ARSlider.Enabled = enabled;
            ARDisplay.Enabled = enabled ? true : false;
            ARDisplay.BackColor = enabled ? Colors.ReadOnlyBg : SystemColors.ControlDark;
            ARDisplay.ForeColor = Colors.ReadOnlyFg;
            ARDisplay.Font = new Font(ARDisplay.Font, FontStyle.Bold);

            enabled = (editor.State != EditorState.NOT_READY);
            ODSlider.Enabled = enabled;
            ODSlider.MaxValue = editor.GetMode() == GameMode.Mania ? 10 : 11; // od higher than 10 is invalid for o!m since it doesn't scaled with dt
            ODDisplay.Enabled = enabled ? true : false;
            ODDisplay.BackColor = enabled ? Colors.ReadOnlyBg : SystemColors.ControlDark;
            ODDisplay.ForeColor = Colors.ReadOnlyFg;
            ODDisplay.Font = new Font(ODDisplay.Font, FontStyle.Bold);
        }

        private void UpdateHpCsArOdDisplay(object sender, EventArgs e)
        {
            // HP
            decimal newHP = editor.NewBeatmap.HPDrainRate;
            decimal originalHP = editor.OriginalBeatmap.HPDrainRate;
            HPDisplay.Text = newHP.ToString("0.#");
            HPSlider.Value = (decimal)newHP;
            if (newHP > originalHP)
            {
                HPDisplay.ForeColor = Colors.AccentRed;
                HPDisplay.Font = new Font(HPDisplay.Font, FontStyle.Bold);
            }
            else if (newHP < originalHP)
            {
                HPDisplay.ForeColor = Colors.Easier;
                HPDisplay.Font = new Font(HPDisplay.Font, FontStyle.Bold);
            }
            else
            {
                HPDisplay.ForeColor = Colors.TextBoxFg;
                HPDisplay.Font = new Font(HPDisplay.Font, FontStyle.Bold);
            }

            // CS
            decimal newCS = editor.NewBeatmap.CircleSize;
            decimal originalCS = editor.OriginalBeatmap.CircleSize;
            CSDisplay.Text = newCS.ToString("0.#");
            CSSlider.Value = (decimal)newCS;
            if (newCS > originalCS)
            {
                CSDisplay.ForeColor = Colors.AccentRed;
                CSDisplay.Font = new Font(CSDisplay.Font, FontStyle.Bold);
            }
            else if (newCS < originalCS)
            {
                CSDisplay.ForeColor = Colors.Easier;
                CSDisplay.Font = new Font(CSDisplay.Font, FontStyle.Bold);
            }
            else
            {
                CSDisplay.ForeColor = Colors.TextBoxFg;
                CSDisplay.Font = new Font(CSDisplay.Font, FontStyle.Bold);
            }

            // AR
            decimal newAR = editor.NewBeatmap.ApproachRate;
            ARDisplay.Text = newAR.ToString("0.#");
            ARSlider.Value = (decimal)newAR;
            if (newAR > editor.GetScaledAR())
            {
                ARDisplay.ForeColor = Colors.AccentRed;
                ARDisplay.Font = new Font(ARDisplay.Font, FontStyle.Bold);
            }
            else if (newAR < editor.GetScaledAR())
            {
                ARDisplay.ForeColor = Colors.Easier;
                ARDisplay.Font = new Font(ARDisplay.Font, FontStyle.Bold);
            }
            else
            {
                ARDisplay.ForeColor = Colors.TextBoxFg;
                ARDisplay.Font = new Font(ARDisplay.Font, FontStyle.Bold);
            }
            if (newAR > 10)
            {
                ARDisplay.ForeColor = Color.Magenta;
                ARDisplay.Font = new Font(ARDisplay.Font, FontStyle.Bold);
            }

            // OD
            decimal newOD = editor.NewBeatmap.OverallDifficulty;
            decimal originalOD = editor.OriginalBeatmap.OverallDifficulty;
            ODDisplay.Text = newOD.ToString("0.#");
            ODSlider.Value = (decimal)newOD;
            if (newOD > editor.GetScaledOD())
            {
                ODDisplay.ForeColor = Colors.AccentRed;
                ODDisplay.Font = new Font(ODDisplay.Font, FontStyle.Bold);
            }
            else if (newOD < editor.GetScaledOD())
            {
                ODDisplay.ForeColor = Colors.Easier;
                ODDisplay.Font = new Font(ODDisplay.Font, FontStyle.Bold);
            }
            else
            {
                ODDisplay.ForeColor = Colors.TextBoxFg;
                ODDisplay.Font = new Font(ODDisplay.Font, FontStyle.Bold);
            }
            if (newOD > 10)
            {
                ODDisplay.ForeColor = Color.Magenta;
                ODDisplay.Font = new Font(ARDisplay.Font, FontStyle.Bold);
            }
        }

        private void ToggleBpmInputControls(object sender, EventArgs e)
        {
            bool enabled = (editor.State != EditorState.NOT_READY);
            BpmSlider.Enabled = enabled ? true : false;
            BpmMultiplierTextBox.Enabled = enabled ? true : false;
            BpmMultiplierTextBox.BackColor = enabled ? Colors.TextBoxBg : SystemColors.ControlDark;
        }

        private void UpdateDifficultyDisplay(object sender, EventArgs e)
        {
            SongDisplay.Stars = (float)editor.StarRating;

            var mode = editor.GetMode();
            if (mode.HasValue)
            {
                SongDisplay.GameMode = mode.Value;
            }
        }

        private void TogglePrettyButtons(object sender, EventArgs e)
        {
            bool enabled = false;
            switch (editor.State)
            {
                case EditorState.READY:
                    enabled = editor.NewMapIsDifferent() ? true : false;
                    SongsFolderButton.Visible = false;
                    break;
                case EditorState.NOT_READY:
                    enabled = false;
                    SongsFolderButton.Visible = true;
                    break;
                case EditorState.GENERATING_BEATMAP:
                    enabled = false;
                    break;
            }
            GenerateMapButton.Enabled = enabled;
            GenerateMapButton.ForeColor = enabled ? Color.White : Colors.Disabled;
            GenerateMapButton.Color = enabled ? Colors.AccentPink2 : Colors.TextBoxBg;
            GenerateMapButton.Text = editor.State == EditorState.GENERATING_BEATMAP ? "Working..." : "Create Map";

            ResetButton.Enabled = enabled;
            ResetButton.ForeColor = enabled ? Color.White : Colors.Disabled;
            ResetButton.Color = enabled ? Color.SteelBlue : Colors.TextBoxBg;
        }

#endregion Callbacks for updating GUI controls

#region User input event handlers

        private void HpSlider_ValueChanged(object sender, EventArgs e) => editor.SetHP(HPSlider.Value);

        private void CsSlider_ValueChanged(object sender, EventArgs e) => editor.SetCS(CSSlider.Value);

        private void ArSlider_ValueChanged(object sender, EventArgs e) => editor.SetAR(ARSlider.Value);

        private void OdSlider_ValueChanged(object sender, EventArgs e) => editor.SetOD(ODSlider.Value);

        private void HpLockCheck_CheckedChanged(object sender, EventArgs e) => editor.ToggleHpLock();

        private void CsLockCheck_CheckedChanged(object sender, EventArgs e) => editor.ToggleCsLock();

        private void ArLockCheck_CheckedChanged(object sender, EventArgs e) => editor.ToggleArLock();

        private void OdLockCheck_CheckedChanged(object sender, EventArgs e) => editor.ToggleOdLock();

        private void BpmLockCheck_CheckedChanged(object sender, EventArgs e) => editor.ToggleBpmLock();

        private void ScaleODCheck_CheckedChanged(object sender, EventArgs e) => editor.SetScaleOD(!editor.ScaleOD);
        private void ScaleARCheck_CheckedChanged(object sender, EventArgs e) => editor.SetScaleAR(!editor.ScaleAR);

        private void ChangePitchButton_CheckedChanged(object sender, EventArgs e) => editor.ToggleChangePitchSetting();
        private void NoSpinnerCheckBox_CheckedChanged(object sender, EventArgs e) => editor.ToggleNoSpinners();
        private void highQualityCheckBox_CheckedChanged(object sender, EventArgs e) => editor.ToggleHighQualityMp3s();

        private void BpmMultiplierTextBox_Submit(object sender, EventArgs e)
        {
            decimal mult;
            if (Decimal.TryParse(BpmMultiplierTextBox.Text, out mult))
                editor.SetBpmMultiplier(mult);
            else
                BpmMultiplierTextBox.Text = editor.BpmRate.ToString("0.00");
        }

        private void NewBpmTextBox_Submit()
        {
            int bpm;
            if (int.TryParse(NewBpmTextBox.Text, out bpm))
                editor.SetBpm(bpm);
        }

        private void NewBpmTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
                NewBpmTextBox_Submit();
        }

        private void NewBpmTextBox_Leave(object sender, EventArgs e)
        {
            NewBpmTextBox_Submit();
        }
        private void NewBpmTextBox_KeyPress(object sender, KeyPressEventArgs e)
        {
            if (e.KeyChar.ToString() == " ")
            {
                e.Handled = true;
                BpmLockCheck.Checked = !BpmLockCheck.Checked;
            }
        }

        private void ResetButton_Click(object sender, EventArgs e) => editor.ResetBeatmap();

        private void DeleteButton_Click(object sender, EventArgs e)
        {
            if (gameLoaded == true)
            {
                MessageBox.Show("Please close osu! first then try again.", "osu! is running");
                return;
            }
            var mp3List = editor.GetUnusedMp3s();
            if (new DeleteMp3sForm(mp3List).ShowDialog() == DialogResult.OK)
            {
                mp3List
                    .Select(relativeMp3 => JunUtils.FullPathFromSongsFolder(relativeMp3))
                    .Select(absMp3 => new FileInfo(absMp3))
                    .ToList()
                    .ForEach(file => file.Delete());
                if (mp3List.Count > 0)
                    MessageBox.Show($"Deleted {mp3List.Count} file(s).", "Success");
                editor.CleanUpManifestFile();
            }
        }

        private void GenerateMapButton_Click(object sender, EventArgs e)
        {
            if ( !Properties.Settings.Default.HighARODMessageShown && (editor.NewBeatmap.ApproachRate > 10M || editor.NewBeatmap.ApproachRate > 10M) )
            {
                MessageBox.Show("You have chosen an AR or OD greater than 10. After this map gets created, make sure to play it with Doubletime.", "Note");
                Properties.Settings.Default.HighARODMessageShown = true;
                Properties.Settings.Default.Save();
            }
            BackgroundWorker.RunWorkerAsync();
        }

        private void Unfocus(object sender, EventArgs e) => ActiveControl = middlePanel;

        private void CreateMapHotkeyHandler(object sender, EventArgs e)
        {
            if (!GenerateMapButton.Enabled)
                return;

            var k = (KeyEventArgs)e;
            if (k.Control && k.Shift && k.KeyCode == Keys.X)
            {
                sound.Stream = Properties.Resources.hotkey;
                sound.Play();
                GenerateMapButton_Click(sender, EventArgs.Empty);
            }
        }

#endregion User input event handlers

#region Timer events

        private void BeatmapUpdateTimer_Tick(object sender, EventArgs e)
        {
            if (gameLoaded == false || gameLoaded == null || !mapSelectScreen || editor.State == EditorState.GENERATING_BEATMAP)
                return;

            // this can be cleaned up...
            // Read memory for current map
            osuReader.TryRead(osuReader.OsuMemoryAddresses.Beatmap);
            string beatmapFilename = osuReader.OsuMemoryAddresses.Beatmap.OsuFileName;
            string beatmapFolder = osuReader.OsuMemoryAddresses.Beatmap.FolderName;

            var invalidChars = Path.GetInvalidPathChars();

            // Read unsuccessful
            if (string.IsNullOrWhiteSpace(beatmapFilename) || beatmapFilename.Any(c => invalidChars.Contains(c)))
                return;
            if (string.IsNullOrWhiteSpace(beatmapFolder) || beatmapFolder.Any(c => invalidChars.Contains(c)))
                return;

            // Beatmap name hasn't changed since last read
            if (previousBeatmapRead != null && previousBeatmapRead == beatmapFilename)
                return;
            previousBeatmapRead = beatmapFilename;

            // Try to locate the beatmap
            string absoluteFilename = Path.Combine(userSongsFolder, beatmapFolder.TrimEnd(), beatmapFilename);
            if (!File.Exists(absoluteFilename))
                return; 

            // signal the editor class to load this beatmap sometime in the future
            editor.RequestBeatmapLoad(absoluteFilename);
        }

        private async void OsuRunningTimer_Tick(object sender, EventArgs e)
        {
            // check if osu!.exe is running
            var processes = Process.GetProcessesByName("osu!");
            if (processes.Length == 0)
                gameLoaded = false;
            else
            {
                if (gameLoaded == false) // @ posedge gameLoaded
                {
                    await Task.Run(() => Thread.Sleep(5000));
                    gameLoaded = true;
                }
                else if (gameLoaded == null)
                    gameLoaded = true;

                if (userSongsFolder == null || userSongsFolder == "")
                {
                    // Try to get osu songs folder
                    // TODO: wrap with try catch
                    var osuExePath = processes[0].MainModule.FileName;
                    userSongsFolder = Path.Combine(Path.GetDirectoryName(osuExePath), "Songs");
                    Properties.Settings.Default.SongsFolder = userSongsFolder;
                    Properties.Settings.Default.Save();
                }
            }
            osuReader.TryRead(osuReader.OsuMemoryAddresses.GeneralData);
            OsuMemoryStatus status = (OsuMemoryStatus)(osuReader.OsuMemoryAddresses.GeneralData.OsuStatus);

            if (status == OsuMemoryStatus.SongSelect || status == OsuMemoryStatus.MultiplayerRoom || status == OsuMemoryStatus.MultiplayerSongSelect)
                mapSelectScreen = true;
            else
                mapSelectScreen = false;

            if (mapSelectScreen && !hooked)
            {
                kbhook.hook();
                hooked = true;
            }
            if (!mapSelectScreen && hooked)
            {
                kbhook.unhook();
                hooked = false;
            }
        }

#endregion Timer events

#region borderless window title bar

        private bool Drag;
        private int MouseX;
        private int MouseY;

        private void PanelMove_MouseDown(object sender, MouseEventArgs e)
        {
            Drag = true;
            MouseX = Cursor.Position.X - this.Left;
            MouseY = Cursor.Position.Y - this.Top;
        }

        private void PanelMove_MouseMove(object sender, MouseEventArgs e)
        {
            if (Drag)
            {
                Top = Cursor.Position.Y - MouseY;
                Left = Cursor.Position.X - MouseX;
            }
        }

        private void PanelMove_MouseUp(object sender, MouseEventArgs e) => Drag = false;

        private void closeButton_Click(object sender, EventArgs e) => Close();

        private void minimizeButton_Click(object sender, EventArgs e) => WindowState = FormWindowState.Minimized;

#endregion borderless window title bar

#region Background Worker

        private void BackgroundWorker_DoWork(object sender, DoWorkEventArgs e)
        {
            editor.GenerateBeatmap();
        }

        private void BackgroundWorker_RunWorkerCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            if (e.Error != null)
            {
                MessageBox.Show($"Your map couldn't be generated because of the following error:\n{e.Error.ToString()}", "Generating Map failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                // TODO: Catch UnauthorizedAccessException
            }
            else
            {
                //GenerateMapButton.Progress = 0f;
            }
        }

#endregion Background Worker

        private void titlePanel_Paint(object sender, PaintEventArgs e)
        {
            e.Graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            Icon smallIcon = new Icon(this.Icon, 16, 16);
            e.Graphics.DrawIcon(smallIcon, 10, 10);
            e.Graphics.DrawString(Text, new Font(Font, FontStyle.Regular), Brushes.White, 10 + 16 + 4, 10);
        }


        private void BpmMultiplierTextBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter)
            {
                BpmMultiplierTextBox_Submit(sender, e);
                e.Handled = e.SuppressKeyPress = true; // silence annoying windows bell
            }
        }

        private void BpmSlider_ValueChanged(object sender, EventArgs e)
        {
            editor.SetBpmMultiplier(BpmSlider.Value);
        }

        private void HRCheck_CheckedChanged(object sender, EventArgs e)
        {
            editor.ToggleHrEmulation();
        }

        private void SongsFolderButton_Click(object sender, EventArgs e)
        {
            var songsFolderForm = new SongsFolderForm(userSongsFolder);
            if (songsFolderForm.ShowDialog() == DialogResult.OK)
            {
                if (!Directory.Exists(songsFolderForm.SongsFolder))
                {
                    MessageBox.Show("Could not find that directory. Make sure it was typed correctly and that it actually exists.");
                    return;
                }
                userSongsFolder = songsFolderForm.SongsFolder.TrimEnd(Path.DirectorySeparatorChar);
                Properties.Settings.Default.SongsFolder = userSongsFolder;
                Properties.Settings.Default.Save();
            }
        }

        private void MainForm_FormClosing(object sender, FormClosingEventArgs e)
        {
            editor.SaveSettings();
            editor.SaveProfilesToDisk();
            // save hotkeys
            Properties.Settings.Default.HotkeyCreateMap = Hotkeys[0];
            Properties.Settings.Default.HotkeyProfile1 = Hotkeys[1];
            Properties.Settings.Default.HotkeyProfile2 = Hotkeys[2];
            Properties.Settings.Default.HotkeyProfile3 = Hotkeys[3];
            Properties.Settings.Default.HotkeyProfile4 = Hotkeys[4];
            Properties.Settings.Default.Save();
        }

#region User Profile Buttons
        private void profileButton1_Click(object sender, EventArgs e) => editor.LoadProfile(0);
        private void profileButton2_Click(object sender, EventArgs e) => editor.LoadProfile(1);
        private void profileButton3_Click(object sender, EventArgs e) => editor.LoadProfile(2);
        private void profileButton4_Click(object sender, EventArgs e) => editor.LoadProfile(3);
        private void saveButton1_Click(object sender, EventArgs e) => saveButtonClick(0);
        private void saveButton2_Click(object sender, EventArgs e) => saveButtonClick(1);
        private void saveButton3_Click(object sender, EventArgs e) => saveButtonClick(2);
        private void saveButton4_Click(object sender, EventArgs e) => saveButtonClick(3);
        private void saveButtonClick(int whichProfile)
        {
            var saveButtons = new List<Button>() { saveButton1, saveButton2, saveButton3, saveButton4 };
            saveButtons[whichProfile].ForeColor = Color.White;
            saveButtons[whichProfile].Text = "Saved!";
            saveButtonHighlight[whichProfile] = 1.0M;
            editor.SaveProfile(whichProfile);
        }
        private void renameButton1_Click(object sender, EventArgs e) => renameProfileClick(0);
        private void renameButton2_Click(object sender, EventArgs e) => renameProfileClick(1);
        private void renameButton3_Click(object sender, EventArgs e) => renameProfileClick(2);
        private void renameButton4_Click(object sender, EventArgs e) => renameProfileClick(3);
        private void renameProfileClick(int whichProfile)
        {
            var popup = new RenameProfileForm(Cursor.Position.X - 83, Cursor.Position.Y - 15);
            if (popup.ShowDialog() == DialogResult.OK)
                editor.RenameProfile(whichProfile, popup.InputString);
        }
        private void UpdateProfiles(object sender, EventArgs e)
        {
            bool profilesVisible = (editor.State == EditorState.NOT_READY) ? false : true;
            profileButton1.Visible = profilesVisible;
            profileButton2.Visible = profilesVisible;
            profileButton3.Visible = profilesVisible;
            profileButton4.Visible = profilesVisible;
            saveButton1.Visible = profilesVisible;
            saveButton2.Visible = profilesVisible;
            saveButton3.Visible = profilesVisible;
            saveButton4.Visible = profilesVisible;
            renameButton1.Visible = profilesVisible;
            renameButton2.Visible = profilesVisible;
            renameButton3.Visible = profilesVisible;
            renameButton4.Visible = profilesVisible;

            profileButton1.Text = editor.UserProfiles[0].Name;
            profileButton2.Text = editor.UserProfiles[1].Name;
            profileButton3.Text = editor.UserProfiles[2].Name;
            profileButton4.Text = editor.UserProfiles[3].Name;
        }
        private void ProfileHotkeyHandler(object sender, EventArgs e)
        {
            var k = (KeyEventArgs)e;
            if (k.Alt && k.Shift)
            {
                var KeyToProfileMapping = new Dictionary<Keys, int>()
                {
                    { Hotkeys[1], 0},
                    { Hotkeys[2], 1},
                    { Hotkeys[3], 2},
                    { Hotkeys[4], 3}
                };
                if (KeyToProfileMapping.ContainsKey(k.KeyCode)) {
                    int whichProfile = KeyToProfileMapping[k.KeyCode];
                    editor.LoadProfile(whichProfile);
                    if (GenerateMapButton.Enabled)
                    {
                        sound.Stream = Properties.Resources.hotkey;
                        sound.Play();
                        GenerateMapButton_Click(sender, EventArgs.Empty);
                    }
                }
            }
        }

        private List<decimal> saveButtonHighlight = new List<decimal>() { 0, 0, 0, 0 };
        const decimal HIGHLIGHT_FADE = 0.03M;
        private void formAnimationTimer_Tick(object sender, EventArgs e)
        {
            Color startBackColor = Color.FromArgb(71, 115, 66);
            Color endBackColor = Color.FromArgb(45, 42, 63);
            var saveButtons = new List<Button>() { saveButton1, saveButton2, saveButton3, saveButton4 };
            for (int i = 0; i < saveButtons.Count; i++)
            {
                // linearly interpolate colour
                // saveButtonHighlight 1.0 -> 0.0
                if (saveButtonHighlight[i] > 0)
                {
                    saveButtons[i].FlatAppearance.MouseOverBackColor = JunUtils.LerpColor(startBackColor, endBackColor, 1 - saveButtonHighlight[i]);
                    saveButtons[i].ForeColor = JunUtils.LerpColor(Color.White, Color.FromArgb(90, 90, 134), 1 - saveButtonHighlight[i]);
                }

                // decay
                saveButtonHighlight[i] -= HIGHLIGHT_FADE;
                if (saveButtonHighlight[i] <= 0)
                {
                    saveButtonHighlight[i] = 0M;
                    saveButtons[i].ForeColor = Color.FromArgb(90, 90, 134);
                    saveButtons[i].Text = "Save";
                    saveButtons[i].FlatAppearance.MouseOverBackColor = Color.Empty;
                }
            }
        }
#endregion

        private void RearrangeLayout(object sender, EventArgs e)
        {
            bool profilesVisible = (editor.State == EditorState.NOT_READY) ? false : true;
            bool extrasVisible = profilesVisible;
            if (!profilesVisible)
            {
                // not ready layout
                middlePanel.Height = 110;
                BottomPanel.Height = 111;
                Height = 493 + (extrasPanel.Visible ? extrasPanel.Height : 0);
            }
            else
            {
                // ready layout
                middlePanel.Height = 178;
                BottomPanel.Height = 111 - 33;
                Height = 531 + (extrasPanel.Visible ? extrasPanel.Height : 0);
            }
        }
        private void showExtrasButton_Click(object sender, EventArgs e)
        {
            if (!extrasPanel.Visible)
            {
                extrasPanel.Visible = true;
                showExtrasButton.Text = "▼ Less";
            }
            else
            {
                extrasPanel.Visible = false;
                showExtrasButton.Text = "▶ More!";
            }
            RearrangeLayout(this, EventArgs.Empty);
        }
        private void editHotkeysButton_Click(object sender, EventArgs e)
        {
            int centerX = DesktopLocation.X + (Width / 2);
            int centerY = DesktopLocation.Y + (Height / 2);
            var hotkeyForm = new HotkeyForm(centerX - 321/2, centerY - 217/2, Hotkeys);
            hotkeyForm.ShowDialog();
            Hotkeys = hotkeyForm.Hotkeys;
            ApplyHotkeys();
        }


        private void updatesCheck_CheckedChanged(object sender, EventArgs e)
        {
            ToggleCheckForUpdates();
            UpdateCheckBoxes(this, EventArgs.Empty);
        }
        /// <summary>
        /// return false on failure
        /// </summary>
        private void ToggleCheckForUpdates()
        {
            string getKey(string s) { return s.Split(':')[0].Trim().ToLower(); }
            string getValue(string s) { return s.Split(':')[1].Trim().ToLower(); }

            List<string> lines;
            try {
                lines = File.ReadAllLines("version.txt").ToList();
            } catch {
                MessageBox.Show("version.txt missing or corrupt.");
                return;
            }

            var checkForUpdatesLineIndex = lines.FindIndex(l => getKey(l) == "check for updates");
            if (checkForUpdatesLineIndex == -1)
            {
                MessageBox.Show("version.txt missing or corrupt.");
                return;
            }

            string checkForUpdatesLine = lines[checkForUpdatesLineIndex];
            string checkForUpdatesValue = getValue(lines[checkForUpdatesLineIndex]);
            bool toggleToTrue = new string[] { "no", "false" }.Contains(checkForUpdatesValue);

            string replacementLine = $"check for updates: {(toggleToTrue ? "true" : "false")}";
            int replaceIndex = lines.FindIndex(l => l.Split(':')[0].Trim().ToLower() == "check for updates");
            if (replaceIndex != -1)
                lines[replaceIndex] = replacementLine;

            try {
                File.WriteAllLines("version.txt", lines);
            }
            catch (UnauthorizedAccessException)
            {
                MessageBox.Show("Permission to write to version.txt was denied. Check your antivirus, or try running osu trainer with administrator priveledges.");
                return;
            }
            catch (Exception)
            {
                MessageBox.Show("Unable to write to version.txt");
                return;
            }
            return;
        }

        private void spectrogramButton_Click(object sender, EventArgs e)
        {
        }
    }
}
