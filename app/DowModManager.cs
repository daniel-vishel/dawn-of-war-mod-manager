// ============================================================
//  Dawn of War — Mod Manager (WinForms, .NET Framework 4.8)
//  Graphical mod manager. All the heavy lifting (file patching, SGA
//  extraction, texture slicing and fitting, UI transforms) lives in the
//  PowerShell scripts; this app only collects settings, reads the real
//  game state and calls DoW-Launcher.ps1.
//
//  Build: powershell -File app\Build-App.ps1
//  Headless self-test: DoW-ModManager.exe --selftest
// ============================================================
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DowModManager
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--selftest") { SelfTest.Run(); return; }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    // ---------- Colour theme, taken from the system ----------
    class Theme
    {
        public bool Dark;
        public Color Bg, Fg, GroupFg, Muted, PanelBg, InputBg, InputFg, Accent, AccentFg, LogBg, LogFg, Border;

        public static Theme FromSystem()
        {
            bool dark = false;
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (k != null)
                    {
                        object v = k.GetValue("AppsUseLightTheme");
                        if (v is int) dark = ((int)v) == 0;
                    }
                }
            }
            catch { }
            return dark ? DarkTheme() : LightTheme();
        }

        static Theme LightTheme()
        {
            return new Theme
            {
                Dark = false,
                Bg = Color.FromArgb(245, 246, 248),
                Fg = Color.FromArgb(20, 22, 26),
                GroupFg = Color.FromArgb(40, 44, 52),
                Muted = Color.DimGray,
                PanelBg = Color.FromArgb(28, 32, 38),
                InputBg = Color.White,
                InputFg = Color.FromArgb(20, 22, 26),
                Accent = Color.FromArgb(52, 120, 200),
                AccentFg = Color.White,
                LogBg = Color.FromArgb(24, 26, 30),
                LogFg = Color.Gainsboro,
                Border = Color.FromArgb(200, 204, 210),
            };
        }

        static Theme DarkTheme()
        {
            return new Theme
            {
                Dark = true,
                Bg = Color.FromArgb(32, 34, 38),
                Fg = Color.FromArgb(232, 234, 238),
                GroupFg = Color.FromArgb(210, 214, 220),
                Muted = Color.FromArgb(150, 154, 160),
                PanelBg = Color.FromArgb(20, 22, 26),
                InputBg = Color.FromArgb(45, 48, 54),
                InputFg = Color.FromArgb(232, 234, 238),
                Accent = Color.FromArgb(58, 130, 214),
                AccentFg = Color.White,
                LogBg = Color.FromArgb(18, 20, 24),
                LogFg = Color.Gainsboro,
                Border = Color.FromArgb(70, 74, 80),
            };
        }
    }

    // ---------- Question-mark dot; the description lives in its tooltip ----------
    class HelpDot : Control
    {
        Theme th;
        bool hover;

        public HelpDot(Theme theme, ToolTip tip, string text)
        {
            th = theme;
            Size = new Size(18, 18);
            Cursor = Cursors.Help;
            SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.UserPaint |
                     ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
            BackColor = Color.Transparent;
            tip.SetToolTip(this, text);
            MouseEnter += (s, e) => { hover = true; Invalidate(); };
            MouseLeave += (s, e) => { hover = false; Invalidate(); };
        }

        public void SetTheme(Theme theme) { th = theme; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            Color ring = hover ? th.Accent : th.Muted;
            using (var b = new SolidBrush(hover ? Color.FromArgb(40, th.Accent) : Color.Transparent))
                g.FillEllipse(b, r);
            using (var p = new Pen(ring, 1.4f))
                g.DrawEllipse(p, r);
            using (var f = new Font("Segoe UI", 9f, FontStyle.Bold))
            using (var b = new SolidBrush(ring))
            {
                var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                g.DrawString("?", f, b, new RectangleF(0, 0, Width, Height + 1), sf);
            }
        }
    }

    // ---------- Settings, mirroring $S in DoW-Launcher.ps1 ----------
    class Settings
    {
        public string GamePath = "";
        public string Game = "W40k";          // W40k | WA
        public int Width = 3440;
        public int Height = 1440;
        public bool Widescreen = true;         // rendering patch + UI, one bundle
        public bool TexturesCustom = false;    // repainted art is a work in progress; hard-cut only
        public bool Zoom = true;
        public int DistMax = 76;
        public bool Russian = false;
        public string ExeMode = "skip";        // skip | compromise | full

        public Settings Clone() { return (Settings)MemberwiseClone(); }

        // Compare without GamePath: what matters is whether the mod set changed
        public bool SameAs(Settings o)
        {
            return Widescreen == o.Widescreen && Width == o.Width && Height == o.Height
                && TexturesCustom == o.TexturesCustom && Zoom == o.Zoom && DistMax == o.DistMax
                && Russian == o.Russian && ExeMode == o.ExeMode;
        }

        public static Settings Load(string path)
        {
            var s = new Settings();
            if (!File.Exists(path)) return s;
            try
            {
                string t = File.ReadAllText(path, Encoding.UTF8);
                s.GamePath       = JStr(t, "GamePath", s.GamePath);
                s.Game           = JStr(t, "Game", s.Game);
                s.Width          = JInt(t, "Width", s.Width);
                s.Height         = JInt(t, "Height", s.Height);
                s.Widescreen     = JBool(t, "Widescreen", s.Widescreen);
                // Deliberately not read from the file: the repainted-textures
                // mode is a work in progress and stays off. An older
                // launcher-settings.json that still carries true would otherwise
                // leave the window dirty against a mode the UI cannot select.
                s.TexturesCustom = false;
                s.Zoom           = JBool(t, "Zoom", s.Zoom);
                s.DistMax        = JInt(t, "DistMax", s.DistMax);
                s.Russian        = JBool(t, "Russian", s.Russian);
                s.ExeMode        = JStr(t, "ExeMode", s.ExeMode);
            }
            catch { }
            return s;
        }

        public void Save(string path)
        {
            var sb = new StringBuilder();
            sb.Append("{\r\n");
            sb.AppendFormat("  \"GamePath\": \"{0}\",\r\n", Esc(GamePath));
            sb.AppendFormat("  \"Game\": \"{0}\",\r\n", Esc(Game));
            sb.AppendFormat("  \"Width\": {0},\r\n", Width);
            sb.AppendFormat("  \"Height\": {0},\r\n", Height);
            sb.AppendFormat("  \"Widescreen\": {0},\r\n", B(Widescreen));
            sb.AppendFormat("  \"TexturesCustom\": {0},\r\n", B(TexturesCustom));
            sb.AppendFormat("  \"Zoom\": {0},\r\n", B(Zoom));
            sb.AppendFormat("  \"DistMax\": {0},\r\n", DistMax);
            sb.AppendFormat("  \"Russian\": {0},\r\n", B(Russian));
            sb.AppendFormat("  \"ExeMode\": \"{0}\"\r\n", Esc(ExeMode));
            sb.Append("}\r\n");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        static string B(bool v) { return v ? "true" : "false"; }
        static string Esc(string s) { return (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\""); }
        public static string JStr(string t, string key, string def)
        {
            var m = Regex.Match(t, "\"" + key + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            if (!m.Success) return def;
            return m.Groups[1].Value.Replace("\\\\", "\\").Replace("\\\"", "\"");
        }
        public static int JInt(string t, string key, int def)
        {
            var m = Regex.Match(t, "\"" + key + "\"\\s*:\\s*(-?\\d+)");
            int v; return (m.Success && int.TryParse(m.Groups[1].Value, out v)) ? v : def;
        }
        public static bool JBool(string t, string key, bool def)
        {
            var m = Regex.Match(t, "\"" + key + "\"\\s*:\\s*(true|false)", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.ToLowerInvariant() == "true" : def;
        }
    }

    // ---------- Real game state, from DoW-Launcher.ps1 -Status ----------
    class GameStatus
    {
        public bool WidescreenPatched, UiInstalled, TexturesCustom, ZoomInstalled, LocaleRussian, LangRussian;
        public bool LocaleEnglish;      // English locale present, needed to switch back
        public string LocalesFound = "";// locales really installed, comma separated
        public int Width, Height, DistMax;
        public bool Known;   // whether the state could be read at all

        public static GameStatus Parse(string json)
        {
            var s = new GameStatus();
            if (string.IsNullOrWhiteSpace(json) || !json.Contains("{")) return s;
            s.WidescreenPatched = Settings.JBool(json, "WidescreenPatched", false);
            s.UiInstalled       = Settings.JBool(json, "UiInstalled", false);
            s.TexturesCustom    = Settings.JBool(json, "TexturesCustom", false);
            s.ZoomInstalled     = Settings.JBool(json, "ZoomInstalled", false);
            s.LocaleRussian     = Settings.JBool(json, "LocaleRussian", false);
            s.LocaleEnglish     = Settings.JBool(json, "LocaleEnglish", false);
            s.LocalesFound      = Settings.JStr(json, "LocalesFound", "");
            s.LangRussian       = Settings.JBool(json, "LangRussian", false);
            s.Width             = Settings.JInt(json, "Width", 0);
            s.Height            = Settings.JInt(json, "Height", 0);
            s.DistMax           = Settings.JInt(json, "DistMax", 0);
            s.Known = true;
            return s;
        }
    }

    // ---------- Resolution of the current screen ----------
    static class ScreenInfo
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public int dmFields;
            public int dmPositionX, dmPositionY;
            public int dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
            public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }
        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE devMode);
        const int ENUM_CURRENT_SETTINGS = -1;

        // Physical resolution of the monitor the window sits on.
        // EnumDisplaySettings reports honest pixels under Windows display
        // scaling, unlike Screen.Bounds, so it is primary and Screen is the
        // fallback.
        public static void Get(Control c, out int w, out int h)
        {
            w = 0; h = 0;
            try
            {
                var scr = c != null ? Screen.FromControl(c) : Screen.PrimaryScreen;
                var dm = new DEVMODE();
                dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
                if (EnumDisplaySettings(scr.DeviceName, ENUM_CURRENT_SETTINGS, ref dm)
                    && dm.dmPelsWidth > 0 && dm.dmPelsHeight > 0)
                {
                    w = dm.dmPelsWidth; h = dm.dmPelsHeight;
                    return;
                }
                w = scr.Bounds.Width; h = scr.Bounds.Height;
            }
            catch
            {
                try { w = Screen.PrimaryScreen.Bounds.Width; h = Screen.PrimaryScreen.Bounds.Height; } catch { }
            }
        }
    }

    // ---------- Game folder auto-detection ----------
    static class GameFinder
    {
        public static string Find()
        {
            string[] candidates =
            {
                @"C:\Program Files (x86)\Steam\steamapps\common\Dawn of War Gold",
                @"C:\Program Files (x86)\Steam\steamapps\common\Dawn of War Anniversary Edition",
                @"C:\Program Files\Steam\steamapps\common\Dawn of War Gold",
                @"D:\Steam\steamapps\common\Dawn of War Gold",
                @"D:\SteamLibrary\steamapps\common\Dawn of War Gold",
                @"E:\Steam\steamapps\common\Dawn of War Gold",
                @"E:\SteamLibrary\steamapps\common\Dawn of War Gold",
            };
            foreach (var c in candidates)
                if (File.Exists(Path.Combine(c, "W40k.exe"))) return c;
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                {
                    string steam = key != null ? key.GetValue("SteamPath") as string : null;
                    if (!string.IsNullOrEmpty(steam))
                    {
                        string vdf = Path.Combine(steam, @"steamapps\libraryfolders.vdf");
                        if (File.Exists(vdf))
                        {
                            string t = File.ReadAllText(vdf);
                            foreach (Match m in Regex.Matches(t, "\"path\"\\s+\"(.+?)\""))
                            {
                                string lib = m.Groups[1].Value.Replace("\\\\", "\\");
                                foreach (var name in new[] { "Dawn of War Gold", "Dawn of War Anniversary Edition", "Dawn of War" })
                                {
                                    string p = Path.Combine(lib, @"steamapps\common\" + name);
                                    if (File.Exists(Path.Combine(p, "W40k.exe"))) return p;
                                }
                            }
                        }
                    }
                }
            }
            catch { }
            return null;
        }
    }

    // ---------- Tooltip texts ----------
    static class Help
    {
        public const string Widescreen =
            "Расширяет обзор под ваш экран (честный FOV, не растяжение) и ставит нерастянутый UI.\r\n" +
            "Файлы игры патчатся на диске с полным бэкапом, поэтому после «Применить»\r\n" +
            "игра запускается ОБЫЧНЫМ способом из Steam — лаунчер для игры не нужен.\r\n" +
            "\r\n" +
            "Панели HUD не тянутся на всю ширину, а делятся и разъезжаются по углам экрана:\r\n" +
            "  • мини-карта и ресурсы — в левый нижний угол;\r\n" +
            "  • панель выбора отряда, команды и кнопки меню — в правый нижний угол;\r\n" +
            "  • центр экрана остаётся открытым — там виден 3D-мир.\r\n" +
            "\r\n" +
            "Отрисовка и UI работают только вместе: выключение снимает и UI-мод.";

        public const string ExeMode =
            "Режимы «Мини-карта (exe)» — как патчить константу соотношения в W40k.exe\r\n" +
            "(влияет только на мини-карту):\r\n" +
            "\r\n" +
            "  • skip (рекомендуется) — exe не трогаем; 3D-мир корректный, а квадратность\r\n" +
            "    мини-карты и так чинит UI-мод через разметку. Начинать с него.\r\n" +
            "\r\n" +
            "  • compromise — в exe пишется 1.25: помогает мини-карте у части версий,\r\n" +
            "    но искажает главное меню (в бою нормально).\r\n" +
            "\r\n" +
            "  • full — в exe полное соотношение: у кого-то мини-карта ок, но 3D-мир\r\n" +
            "    может растянуться. Крайний вариант.";

        public const string Textures =
            "Текстуры нижней панели управления:\r\n" +
            "\r\n" +
            "  • жёсткая обрезка — стандартные текстуры игры: они обрезаны по границе\r\n" +
            "    с картой, части раздвигаются по углам, и на стыках виден резкий обрыв\r\n" +
            "    картинки когда-то цельной панели. Сейчас это единственный режим.\r\n" +
            "\r\n" +
            "  • дорисованные — В РАЗРАБОТКЕ, выбор заблокирован.\r\n" +
            "    Идея: панель с плавным, аккуратным краем вместо резкого среза.\r\n" +
            "    Готового арта в репозитории нет и не будет: перерисованные\r\n" +
            "    текстуры делаются на основе игровых, а это чужой контент —\r\n" +
            "    раздавать его вместе с модом нельзя.\r\n" +
            "    Механика в скриптах уже есть (Edit-BarTextures.ps1: -Export,\r\n" +
            "    -Preview, -Import), так что свой арт подключить можно вручную.";

        public const string Zoom =
            "Отодвигает максимальный отвод камеры колёсиком (DistMax; оригинал 38).\r\n" +
            "Требует включённой «Full 3D Camera» в настройках графики игры.\r\n" +
            "Чем больше значение, тем сильнее дальний план затягивает туманом.";

        public const string Russian =
            "Ставит русский язык: прописывает [lang:russian] в W40k.ini (с резервной копией).\r\n" +
            "\r\n" +
            "Менеджер проверяет, какие локали РЕАЛЬНО лежат в игре (папки\r\n" +
            "<модуль>\\Locale\\<Язык> с файлами .ucs/.sga), и пишет это в строке\r\n" +
            "состояния. Пустая папка локали за установленную не считается.\r\n" +
            "\r\n" +
            "Защита от вылета: язык переключается ТОЛЬКО на локаль, которая\r\n" +
            "действительно есть. Если русификатор заменил английскую локаль,\r\n" +
            "выключить русский нельзя — с несуществующей локалью игра падает\r\n" +
            "на старте. Менеджер это заблокирует и не тронет W40k.ini;\r\n" +
            "вернуть английский можно проверкой целостности файлов в Steam.\r\n" +
            "\r\n" +
            "Если русификатора нет — нажмите «Скачать русификатор…» (откроется\r\n" +
            "гайд со ссылками), затем «Указать архив…». Поддерживаются zip, rar,\r\n" +
            "7z и другие форматы (нужен установленный 7-Zip или WinRAR; zip\r\n" +
            "распаковывается и без них). Лишняя папка-обёртка внутри архива\r\n" +
            "снимается автоматически.\r\n" +
            "\r\n" +
            "Самих файлов русификатора в репозитории нет: это чужой контент,\r\n" +
            "раздавать его вместе с модом нельзя.";

        public const string GamePath =
            "Папка, где лежит W40k.exe (например ...\\steamapps\\common\\Dawn of War Gold).\r\n" +
            "Обычно определяется автоматически по реестру Steam и библиотекам.";
    }

    // ---------- Main window ----------
    class MainForm : Form
    {
        Settings S;              // what the window currently shows
        Settings applied;        // snapshot of what is really installed
        GameStatus status = new GameStatus();
        Theme th;

        readonly string root, launcher, cfgPath;
        string rusArchive = "";

        TextBox txtPath, log, txtRus;
        ComboBox cmbGame, cmbRes, cmbExe;
        NumericUpDown numW, numH, numDist;
        CheckBox chkWs, chkZoom, chkRus;
        RadioButton radCustom, radHard;
        Button btnApply, btnPlay, btnRestore, btnDefaults, btnRusBrowse, btnLogToggle, btnLaunchOnly;
        Button btnRusGet;
        // Steam guide with the localisation download mirrors (same as in README)
        const string RusGuideUrl = "https://steamcommunity.com/sharedfiles/filedetails/?id=3421728842";
        Label lblRusState, lblStatus;
        Panel header;
        ToolTip tip;
        List<HelpDot> dots = new List<HelpDot>();
        bool busy, loading, logVisible = false, gameFound;
        int formHeightWithLog, formHeightNoLog;

        public MainForm()
        {
            root = ResolveRoot();
            launcher = Path.Combine(root, "DoW-Launcher.ps1");
            cfgPath = Path.Combine(root, "launcher-settings.json");
            th = Theme.FromSystem();
            S = Settings.Load(cfgPath);
            if (string.IsNullOrEmpty(S.GamePath))
            {
                string auto = GameFinder.Find();
                if (auto != null) S.GamePath = auto;
            }
            applied = S.Clone();
            BuildUi();
            Load += (s, e) => RefreshStatus();
        }

        static string ResolveRoot()
        {
            string dir = Application.StartupPath;
            if (File.Exists(Path.Combine(dir, "DoW-Launcher.ps1"))) return dir;
            string parent = Directory.GetParent(dir) != null ? Directory.GetParent(dir).FullName : dir;
            if (File.Exists(Path.Combine(parent, "DoW-Launcher.ps1"))) return parent;
            return dir;
        }

        void BuildUi()
        {
            Text = "Dawn of War — Mod Manager";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Segoe UI", 9f);
            BackColor = th.Bg;
            ForeColor = th.Fg;
            Width = 688;

            string ico = Path.Combine(root, @"app\app.ico");
            if (File.Exists(ico)) { try { Icon = new Icon(ico); } catch { } }

            tip = new ToolTip { AutoPopDelay = 32000, InitialDelay = 250, ReshowDelay = 80, ShowAlways = true };

            // --- Header: the app\header.png image, or plain text ---
            header = new Panel { Location = new Point(0, 0), Size = new Size(688, 84), BackColor = th.PanelBg };
            string hdrImg = Path.Combine(root, @"app\header.png");
            if (File.Exists(hdrImg))
            {
                try
                {
                    var pb = new PictureBox
                    {
                        Image = Image.FromFile(hdrImg),
                        SizeMode = PictureBoxSizeMode.Zoom,
                        Dock = DockStyle.Fill,
                        BackColor = th.PanelBg
                    };
                    header.Controls.Add(pb);
                }
                catch { AddHeaderText(); }
            }
            else AddHeaderText();
            Controls.Add(header);

            int y = 96;

            // --- Game ---
            var grpGame = Group("Игра", 12, y, 660, 84);
            txtPath = Input(14, 24, 470);
            txtPath.Text = S.GamePath;
            txtPath.TextChanged += (s, e) => { if (!loading) MarkDirty(); };
            var dotPath = Dot(490, 27, Help.GamePath);
            var btnBrowse = Btn("Обзор…", 514, 22, 130, 26, (s, e) =>
            {
                using (var d = new FolderBrowserDialog { Description = "Папка с игрой (где лежит W40k.exe)" })
                    if (d.ShowDialog() == DialogResult.OK) { txtPath.Text = d.SelectedPath; RefreshStatus(); }
            });
            var btnAuto = Btn("Найти автоматически", 14, 52, 170, 24, (s, e) =>
            {
                string p = GameFinder.Find();
                if (p != null) { txtPath.Text = p; Log("Найдена игра: " + p); RefreshStatus(); }
                else Log("[!] Автопоиск не нашёл игру — укажите папку вручную.");
            });
            var lblGame = Lbl("Издание:", 380, 56);
            cmbGame = Combo(450, 52, 194, new object[] { "Базовая игра", "Winter Assault" });
            cmbGame.SelectedIndex = S.Game == "WA" ? 1 : 0;
            grpGame.Controls.AddRange(new Control[] { txtPath, dotPath, btnBrowse, btnAuto, lblGame, cmbGame });
            Controls.Add(grpGame);
            y += 92;

            // --- Widescreen + UI ---
            // Grid: three 28px rows, left column at x=14, right at x=396.
            // Everything in a row is centred on that row baseline (RowY).
            const int wsR1 = 24, wsR2 = 54, wsR3 = 86;   // rows
            const int colL = 14, colR = 374;             // columns

            var grpWs = Group("Widescreen-отрисовка + нерастянутый UI (единый комплект)", 12, y, 660, 122);
            chkWs = Check("Включить отрисовку под разрешение (UI ставится автоматически)", colL, wsR1, 0);
            chkWs.Checked = S.Widescreen;
            var dotWs = DotAfter(chkWs, Help.Widescreen);
            chkWs.CheckedChanged += (s, e) => { ToggleWs(); if (!loading) MarkDirty(); };

            // row 2, left column: resolution
            var lblRes = Lbl("Разрешение:", colL, RowLbl(wsR2));
            cmbRes = Combo(colL + 90, wsR2, 100, new object[] { "3440x1440", "2560x1080", "3840x1600", "5120x1440", "2560x1440", "1920x1080", "другое" });
            numW = Num(colL + 198, wsR2, 60, 640, 10000, S.Width);
            var lblX = Lbl("×", colL + 266, RowLbl(wsR2));
            numH = Num(colL + 284, wsR2, 60, 480, 5000, S.Height);
            string preset = S.Width + "x" + S.Height;
            cmbRes.SelectedItem = cmbRes.Items.Contains(preset) ? preset : "другое";
            cmbRes.SelectedIndexChanged += (s, e) =>
            {
                var m = Regex.Match(cmbRes.SelectedItem.ToString(), "^(\\d+)x(\\d+)$");
                if (m.Success) { numW.Value = int.Parse(m.Groups[1].Value); numH.Value = int.Parse(m.Groups[2].Value); }
            };
            numW.ValueChanged += (s, e) => { if (!loading) MarkDirty(); };
            numH.ValueChanged += (s, e) => { if (!loading) MarkDirty(); };

            // row 2, right column: minimap label, help dot and combo on one row
            var lblExe = Lbl("Мини-карта (exe):", colR, RowLbl(wsR2));
            var dotExe = DotAfter(lblExe, Help.ExeMode);
            cmbExe = Combo(dotExe.Right + 10, wsR2, 130, new object[] { "skip", "compromise", "full" });
            cmbExe.SelectedItem = new[] { "skip", "compromise", "full" }.Contains(S.ExeMode) ? S.ExeMode : "skip";
            cmbExe.SelectedIndexChanged += (s, e) => { if (!loading) MarkDirty(); };

            // row 3: the bottom command panel
            var lblTex = Lbl("Нижняя панель управления:", colL, RowLbl(wsR3));
            var dotTex = DotAfter(lblTex, Help.Textures);
            // "Repainted" is a work in progress and is deliberately locked out:
            // the feature needs artwork derived from the game textures, which is
            // third-party content and is not shipped here. The scripts still
            // support it (Edit-BarTextures.ps1), so custom art can be wired in
            // by hand; the manager just does not offer it as a choice yet.
            radHard = Radio("жёсткая обрезка", dotTex.Right + 10, wsR3 + 1, 150);
            radCustom = Radio("дорисованные (в разработке)", radHard.Right + 12, wsR3 + 1, 200);
            radHard.Checked = true;
            radCustom.Checked = false;
            radCustom.Enabled = false;
            tip.SetToolTip(radCustom, "Функция в разработке — выбор заблокирован.\r\n" +
                "Готового арта в репозитории нет: он делается на основе игровых текстур,\r\n" +
                "а это чужой контент. Подключить свой можно вручную через\r\n" +
                "ui-unstretch\\Edit-BarTextures.ps1 (-Export / -Preview / -Import).");
            radCustom.CheckedChanged += (s, e) => { if (!loading) MarkDirty(); };

            grpWs.Controls.AddRange(new Control[] { chkWs, dotWs, lblRes, cmbRes, numW, lblX, numH,
                lblExe, dotExe, cmbExe, lblTex, dotTex, radCustom, radHard });
            Controls.Add(grpWs);
            y += 130;

            // --- Camera ---
            var grpCam = Group("Камера", 12, y, 660, 56);
            chkZoom = Check("Улучшенный зум (отвод колёсиком дальше)", 14, 24, 0);
            chkZoom.Checked = S.Zoom;
            chkZoom.CheckedChanged += (s, e) => { numDist.Enabled = chkZoom.Checked; if (!loading) MarkDirty(); };
            var dotZoom = DotAfter(chkZoom, Help.Zoom);
            var lblDist = Lbl("DistMax:", 320, 26);
            numDist = Num(380, 22, 80, 38, 300, S.DistMax);
            numDist.ValueChanged += (s, e) => { if (!loading) MarkDirty(); };
            grpCam.Controls.AddRange(new Control[] { chkZoom, dotZoom, lblDist, numDist });
            Controls.Add(grpCam);
            y += 64;

            // --- Language ---
            var grpLang = Group("Язык", 12, y, 660, 120);
            chkRus = Check("Русский язык", 14, 22, 0);
            chkRus.Checked = S.Russian;
            chkRus.CheckedChanged += (s, e) => { UpdateRusUi(); if (!loading) MarkDirty(); };
            var dotRus = DotAfter(chkRus, Help.Russian);
            lblRusState = Lbl("", 190, 24);
            lblRusState.AutoSize = false;
            lblRusState.Size = new Size(450, 18);
            txtRus = Input(14, 52, 470);
            txtRus.ReadOnly = true;
            tip.SetToolTip(txtRus, "Архив русификатора (zip/rar/7z), скачанный по ссылкам из README.\r\nБудет распакован в папку игры при нажатии «Применить».");
            btnRusBrowse = Btn("Указать архив…", 494, 50, 150, 26, (s, e) =>
            {
                using (var d = new OpenFileDialog
                {
                    Title = "Архив русификатора",
                    Filter = "Архивы (*.zip;*.rar;*.7z)|*.zip;*.rar;*.7z"
                           + "|Все поддерживаемые|*.zip;*.rar;*.7z;*.tar;*.gz;*.bz2;*.xz;*.cab;*.iso;*.001"
                           + "|Все файлы (*.*)|*.*"
                })
                    if (d.ShowDialog() == DialogResult.OK)
                    {
                        rusArchive = d.FileName;
                        txtRus.Text = d.FileName;
                        MarkDirty();
                    }
            });

            // Let the user go and fetch the localisation straight from the app.
            btnRusGet = Btn("Скачать русификатор…", 14, 84, 190, 26, (s, e) =>
            {
                try { System.Diagnostics.Process.Start(RusGuideUrl); }
                catch (Exception ex) { Log("Не удалось открыть браузер: " + ex.Message); }
            });
            tip.SetToolTip(btnRusGet,
                "Откроет в браузере Steam-гайд со ссылками на архив русификатора\r\n" +
                "(зеркала Playground / Google Drive / Яндекс.Диск / Облако Mail.ru).\r\n" +
                "Скачайте архив, затем нажмите «Указать архив…».");

            grpLang.Controls.AddRange(new Control[] { chkRus, dotRus, lblRusState, txtRus, btnRusBrowse, btnRusGet });
            Controls.Add(grpLang);
            y += 128;

            // --- Buttons ---
            btnApply = Btn("Применить", 12, y, 140, 34, (s, e) => Run("apply"));
            btnPlay = Btn("Применить и играть", 160, y, 180, 34, (s, e) => Run("launch"));
            btnPlay.Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold);
            btnPlay.FlatStyle = FlatStyle.Flat;
            // the accent button does not grey out by itself, so colour it manually
            btnPlay.EnabledChanged += (s, e) => StylePlayButton();
            btnDefaults = Btn("По умолчанию", 388, y, 140, 34, (s, e) => ResetToDefaults());
            tip.SetToolTip(btnDefaults, "Вернуть параметры окна к рекомендуемым значениям:\r\nразрешение подтягивается с текущего экрана, widescreen+UI,\r\nдорисованные текстуры, зум 76, exe: skip.\r\nНа игру это не влияет, пока не нажать «Применить».");
            btnRestore = Btn("Откат", 536, y, 136, 34, (s, e) => Run("restore"));
            tip.SetToolTip(btnRestore, "Возвращает файлы игры к оригиналу и снимает все галочки в окне.\r\nФайлы русификатора при этом не удаляются — только язык вернётся на английский.");
            Controls.AddRange(new Control[] { btnApply, btnPlay, btnDefaults, btnRestore });
            y += 42;

            // --- Plain "launch the game": always enabled, ignores pending changes ---
            btnLaunchOnly = Btn("▶ Запустить игру", 12, y, 328, 34, (s, e) => Run("launchonly"));
            btnLaunchOnly.Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold);
            tip.SetToolTip(btnLaunchOnly, "Просто запускает игру с тем, что сейчас стоит в файлах — ничего не применяя.\r\nАктивна всегда, пока найдена папка игры: не зависит от изменений в окне.");
            Controls.Add(btnLaunchOnly);
            y += 42;

            // --- Status line and the log toggle ---
            lblStatus = Lbl("", 12, y + 6);
            lblStatus.AutoSize = false;
            lblStatus.Size = new Size(520, 20);
            btnLogToggle = Btn("Скрыть лог ▲", 536, y, 136, 26, (s, e) => ToggleLog());
            Controls.AddRange(new Control[] { lblStatus, btnLogToggle });
            y += 32;

            // --- Log ---
            log = new TextBox
            {
                Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
                Location = new Point(12, y), Size = new Size(660, 220),
                Font = new Font("Consolas", 8.5f), BackColor = th.LogBg, ForeColor = th.LogFg,
                BorderStyle = BorderStyle.FixedSingle
            };
            Controls.Add(log);

            formHeightWithLog = y + 220 + 48;
            formHeightNoLog = y + 12 + 48;
            // the log starts collapsed
            log.Visible = false;
            btnLogToggle.Text = "Показать лог ▼";
            Height = formHeightNoLog;

            ToggleWs();
            numDist.Enabled = chkZoom.Checked;
            UpdateRusUi();
            ApplyTheme();

            if (!File.Exists(launcher))
                Log("[!] Не найден DoW-Launcher.ps1 рядом с приложением (" + root + ").");
        }

        void AddHeaderText()
        {
            var title = new Label
            {
                Text = "Dawn of War — Mod Manager",
                ForeColor = Color.Gainsboro,
                Font = new Font("Segoe UI Semibold", 13f, FontStyle.Bold),
                Location = new Point(16, 18), AutoSize = true, BackColor = Color.Transparent
            };
            header.Controls.Add(title);
        }

        // ---------- Control factories ----------
        GroupBox Group(string text, int x, int y, int w, int h)
        {
            return new GroupBox { Text = text, Location = new Point(x, y), Size = new Size(w, h), ForeColor = th.GroupFg };
        }
        Button Btn(string text, int x, int y, int w, int h, EventHandler onClick)
        {
            var b = new Button { Text = text, Location = new Point(x, y), Size = new Size(w, h) };
            b.Click += onClick;
            return b;
        }
        Label Lbl(string text, int x, int y)
        {
            var l = new Label { Text = text, Location = new Point(x, y), ForeColor = th.Fg, Font = Font, AutoSize = false };
            l.Size = new Size(TextWidth(l) + 2, 17);
            return l;
        }
        CheckBox Check(string text, int x, int y, int w)
        {
            var c = new CheckBox { Text = text, Location = new Point(x, y), ForeColor = th.Fg, Font = Font };
            if (w > 0) c.Size = new Size(w, 22);
            else c.Size = new Size(TextWidth(c) + 22, 22);
            return c;
        }
        // Width of a control's text in its own font
        int TextWidth(Control c)
        {
            return TextRenderer.MeasureText(c.Text, c.Font).Width;
        }
        // A 17px label centred on a row sized for a 24px control
        int RowLbl(int rowY) { return rowY + 4; }
        // Help dot right after the parameter name, uniform gap, vertically centred
        HelpDot DotAfter(Control c, string help)
        {
            int right = c.Left + c.Width;
            int cy = c.Top + (c.Height - 18) / 2;
            return Dot(right + 8, cy, help);
        }
        RadioButton Radio(string text, int x, int y, int w)
        {
            return new RadioButton { Text = text, Location = new Point(x, y), Size = new Size(w, 22), ForeColor = th.Fg };
        }
        TextBox Input(int x, int y, int w)
        {
            return new TextBox { Location = new Point(x, y), Size = new Size(w, 24), BackColor = th.InputBg, ForeColor = th.InputFg, BorderStyle = BorderStyle.FixedSingle };
        }
        ComboBox Combo(int x, int y, int w, object[] items)
        {
            var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Location = new Point(x, y), Size = new Size(w, 24), BackColor = th.InputBg, ForeColor = th.InputFg, FlatStyle = th.Dark ? FlatStyle.Flat : FlatStyle.Standard };
            c.Items.AddRange(items);
            return c;
        }
        NumericUpDown Num(int x, int y, int w, int min, int max, int val)
        {
            return new NumericUpDown { Location = new Point(x, y), Size = new Size(w, 24), Minimum = min, Maximum = max, Value = Math.Min(Math.Max(val, min), max), BackColor = th.InputBg, ForeColor = th.InputFg, BorderStyle = BorderStyle.FixedSingle };
        }
        HelpDot Dot(int x, int y, string text)
        {
            var d = new HelpDot(th, tip, text) { Location = new Point(x, y) };
            dots.Add(d);
            return d;
        }

        void StylePlayButton()
        {
            if (btnPlay.Enabled)
            {
                btnPlay.BackColor = th.Accent;
                btnPlay.ForeColor = th.AccentFg;
                btnPlay.FlatAppearance.BorderColor = th.Accent;
            }
            else
            {
                btnPlay.BackColor = th.Dark ? Color.FromArgb(55, 58, 64) : Color.FromArgb(205, 208, 214);
                btnPlay.ForeColor = th.Muted;
                btnPlay.FlatAppearance.BorderColor = th.Border;
            }
        }

        void ApplyTheme()
        {
            BackColor = th.Bg;
            ForeColor = th.Fg;
            header.BackColor = th.PanelBg;
            foreach (var d in dots) d.SetTheme(th);
            StylePlayButton();
            log.BackColor = th.LogBg;
            log.ForeColor = th.LogFg;
            lblStatus.ForeColor = th.Muted;
            lblRusState.ForeColor = th.Muted;
            if (th.Dark)
            {
                foreach (var b in new[] { btnApply, btnDefaults, btnRestore, btnRusBrowse, btnLogToggle })
                {
                    b.FlatStyle = FlatStyle.Flat;
                    b.BackColor = th.InputBg;
                    b.ForeColor = th.Fg;
                    b.FlatAppearance.BorderColor = th.Border;
                }
            }
        }

        // ---------- State ----------
        void ToggleWs()
        {
            bool on = chkWs.Checked;
            foreach (Control c in new Control[] { cmbRes, numW, numH, radHard, cmbExe })
                c.Enabled = on;
            radCustom.Enabled = false;   // work in progress, never selectable
        }

        // Always reports the REAL locale state, whether or not the box is
        // ticked. It used to blank out when unticked, so there was no way to
        // tell whether a localisation was installed at all. A missing English
        // locale is called out separately: that is what crashed the game when
        // Russian was switched off, since the engine dies if [lang:X] points
        // at nothing.
        void UpdateRusUi()
        {
            bool on = chkRus.Checked;
            bool haveRus = status.LocaleRussian;
            bool haveEng = status.LocaleEnglish;

            // colours that stay readable in both the light and dark themes
            Color okColor   = Color.FromArgb(60, 160, 60);
            Color warnColor = Color.FromArgb(210, 120, 20);

            if (!status.Known)
            {
                lblRusState.Text = "Состояние игры ещё не прочитано.";
                lblRusState.ForeColor = th.Muted;
                txtRus.Enabled = true; btnRusBrowse.Enabled = true;
                return;
            }

            string found = string.IsNullOrEmpty(status.LocalesFound)
                ? "локалей не найдено" : "в игре: " + status.LocalesFound;

            if (haveRus)
            {
                lblRusState.Text = "Русификатор установлен (" + found + ").";
                lblRusState.ForeColor = okColor;
                txtRus.Enabled = false; btnRusBrowse.Enabled = false;
            }
            else
            {
                lblRusState.Text = "Русификатора нет (" + found + ") — укажите архив или скачайте.";
                lblRusState.ForeColor = th.Muted;
                txtRus.Enabled = true; btnRusBrowse.Enabled = true;
            }

            // Warn that switching back to English is not possible
            if (haveRus && !haveEng)
            {
                lblRusState.Text = "Русификатор установлен, английской локали нет.";
                lblRusState.ForeColor = warnColor;
                tip.SetToolTip(lblRusState,
                    "Английская локаль в игре пуста или удалена (её заменил русификатор).\r\n" +
                    "Поэтому выключить русский язык нельзя: игра вылетит при запуске.\r\n" +
                    "Менеджер это заблокирует и не станет портить W40k.ini.\r\n\r\n" +
                    "Чтобы вернуть английский — проверьте целостность файлов игры в Steam\r\n" +
                    "(Свойства -> Установленные файлы -> Проверить целостность).");
                if (!on)
                    lblRusState.Text = "Выключить русский нельзя: английской локали нет (наведите для деталей).";
            }
            else tip.SetToolTip(lblRusState, "");
        }

        void SyncFromUi()
        {
            S.GamePath = txtPath.Text.Trim();
            S.Game = cmbGame.SelectedIndex == 1 ? "WA" : "W40k";
            S.Width = (int)numW.Value;
            S.Height = (int)numH.Value;
            S.Widescreen = chkWs.Checked;
            S.TexturesCustom = radCustom.Checked;
            S.Zoom = chkZoom.Checked;
            S.DistMax = (int)numDist.Value;
            S.Russian = chkRus.Checked;
            S.ExeMode = cmbExe.SelectedItem != null ? cmbExe.SelectedItem.ToString() : "skip";
        }

        // The Apply buttons are enabled only when the window differs from
        // what is actually installed in the game.
        void MarkDirty()
        {
            SyncFromUi();
            if (!gameFound) { CheckGameInstalled(); return; }
            bool dirty = !S.SameAs(applied);
            if (chkRus.Checked && !status.LocaleRussian && !string.IsNullOrEmpty(rusArchive)) dirty = true;
            btnApply.Enabled = dirty && !busy;
            btnPlay.Enabled = dirty && !busy;
            lblStatus.ForeColor = th.Muted;
            lblStatus.Text = dirty ? "Есть несохранённые изменения — нажмите «Применить»."
                                   : "Настройки совпадают с тем, что установлено в игре.";
        }

        // Is the game installed? Everything else is disabled when it is not.
        bool CheckGameInstalled()
        {
            string gp = txtPath.Text.Trim();
            gameFound = !string.IsNullOrEmpty(gp) && File.Exists(Path.Combine(gp, "W40k.exe"));
            foreach (Control c in Controls)
                if (c is GroupBox && c.Text != "Игра") c.Enabled = gameFound;
            btnApply.Enabled = btnPlay.Enabled = btnRestore.Enabled = gameFound && !busy;
            // "Launch game" depends only on the game being present, not on changes
            if (btnLaunchOnly != null) btnLaunchOnly.Enabled = gameFound && !busy;
            if (!gameFound)
            {
                lblStatus.ForeColor = Color.FromArgb(200, 80, 60);
                lblStatus.Text = string.IsNullOrEmpty(gp)
                    ? "Игра не найдена — укажите папку с W40k.exe."
                    : "В этой папке нет W40k.exe — игра не установлена или путь неверный.";
            }
            else lblStatus.ForeColor = th.Muted;
            return gameFound;
        }

        void RefreshStatus()
        {
            if (!CheckGameInstalled())
            {
                Log("[!] " + lblStatus.Text);
                return;
            }
            Log("Определяю текущее состояние игры…");
            SetBusy(true);
            RunPs("-Status", (code, output) =>
            {
                status = GameStatus.Parse(output);
                SetBusy(false);
                if (!status.Known) { Log("[!] Не удалось прочитать состояние игры."); return; }
                loading = true;
                // push the real facts into the window
                chkWs.Checked = status.WidescreenPatched || status.UiInstalled;
                if (status.Width > 0 && status.Height > 0)
                {
                    numW.Value = Math.Min(Math.Max(status.Width, 640), 10000);
                    numH.Value = Math.Min(Math.Max(status.Height, 480), 5000);
                    string p = status.Width + "x" + status.Height;
                    cmbRes.SelectedItem = cmbRes.Items.Contains(p) ? p : "другое";
                }
                if (radCustom.Enabled) radCustom.Checked = status.TexturesCustom;
                radHard.Checked = !radCustom.Checked;
                chkZoom.Checked = status.ZoomInstalled;
                if (status.DistMax >= 38) numDist.Value = Math.Min(status.DistMax, 300);
                numDist.Enabled = chkZoom.Checked;
                chkRus.Checked = status.LangRussian;
                ToggleWs();
                UpdateRusUi();
                SyncFromUi();
                applied = S.Clone();      // this is now "what is installed"
                loading = false;
                MarkDirty();

                var parts = new List<string>();
                parts.Add(status.WidescreenPatched ? "widescreen: стоит" : "widescreen: нет");
                parts.Add(status.UiInstalled ? "UI: стоит" : "UI: нет");
                parts.Add(status.ZoomInstalled ? ("зум: стоит (DistMax " + status.DistMax + ")") : "зум: нет");
                parts.Add(status.LocaleRussian ? (status.LangRussian ? "русский: включён" : "русификатор есть, язык англ.") : "русификатора нет");
                Log("Состояние игры → " + string.Join("; ", parts));
            });
        }

        void ResetToDefaults()
        {
            loading = true;
            var d = new Settings();
            // take the resolution from the monitor the window is on
            int sw, sh;
            ScreenInfo.Get(this, out sw, out sh);
            if (sw >= 640 && sh >= 480) { d.Width = sw; d.Height = sh; }
            chkWs.Checked = d.Widescreen;
            numW.Value = Math.Min(Math.Max(d.Width, 640), 10000);
            numH.Value = Math.Min(Math.Max(d.Height, 480), 5000);
            string dp = d.Width + "x" + d.Height;
            cmbRes.SelectedItem = cmbRes.Items.Contains(dp) ? dp : "другое";
            cmbExe.SelectedItem = d.ExeMode;
            if (radCustom.Enabled) radCustom.Checked = d.TexturesCustom; else radHard.Checked = true;
            chkZoom.Checked = d.Zoom;
            numDist.Value = d.DistMax;
            chkRus.Checked = d.Russian;
            cmbGame.SelectedIndex = 0;
            ToggleWs();
            numDist.Enabled = chkZoom.Checked;
            UpdateRusUi();
            loading = false;
            MarkDirty();
            Log(string.Format("По умолчанию: разрешение текущего экрана {0}x{1}, widescreen+UI, зум {2}, exe: {3}. "
                + "На игру пока не влияет — нажмите «Применить».", d.Width, d.Height, d.DistMax, d.ExeMode));
        }

        // After a rollback clear every box: the window shows "nothing installed"
        void ResetUiAfterRestore()
        {
            loading = true;
            chkWs.Checked = false;
            chkZoom.Checked = false;
            chkRus.Checked = false;
            radHard.Checked = true;
            numDist.Enabled = false;
            rusArchive = ""; txtRus.Text = "";
            ToggleWs();
            UpdateRusUi();
            SyncFromUi();
            applied = S.Clone();
            loading = false;
            MarkDirty();
        }

        void ToggleLog()
        {
            logVisible = !logVisible;
            log.Visible = logVisible;
            btnLogToggle.Text = logVisible ? "Скрыть лог ▲" : "Показать лог ▼";
            Height = logVisible ? formHeightWithLog : formHeightNoLog;
        }

        void SetBusy(bool b)
        {
            busy = b;
            if (b) { btnApply.Enabled = btnPlay.Enabled = btnRestore.Enabled = false; if (btnLaunchOnly != null) btnLaunchOnly.Enabled = false; }
            btnDefaults.Enabled = !b;
            Cursor = b ? Cursors.WaitCursor : Cursors.Default;
            if (!b)
            {
                btnRestore.Enabled = gameFound;
                if (btnLaunchOnly != null) btnLaunchOnly.Enabled = gameFound;
                MarkDirty();
            }
        }

        // ---------- Backend invocation ----------
        void RunPs(string modeArg, Action<int, string> onDone)
        {
            if (!File.Exists(launcher)) { Log("[!] DoW-Launcher.ps1 не найден."); if (onDone != null) onDone(1, ""); return; }
            string gp = txtPath.Text.Trim().Replace("'", "''");
            string psCmd = "[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; & '"
                + launcher.Replace("'", "''") + "' " + modeArg + " -GamePath '" + gp + "'";
            if (modeArg != "-Status" && !string.IsNullOrEmpty(rusArchive))
                psCmd += " -RussianArchive '" + rusArchive.Replace("'", "''") + "'";

            var psi = new ProcessStartInfo("powershell.exe",
                "-NoProfile -ExecutionPolicy Bypass -Command \"" + psCmd.Replace("\"", "\\\"") + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = root
            };
            var sb = new StringBuilder();
            bool quiet = modeArg == "-Status";
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.OutputDataReceived += (s, e) =>
            {
                if (e.Data == null) return;
                sb.AppendLine(e.Data);
                if (!quiet) Log(e.Data);
            };
            p.ErrorDataReceived += (s, e) => { if (e.Data != null) Log("[err] " + e.Data); };
            p.Exited += (s, e) => BeginInvoke((Action)(() => { if (onDone != null) onDone(p.ExitCode, sb.ToString()); }));
            try
            {
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                Log("[!] Не удалось запустить powershell: " + ex.Message);
                if (onDone != null) onDone(1, "");
            }
        }

        void Run(string mode)
        {
            if (busy) return;
            SyncFromUi();
            try { S.Save(cfgPath); }
            catch (Exception ex) { Log("[!] Не удалось сохранить настройки: " + ex.Message); return; }

            string modeArg = mode == "launch" ? "-Launch"
                           : mode == "launchonly" ? "-LaunchOnly"
                           : mode == "restore" ? "-RestoreAll" : "-Apply";
            Log("");
            Log("==== " + (mode == "launch" ? "Применяю и запускаю игру"
                       : mode == "launchonly" ? "Запускаю игру"
                       : mode == "restore" ? "Полный откат" : "Применяю настройки") + " ====");
            SetBusy(true);
            RunPs(modeArg, (code, output) =>
            {
                Log("==== Готово (код выхода " + code + ") ====");
                SetBusy(false);
                if (mode == "restore") { ResetUiAfterRestore(); RefreshStatus(); }
                else if (mode != "launchonly") RefreshStatus();   // a plain launch changes nothing
            });
        }

        void Log(string msg)
        {
            if (log.InvokeRequired) { log.BeginInvoke((Action<string>)Log, msg); return; }
            log.AppendText(msg + "\r\n");
            log.SelectionStart = log.TextLength;
            log.ScrollToCaret();
        }
    }

    // ---------- Headless self-test ----------
    static class SelfTest
    {
        public static void Run()
        {
            string tmp = Path.Combine(Path.GetTempPath(), "dowmm-selftest.json");
            var a = new Settings { GamePath = @"D:\Games\Dawn of War Gold", Width = 3440, Height = 1440, Russian = true, DistMax = 90, ExeMode = "compromise", TexturesCustom = false };
            a.Save(tmp);
            var b = Settings.Load(tmp);
            bool ok = b.GamePath == a.GamePath && b.Width == a.Width && b.Height == a.Height
                && b.Russian == a.Russian && b.DistMax == a.DistMax
                && b.ExeMode == a.ExeMode && b.TexturesCustom == a.TexturesCustom;
            Console.WriteLine("settings roundtrip: " + (ok ? "OK" : "FAIL"));

            string js = "{\"GamePath\":\"D:\\\\g\",\"WidescreenPatched\":true,\"UiInstalled\":true,\"TexturesCustom\":false,\"ZoomInstalled\":true,\"DistMax\":76,\"LocaleRussian\":true,\"LocaleEnglish\":false,\"LocalesFound\":\"Russian\",\"LangRussian\":false,\"Width\":3440,\"Height\":1440}";
            var st = GameStatus.Parse(js);
            bool ok2 = st.Known && st.WidescreenPatched && st.UiInstalled && !st.TexturesCustom
                && st.ZoomInstalled && st.DistMax == 76 && st.LocaleRussian && !st.LangRussian
                && st.Width == 3440 && st.Height == 1440;
            Console.WriteLine("status parse: " + (ok2 ? "OK" : "FAIL"));

            // locales: the "localisation present, English locale missing" case
            // that used to crash the game when Russian was switched off
            bool ok2b = st.LocaleRussian && !st.LocaleEnglish && st.LocalesFound == "Russian";
            var stEmpty = GameStatus.Parse("{\"LocaleRussian\":false,\"LocaleEnglish\":true,\"LocalesFound\":\"English\"}");
            ok2b = ok2b && !stEmpty.LocaleRussian && stEmpty.LocaleEnglish && stEmpty.LocalesFound == "English";
            Console.WriteLine("locale state parse: " + (ok2b ? "OK" : "FAIL"));

            var s1 = new Settings(); var s2 = new Settings();
            bool ok3 = s1.SameAs(s2); s2.Zoom = !s2.Zoom; ok3 = ok3 && !s1.SameAs(s2);
            Console.WriteLine("dirty compare: " + (ok3 ? "OK" : "FAIL"));

            int sw, sh;
            ScreenInfo.Get(null, out sw, out sh);
            bool ok4 = sw >= 640 && sh >= 480;
            Console.WriteLine("screen detect: " + (ok4 ? "OK" : "FAIL") + " -> " + sw + "x" + sh);

            try { File.Delete(tmp); } catch { }
            Environment.ExitCode = (ok && ok2 && ok3 && ok4) ? 0 : 1;
        }
    }
}
