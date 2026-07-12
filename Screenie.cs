// Screenie 1.0 — snap a region, jot a note, route the pair to your project.
// Build: run build.bat (uses the csc.exe that ships with Windows / .NET Framework 4.x)
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ScreenieApp
{
    static class Program
    {
        [DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")]
        static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int RegisterWindowMessage(string name);
        [DllImport("user32.dll")]
        public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        // broadcast by a second launch so the running instance opens its window
        public static readonly int WM_SHOWME = RegisterWindowMessage("Screenie_ShowMe");

        // per-monitor v2 so captures aren't scaled/mis-cropped on mixed-DPI monitor setups;
        // system-DPI-aware fallback for pre-1703 Windows
        static void SetDpiAwareness()
        {
            try
            {
                if (SetProcessDpiAwarenessContext(new IntPtr(-4))) return; // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
            }
            catch (EntryPointNotFoundException) { }
            SetProcessDPIAware();
        }

        [STAThread]
        static void Main(string[] args)
        {
            SetDpiAwareness();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            bool trayStart = false;
            foreach (string a in args) if (a == "--tray") trayStart = true;
            using (System.Threading.Mutex mtx = new System.Threading.Mutex(false, "ScreenieSingleInstance"))
            {
                if (!mtx.WaitOne(0, false))
                {
                    PostMessage((IntPtr)0xffff, WM_SHOWME, IntPtr.Zero, IntPtr.Zero);
                    return;
                }
                Application.Run(new MainForm(trayStart));
            }
        }
    }

    class Config
    {
        public string Folder = "";
        public List<string> Recent = new List<string>();
        public string Hotkey = "F5";
        public bool FirstRun = false;

        static string Dir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Screenie"); } }
        static string FilePath { get { return Path.Combine(Dir, "config.ini"); } }

        public static Config Load()
        {
            Config c = new Config();
            if (!File.Exists(FilePath))
            {
                c.FirstRun = true;
                c.Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Screenie", "inbox");
                return c;
            }
            foreach (string line in File.ReadAllLines(FilePath))
            {
                int i = line.IndexOf('=');
                if (i < 1) continue;
                string k = line.Substring(0, i).Trim();
                string v = line.Substring(i + 1).Trim();
                if (k == "folder") c.Folder = v;
                else if (k == "hotkey") c.Hotkey = v;
                else if (k == "recent" && v.Length > 0) c.Recent.AddRange(v.Split('|'));
            }
            if (c.Folder.Length == 0)
                c.Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Screenie", "inbox");
            return c;
        }

        public void Save()
        {
            Directory.CreateDirectory(Dir);
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("folder=" + Folder);
            sb.AppendLine("hotkey=" + Hotkey);
            sb.AppendLine("recent=" + string.Join("|", Recent.ToArray()));
            File.WriteAllText(FilePath, sb.ToString());
        }

        public void Touch(string folder)
        {
            Folder = folder;
            Recent.RemoveAll(delegate(string s) { return string.Equals(s, folder, StringComparison.OrdinalIgnoreCase); });
            Recent.Insert(0, folder);
            if (Recent.Count > 8) Recent.RemoveRange(8, Recent.Count - 8);
            Save();
        }
    }

    class MainForm : Form
    {
        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
        const int WM_HOTKEY = 0x0312;
        const int WM_SYSCOMMAND = 0x0112;
        const int SC_MINIMIZE = 0xF020;
        const int SW_RESTORE = 9;
        const int HOTKEY_ID = 1;
        const string RUN_KEY = @"Software\Microsoft\Windows\CurrentVersion\Run";

        Config cfg;
        NotifyIcon tray;
        ToolStripMenuItem routeMenu;
        TextBox folderBox;
        TextBox hotkeyBox;
        CheckBox startupChk;
        StatusBar status;
        bool allowVisible;
        bool loadingUi;
        bool capturing;
        bool hotkeyOk;
        uint hkMods, hkVk;
        string hkDisplay;

        public MainForm(bool startInTray)
        {
            cfg = Config.Load();
            allowVisible = !startInTray || cfg.FirstRun;
            ParseHotkey(cfg.Hotkey);
            BuildUi();
            BuildTray();

            loadingUi = true;
            startupChk.Checked = IsStartupEnabled();
            loadingUi = false;

            if (cfg.FirstRun)
            {
                try { Directory.CreateDirectory(cfg.Folder); } catch { }
                cfg.Touch(cfg.Folder);
                RebuildRouteMenu();
                loadingUi = true;
                SetStartup(true);
                startupChk.Checked = true;
                loadingUi = false;
            }

            IntPtr h = this.Handle; // force handle creation so the hotkey works while hidden
            hotkeyOk = RegisterHotKey(h, HOTKEY_ID, hkMods, hkVk);
            if (!hotkeyOk)
                status.Text = "Could not grab " + hkDisplay + " — another app owns it. Pick a different hotkey below.";
        }

        void ParseHotkey(string spec)
        {
            hkMods = 0; hkVk = 0x74; hkDisplay = "F5";
            if (string.IsNullOrEmpty(spec)) return;
            string[] parts = spec.ToUpperInvariant().Split('+');
            uint mods = 0; uint vk = 0; string keyName = "";
            foreach (string raw in parts)
            {
                string p = raw.Trim();
                if (p == "CTRL" || p == "CONTROL") mods |= 2;
                else if (p == "ALT") mods |= 1;
                else if (p == "SHIFT") mods |= 4;
                else if (p == "WIN") mods |= 8;
                else keyName = p;
            }
            if (keyName.Length >= 2 && keyName[0] == 'F')
            {
                int n;
                if (int.TryParse(keyName.Substring(1), out n) && n >= 1 && n <= 12) vk = (uint)(0x6F + n);
            }
            else if (keyName.Length == 1 && ((keyName[0] >= 'A' && keyName[0] <= 'Z') || (keyName[0] >= '0' && keyName[0] <= '9')))
            {
                vk = (uint)keyName[0];
            }
            else if (keyName == "PRINTSCREEN" || keyName == "PRTSC")
            {
                vk = 0x2C;
            }
            if (vk != 0) { hkMods = mods; hkVk = vk; hkDisplay = spec.ToUpperInvariant(); }
        }

        void BuildUi()
        {
            Text = "Screenie 1.0";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            Font = new Font("Tahoma", 8.25f);
            ClientSize = new Size(434, 252);
            BackColor = SystemColors.Control;
            StartPosition = FormStartPosition.CenterScreen;

            Panel banner = new Panel();
            banner.BackColor = Color.White;
            banner.BorderStyle = BorderStyle.FixedSingle;
            banner.SetBounds(-1, -1, 437, 58);
            Label title = new Label();
            title.Text = "Screenie";
            title.Font = new Font("Tahoma", 14f, FontStyle.Bold);
            title.AutoSize = true;
            title.Location = new Point(12, 15);
            title.BackColor = Color.White;
            banner.Controls.Add(title);

            GroupBox grp = new GroupBox();
            grp.Text = "Route screenshots + notes to";
            grp.SetBounds(10, 66, 414, 58);
            folderBox = new TextBox();
            folderBox.ReadOnly = true;
            folderBox.SetBounds(10, 22, 310, 21);
            folderBox.Text = cfg.Folder;
            Button browse = new Button();
            browse.Text = "Browse...";
            browse.FlatStyle = FlatStyle.System;
            browse.SetBounds(328, 20, 76, 23);
            browse.Click += OnBrowse;
            grp.Controls.Add(folderBox);
            grp.Controls.Add(browse);

            GroupBox hkGrp = new GroupBox();
            hkGrp.Text = "Global hotkey";
            hkGrp.SetBounds(10, 132, 414, 58);
            hotkeyBox = new TextBox();
            hotkeyBox.ReadOnly = true;
            hotkeyBox.BackColor = Color.White;
            hotkeyBox.TextAlign = HorizontalAlignment.Center;
            hotkeyBox.SetBounds(10, 22, 100, 21);
            hotkeyBox.Text = hkDisplay;
            hotkeyBox.KeyDown += OnHotkeyBoxKeyDown;
            Label hkHint = new Label();
            hkHint.Text = "Click the box, then press the shortcut you want\r\n(e.g. Ctrl+F5). Applies instantly.";
            hkHint.ForeColor = SystemColors.ControlDarkDark;
            hkHint.SetBounds(120, 17, 288, 34);
            hkGrp.Controls.Add(hotkeyBox);
            hkGrp.Controls.Add(hkHint);

            startupChk = new CheckBox();
            startupChk.Text = "Start Screenie with Windows (runs in the tray)";
            startupChk.SetBounds(14, 200, 406, 18);
            startupChk.CheckedChanged += OnStartupChanged;

            status = new StatusBar();
            status.SizingGrip = false;
            status.Text = "Ready — press " + hkDisplay + " to snap.";

            Controls.Add(banner);
            Controls.Add(grp);
            Controls.Add(hkGrp);
            Controls.Add(startupChk);
            Controls.Add(status);

            Bitmap ib = new Bitmap(16, 16);
            using (Graphics g = Graphics.FromImage(ib))
            {
                g.FillRectangle(Brushes.DimGray, 0, 0, 16, 16);
                g.DrawRectangle(Pens.White, 0, 0, 15, 15);
                using (Font f = new Font("Tahoma", 8f, FontStyle.Bold))
                    g.DrawString("S", f, Brushes.White, 3, 1);
            }
            this.Icon = Icon.FromHandle(ib.GetHicon());
        }

        void BuildTray()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("Open Screenie", null, delegate { ShowUi(); });
            menu.Items.Add("Take snap now", null, delegate { BeginCapture(); });
            routeMenu = new ToolStripMenuItem("Route to");
            menu.Items.Add(routeMenu);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, delegate { ExitApp(); });

            tray = new NotifyIcon();
            tray.Icon = this.Icon;
            tray.Text = "Screenie — " + hkDisplay + " to snap";
            tray.ContextMenuStrip = menu;
            tray.Visible = true;
            tray.MouseClick += delegate(object s, MouseEventArgs e)
            {
                if (e.Button == MouseButtons.Left) ShowUi();
            };
            tray.DoubleClick += delegate { ShowUi(); };
            RebuildRouteMenu();
        }

        void OnHotkeyBoxKeyDown(object sender, KeyEventArgs e)
        {
            e.Handled = true;
            e.SuppressKeyPress = true;
            Keys k = e.KeyCode;
            if (k == Keys.ControlKey || k == Keys.ShiftKey || k == Keys.Menu || k == Keys.LWin || k == Keys.RWin)
                return; // wait for the real key
            string keyName = KeyNameFor(k);
            if (keyName == null)
            {
                status.Text = "That key isn't supported — use F1-F12, A-Z, 0-9 or PrintScreen (with optional Ctrl/Alt/Shift).";
                return;
            }
            StringBuilder sb = new StringBuilder();
            if (e.Control) sb.Append("CTRL+");
            if (e.Alt) sb.Append("ALT+");
            if (e.Shift) sb.Append("SHIFT+");
            sb.Append(keyName);
            ApplyHotkey(sb.ToString());
        }

        static string KeyNameFor(Keys k)
        {
            if (k >= Keys.F1 && k <= Keys.F12) return k.ToString().ToUpperInvariant();
            if (k >= Keys.A && k <= Keys.Z) return k.ToString().ToUpperInvariant();
            if (k >= Keys.D0 && k <= Keys.D9) return ((char)('0' + (k - Keys.D0))).ToString();
            if (k == Keys.PrintScreen) return "PRINTSCREEN";
            return null;
        }

        void ApplyHotkey(string spec)
        {
            uint oldMods = hkMods, oldVk = hkVk;
            string oldDisp = hkDisplay;
            if (hotkeyOk) { UnregisterHotKey(this.Handle, HOTKEY_ID); hotkeyOk = false; }
            ParseHotkey(spec);
            hotkeyOk = RegisterHotKey(this.Handle, HOTKEY_ID, hkMods, hkVk);
            if (hotkeyOk)
            {
                cfg.Hotkey = hkDisplay;
                cfg.Save();
                status.Text = "Hotkey set to " + hkDisplay + ".";
            }
            else
            {
                hkMods = oldMods; hkVk = oldVk; hkDisplay = oldDisp;
                hotkeyOk = RegisterHotKey(this.Handle, HOTKEY_ID, hkMods, hkVk);
                MessageBox.Show(this, "Couldn't grab " + spec + " — another app already owns it.\r\nKept " + oldDisp + ".", "Screenie");
            }
            hotkeyBox.Text = hkDisplay;
            tray.Text = "Screenie — " + hkDisplay + " to snap";
        }

        void RebuildRouteMenu()
        {
            routeMenu.DropDownItems.Clear();
            foreach (string dir in cfg.Recent)
            {
                ToolStripMenuItem it = new ToolStripMenuItem(dir);
                it.Checked = string.Equals(dir, cfg.Folder, StringComparison.OrdinalIgnoreCase);
                string captured = dir;
                it.Click += delegate { SetFolder(captured); };
                routeMenu.DropDownItems.Add(it);
            }
            if (routeMenu.DropDownItems.Count > 0)
                routeMenu.DropDownItems.Add(new ToolStripSeparator());
            routeMenu.DropDownItems.Add(new ToolStripMenuItem("Browse...", null, delegate { OnBrowse(null, EventArgs.Empty); }));
        }

        void SetFolder(string path)
        {
            cfg.Touch(path);
            folderBox.Text = path;
            RebuildRouteMenu();
            status.Text = "Routing to " + path;
        }

        void OnBrowse(object sender, EventArgs e)
        {
            using (FolderBrowserDialog fb = new FolderBrowserDialog())
            {
                fb.Description = "Where should Screenie drop screenshots and notes?";
                if (Directory.Exists(cfg.Folder)) fb.SelectedPath = cfg.Folder;
                if (fb.ShowDialog(this) == DialogResult.OK) SetFolder(fb.SelectedPath);
            }
        }

        // ---- run-at-startup ----
        bool IsStartupEnabled()
        {
            using (RegistryKey rk = Registry.CurrentUser.OpenSubKey(RUN_KEY, false))
                return rk != null && rk.GetValue("Screenie") != null;
        }

        void SetStartup(bool on)
        {
            using (RegistryKey rk = Registry.CurrentUser.OpenSubKey(RUN_KEY, true))
            {
                if (rk == null) return;
                if (on) rk.SetValue("Screenie", "\"" + Application.ExecutablePath + "\" --tray");
                else if (rk.GetValue("Screenie") != null) rk.DeleteValue("Screenie");
            }
        }

        void OnStartupChanged(object sender, EventArgs e)
        {
            if (loadingUi) return;
            SetStartup(startupChk.Checked);
            status.Text = startupChk.Checked ? "Screenie will start with Windows." : "Autostart disabled.";
        }

        // ---- capture flow ----
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY && (int)m.WParam == HOTKEY_ID)
            {
                BeginCapture();
                return;
            }
            if (m.Msg == Program.WM_SHOWME)
            {
                ShowUi();
                return;
            }
            // minimize acts like X: tuck into the tray, never a limbo minimized state
            if (m.Msg == WM_SYSCOMMAND && (m.WParam.ToInt64() & 0xFFF0) == SC_MINIMIZE)
            {
                Hide();
                return;
            }
            base.WndProc(ref m);
        }

        void BeginCapture()
        {
            if (capturing) return;
            capturing = true;
            try
            {
                Rectangle vb = SystemInformation.VirtualScreen;
                using (Bitmap shot = new Bitmap(vb.Width, vb.Height, PixelFormat.Format32bppArgb))
                {
                    using (Graphics g = Graphics.FromImage(shot))
                        g.CopyFromScreen(vb.X, vb.Y, 0, 0, vb.Size, CopyPixelOperation.SourceCopy);

                    Rectangle sel = Rectangle.Empty;
                    using (CaptureForm cf = new CaptureForm(shot, vb))
                    {
                        if (cf.ShowDialog() == DialogResult.OK) sel = cf.Selection;
                    }
                    if (sel.Width >= 4 && sel.Height >= 4)
                    {
                        using (Bitmap crop = shot.Clone(sel, PixelFormat.Format24bppRgb))
                            SaveSnap(crop);
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Snap failed: " + ex.Message, "Screenie");
            }
            finally { capturing = false; }
        }

        void SaveSnap(Bitmap crop)
        {
            Directory.CreateDirectory(cfg.Folder);
            string baseName = "snap_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string png = Path.Combine(cfg.Folder, baseName + ".png");
            int n = 2;
            while (File.Exists(png))
            {
                png = Path.Combine(cfg.Folder, baseName + "_" + n + ".png");
                n++;
            }
            baseName = Path.GetFileNameWithoutExtension(png);
            crop.Save(png, ImageFormat.Png);
            try { Clipboard.SetImage(crop); } catch { }

            string note = null;
            using (NoteForm nf = new NoteForm(baseName + ".png"))
            {
                if (nf.ShowDialog() == DialogResult.OK) note = nf.NoteText;
            }
            bool hasNote = note != null && note.Trim().Length > 0;
            if (hasNote)
            {
                File.WriteAllText(Path.Combine(cfg.Folder, baseName + ".txt"),
                    "[screenshot: " + baseName + ".png]\r\n\r\n" + note.Trim() + "\r\n", Encoding.UTF8);
            }
            status.Text = "Saved " + baseName + (hasNote ? ".png + .txt" : ".png");
        }

        // ---- window lifecycle ----
        protected override void SetVisibleCore(bool value)
        {
            base.SetVisibleCore(allowVisible && value);
        }

        void ShowUi()
        {
            allowVisible = true;
            WindowState = FormWindowState.Normal;
            if (!Visible) Show();
            ShowWindow(this.Handle, SW_RESTORE);
            // pulse TopMost so Windows' foreground lock can't leave us buried
            TopMost = true;
            TopMost = false;
            BringToFront();
            SetForegroundWindow(this.Handle);
            Activate();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            Cleanup();
            base.OnFormClosing(e);
        }

        void Cleanup()
        {
            if (hotkeyOk) { UnregisterHotKey(this.Handle, HOTKEY_ID); hotkeyOk = false; }
            if (tray != null) tray.Visible = false;
        }

        void ExitApp()
        {
            Cleanup();
            Application.Exit();
        }
    }

    class CaptureForm : Form
    {
        Bitmap shot;
        Point start, cur;
        bool dragging;
        public Rectangle Selection = Rectangle.Empty;

        public CaptureForm(Bitmap shot, Rectangle vb)
        {
            this.shot = shot;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            Bounds = vb;
            TopMost = true;
            ShowInTaskbar = false;
            Cursor = Cursors.Cross;
            KeyPreview = true;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) DialogResult = DialogResult.Cancel;
            base.OnKeyDown(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                dragging = true;
                start = e.Location;
                cur = e.Location;
                Invalidate();
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (dragging) { cur = e.Location; Invalidate(); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (!dragging) return;
            dragging = false;
            Rectangle r = MakeRect(start, e.Location);
            r.Intersect(new Rectangle(Point.Empty, Bounds.Size));
            if (r.Width >= 4 && r.Height >= 4)
            {
                Selection = r;
                DialogResult = DialogResult.OK;
            }
            else Invalidate();
        }

        static Rectangle MakeRect(Point a, Point b)
        {
            return Rectangle.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.DrawImageUnscaled(shot, 0, 0);
            Rectangle sel = dragging ? MakeRect(start, cur) : Rectangle.Empty;
            using (SolidBrush dim = new SolidBrush(Color.FromArgb(110, 0, 0, 0)))
            {
                if (sel.Width > 0 && sel.Height > 0)
                {
                    using (Region reg = new Region(ClientRectangle))
                    {
                        reg.Exclude(sel);
                        g.FillRegion(dim, reg);
                    }
                    using (Pen p = new Pen(Color.FromArgb(51, 153, 255), 2f))
                        g.DrawRectangle(p, sel);
                    string size = sel.Width + " x " + sel.Height;
                    using (Font f = new Font("Tahoma", 8f))
                    {
                        SizeF ts = g.MeasureString(size, f);
                        float tx = sel.X;
                        float ty = sel.Y - ts.Height - 4;
                        if (ty < 0) ty = sel.Y + 4;
                        using (SolidBrush bg = new SolidBrush(Color.FromArgb(200, 0, 0, 0)))
                            g.FillRectangle(bg, tx, ty, ts.Width + 6, ts.Height + 2);
                        g.DrawString(size, f, Brushes.White, tx + 3, ty + 1);
                    }
                }
                else
                {
                    g.FillRectangle(dim, ClientRectangle);
                    string hint = "Drag to select a region  •  Esc to cancel";
                    using (Font f = new Font("Tahoma", 10f, FontStyle.Bold))
                    {
                        SizeF ts = g.MeasureString(hint, f);
                        float tx = (ClientSize.Width - ts.Width) / 2f;
                        using (SolidBrush bg = new SolidBrush(Color.FromArgb(200, 0, 0, 0)))
                            g.FillRectangle(bg, tx - 8, 24, ts.Width + 16, ts.Height + 8);
                        g.DrawString(hint, f, Brushes.White, tx, 28);
                    }
                }
            }
        }
    }

    class NoteForm : Form
    {
        TextBox box;
        public string NoteText { get { return box.Text; } }

        public NoteForm(string pngName)
        {
            Text = "Screenie — Note";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            TopMost = true;
            Font = new Font("Tahoma", 8.25f);
            ClientSize = new Size(400, 196);
            BackColor = SystemColors.Control;

            Label lbl = new Label();
            lbl.Text = "Note for " + pngName + ":";
            lbl.SetBounds(10, 8, 380, 16);

            box = new TextBox();
            box.Multiline = true;
            box.AcceptsReturn = false; // Enter = Save, Ctrl+Enter = new line
            box.ScrollBars = ScrollBars.Vertical;
            box.SetBounds(10, 28, 380, 110);

            Label hint = new Label();
            hint.Text = "Enter = save     Ctrl+Enter = new line     Esc = skip note";
            hint.ForeColor = SystemColors.GrayText;
            hint.SetBounds(10, 144, 380, 14);

            Button ok = new Button();
            ok.Text = "Save";
            ok.DialogResult = DialogResult.OK;
            ok.FlatStyle = FlatStyle.System;
            ok.SetBounds(233, 165, 76, 23);

            Button skip = new Button();
            skip.Text = "Skip";
            skip.DialogResult = DialogResult.Cancel;
            skip.FlatStyle = FlatStyle.System;
            skip.SetBounds(315, 165, 76, 23);

            Controls.Add(lbl);
            Controls.Add(box);
            Controls.Add(hint);
            Controls.Add(ok);
            Controls.Add(skip);
            AcceptButton = ok;
            CancelButton = skip;

            StartPosition = FormStartPosition.Manual;
            Rectangle wa = Screen.FromPoint(Cursor.Position).WorkingArea;
            Location = new Point(wa.X + (wa.Width - Width) / 2, wa.Y + (wa.Height - Height) / 2);
            Shown += delegate { Activate(); box.Focus(); };
        }
    }
}
