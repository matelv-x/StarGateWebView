using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Text.Json;

namespace StarGateWebView
{
    public partial class MainForm : Form
    {
        private readonly WebView2 webView = new WebView2();

        // Hotspots (TRANSPARENT)
        private readonly TransparentPanel dragCorner = new TransparentPanel();   // TOP-LEFT (drag)
        private readonly TransparentPanel resizeCorner = new TransparentPanel(); // BOTTOM-RIGHT (resize)
        private readonly TransparentPanel menuCorner = new TransparentPanel();   // BOTTOM-LEFT (menu button)

        private readonly ToolTip cornerToolTip = new ToolTip
        {
            ShowAlways = true,
            AutomaticDelay = 200,
            AutoPopDelay = 8000,
            InitialDelay = 200,
            ReshowDelay = 100
        };

        private const int CornerSize = 26;
        private static readonly Size MenuButtonSize = new Size(40, 20);

        private const string DefaultHost = "stargate.local";
        private const string PathDial9 = "/retro/dial9.html";
        private const string PathDial = "/retro/dial.html";

        private bool resizing;
        private Point resizeStartMouse;
        private Size resizeStartSize;

        private Uri? startUri;
        private bool isFullscreen;
        private FormBorderStyle prevBorderStyle;
        private FormWindowState prevWindowState;
        private Rectangle prevBounds;
        private bool prevTopMost;

        private const int HOTKEY_ID_F11 = 0xB001;
        private const int HOTKEY_ID_F11_FALLBACK = 0xB003;
        private const int HOTKEY_ID_ESC = 0xB002;
        private bool f11Registered;

        // Prevent menu re-opening loop
        private bool menuOpen;

        private static readonly string ConfigPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "StarGate_Launcher", "config.json");

        // Visual feedback on menu click
        private static readonly Color BLINK_BLUE = Color.FromArgb(0x30, 0x80, 0xFF);

        // Hover glow states
        private bool dragHover;
        private bool resizeHover;
        private bool menuHover;

        // Menu theme
        private static readonly Color MENU_BG = Color.FromArgb(5, 21, 42);
        private static readonly Color MENU_FG = Color.Gainsboro;
        private static readonly Color MENU_BORDER = MENU_BG; // invisible border

        private const int MENU_RADIUS = 10;
        private const int LEGEND_RADIUS = 10;
        private const int LEGEND_MAX_WIDTH = 420;

        // Dynamic UI scaling for menu + legend
        private const float UI_BASE_W = 1200f;
        private const float UI_MIN_SCALE = 0.75f;
        private const float UI_MAX_SCALE = 1.05f;

        private float GetUiScale()
        {
            float s = ClientSize.Width / UI_BASE_W;
            if (s < UI_MIN_SCALE) s = UI_MIN_SCALE;
            if (s > UI_MAX_SCALE) s = UI_MAX_SCALE;
            return s;
        }

        private static int ScaledInt(int v, float s) => Math.Max(1, (int)Math.Round(v * s));

        private static Font ScaleFont(Font baseFont, float s)
        {
            float newSize = Math.Max(8.5f, baseFont.Size * s); // slightly higher min size
            return new Font(baseFont.FontFamily, newSize, baseFont.Style);
        }

        public MainForm()
        {
            EnsureConfigFileExists();

            Text = "StarGate WebView";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            MinimumSize = new Size(600, 400);
            Size = new Size(1200, 800);
            BackColor = Color.FromArgb(5, 21, 42);
            KeyPreview = true;
            AutoScaleMode = AutoScaleMode.Dpi;

            try
            {
                var icoPath = Path.Combine(AppContext.BaseDirectory, "stargate.ico");
                if (File.Exists(icoPath)) Icon = new Icon(icoPath);
            }
            catch { }

            webView.Dock = DockStyle.Fill;
            webView.DefaultBackgroundColor = BackColor;
            webView.Margin = Padding.Empty;
            webView.Padding = Padding.Empty;
            Controls.Add(webView);

            InitializeCorners();

            Resize += (_, _) => UpdateCornerPositions();

            Shown += async (_, _) =>
            {
                if (!RestoreWindowPlacement())
                    MoveToBottomRight();

                SyncFullscreenStateFromCurrentBounds();

                startUri = ShowStartupChoiceAndGetUri();
                if (startUri == null)
                {
                    Close();
                    return;
                }

                await InitWebView2Async(startUri);
            };
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                webView?.Dispose();
                cornerToolTip?.Dispose();
            }
            base.Dispose(disposing);
        }

        // Fallback for F11 / ESC when hotkeys are blocked or WebView eats input
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.F11)
            {
                ToggleFullscreen();
                return true;
            }
            if (keyData == Keys.Escape && isFullscreen)
            {
                ToggleFullscreen();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        private void SyncFullscreenStateFromCurrentBounds()
        {
            try
            {
                var screenBounds = Screen.FromControl(this).Bounds;
                bool looksFullscreen =
                    Math.Abs(Bounds.X - screenBounds.X) <= 2 &&
                    Math.Abs(Bounds.Y - screenBounds.Y) <= 2 &&
                    Math.Abs(Bounds.Width - screenBounds.Width) <= 2 &&
                    Math.Abs(Bounds.Height - screenBounds.Height) <= 2;

                if (!looksFullscreen) return;

                if (!isFullscreen)
                {
                    prevBorderStyle = FormBorderStyle;
                    prevWindowState = WindowState;
                    prevTopMost = TopMost;

                    var wa = Screen.FromControl(this).WorkingArea;
                    int w = 1200, h = 800;
                    int x = wa.Right - w;
                    int y = wa.Bottom - h;
                    prevBounds = new Rectangle(x, y, w, h);

                    isFullscreen = true;
                    TopMost = true;

                    NativeHotKey.RegisterHotKey(Handle, HOTKEY_ID_ESC, 0, Keys.Escape);
                }
            }
            catch { }
        }

        // ──────────────────────────────────────────────
        // CORNER HOTSPOTS
        // ──────────────────────────────────────────────

        // Transparent panel used ONLY for hotspot "keys"
        private sealed class TransparentPanel : Panel
        {
            public TransparentPanel()
            {
                SetStyle(ControlStyles.Opaque, true);
                SetStyle(ControlStyles.AllPaintingInWmPaint, true);
                SetStyle(ControlStyles.UserPaint, true);
                SetStyle(ControlStyles.OptimizedDoubleBuffer, true);
                BackColor = Color.Transparent;
                TabStop = false;
            }

            protected override CreateParams CreateParams
            {
                get
                {
                    var cp = base.CreateParams;
                    cp.ExStyle |= 0x00000020; // WS_EX_TRANSPARENT
                    return cp;
                }
            }

            protected override void OnPaintBackground(PaintEventArgs e)
            {
                // No background paint = transparent
            }
        }

        private void InitializeCorners()
        {
            // Drag area (top-left)
            SetupCorner(dragCorner, Cursors.SizeAll, AnchorStyles.Top | AnchorStyles.Left, 0, 0);
            ApplyRoundedCorners(dragCorner, 7);
            dragCorner.Paint += (_, e) => PaintHotspot(e.Graphics, dragCorner.ClientRectangle, dragHover, drawHamburger: false);
            dragCorner.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) BeginDrag(); };
            dragCorner.MouseEnter += (_, _) => { dragHover = true; dragCorner.Invalidate(); };
            dragCorner.MouseLeave += (_, _) => { dragHover = false; dragCorner.Invalidate(); };

            // Resize grip (bottom-right)
            SetupCorner(resizeCorner, Cursors.SizeNWSE, AnchorStyles.Bottom | AnchorStyles.Right);
            ApplyRoundedCorners(resizeCorner, 7);
            resizeCorner.Paint += (_, e) =>
            {
                PaintHotspot(e.Graphics, resizeCorner.ClientRectangle, resizeHover, drawHamburger: false);
                using var pen = new Pen(Color.WhiteSmoke, 2f);
                for (int offset = 5; offset <= 15; offset += 5)
                    e.Graphics.DrawLine(pen, resizeCorner.Width - offset, resizeCorner.Height - 4,
                        resizeCorner.Width - 4, resizeCorner.Height - offset);
            };
            resizeCorner.MouseDown += ResizeCorner_MouseDown;
            resizeCorner.MouseMove += ResizeCorner_MouseMove;
            resizeCorner.MouseUp += (_, __) => resizing = false;
            resizeCorner.MouseEnter += (_, _) => { resizeHover = true; resizeCorner.Invalidate(); };
            resizeCorner.MouseLeave += (_, _) => { resizeHover = false; resizeCorner.Invalidate(); };

            // Menu button (bottom-left)
            SetupCorner(menuCorner, Cursors.Hand, AnchorStyles.Bottom | AnchorStyles.Left);
            menuCorner.Size = MenuButtonSize;
            ApplyRoundedCorners(menuCorner, 6);
            menuCorner.Paint += (_, e) => PaintHotspot(e.Graphics, menuCorner.ClientRectangle, menuHover, drawHamburger: true);
            menuCorner.MouseEnter += (_, _) =>
            {
                menuHover = true;
                menuCorner.Invalidate();
                if (!menuOpen)
                {
                    menuOpen = true;
                    ShowMenu();
                }
            };
            menuCorner.MouseLeave += (_, _) =>
            {
                menuHover = false;
                menuCorner.Invalidate();
            };
            menuCorner.MouseDown += async (_, e) =>
            {
                if (e.Button != MouseButtons.Left) return;
                var old = menuCorner.BackColor;
                menuCorner.BackColor = BLINK_BLUE;
                try
                {
                    ShowMenu();
                    await Task.Delay(120);
                }
                finally
                {
                    menuCorner.BackColor = old;
                }
            };

            dragCorner.SizeChanged += (_, _) => ApplyRoundedCorners(dragCorner, 7);
            resizeCorner.SizeChanged += (_, _) => ApplyRoundedCorners(resizeCorner, 7);
            menuCorner.SizeChanged += (_, _) => ApplyRoundedCorners(menuCorner, 6);

            Controls.AddRange(new[] { dragCorner, resizeCorner, menuCorner });
            BringAllCornersToFront();
            UpdateCornerPositions();
        }

        private void SetupCorner(Panel p, Cursor cursor, AnchorStyles anchor, int x = 0, int y = 0)
        {
            p.Size = new Size(CornerSize, CornerSize);
            p.BackColor = Color.Transparent; // transparent hotspots
            p.Cursor = cursor;
            p.Anchor = anchor;
            p.Location = new Point(x, y);
            p.BorderStyle = BorderStyle.None;
        }

        private void BringAllCornersToFront()
        {
            foreach (Control c in Controls)
                if (c is Panel) c.BringToFront();
        }

        private void UpdateCornerPositions()
        {
            dragCorner.Location = new Point(0, 0);
            resizeCorner.Location = new Point(ClientSize.Width - resizeCorner.Width, ClientSize.Height - resizeCorner.Height);
            menuCorner.Location = new Point(0, ClientSize.Height - menuCorner.Height);
            BringAllCornersToFront();
        }

        private static void ApplyRoundedCorners(Control c, int radius)
        {
            int r = Math.Max(1, radius);
            int d = r * 2;
            if (c.Width < d + 1 || c.Height < d + 1)
            {
                c.Region = null;
                return;
            }

            var path = new GraphicsPath();
            path.AddArc(0, 0, d, d, 180, 90);
            path.AddArc(c.Width - d, 0, d, d, 270, 90);
            path.AddArc(c.Width - d, c.Height - d, d, d, 0, 90);
            path.AddArc(0, c.Height - d, d, d, 90, 90);
            path.CloseFigure();
            c.Region = new Region(path);
        }

        private void PaintHotspot(Graphics g, Rectangle rect, bool hover, bool drawHamburger)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;

            // IMPORTANT: do not fill background; keep hotspot transparent.

            if (hover)
            {
                using (var brush = new LinearGradientBrush(
                    rect,
                    Color.FromArgb(30, 255, 255, 255),
                    Color.FromArgb(5, 255, 255, 255),
                    LinearGradientMode.Vertical))
                {
                    g.FillRectangle(brush, rect);
                }

                using (var penOuter = new Pen(Color.FromArgb(90, 120, 190, 255), 2f))
                using (var penInner = new Pen(Color.FromArgb(140, 200, 220, 255), 1f))
                {
                    var r1 = Rectangle.Inflate(rect, -1, -1);
                    var r2 = Rectangle.Inflate(rect, -2, -2);
                    DrawRoundedRect(g, penOuter, r1, Math.Min(r1.Width, r1.Height) / 3);
                    DrawRoundedRect(g, penInner, r2, Math.Min(r2.Width, r2.Height) / 3);
                }
            }

            if (drawHamburger)
            {
                using var p = new Pen(Color.WhiteSmoke, 2f);
                int padX = Math.Max(10, rect.Width / 6);
                int x1 = padX;
                int x2 = rect.Width - padX;
                int centerY = rect.Height / 2;
                int gap = Math.Min(5, Math.Max(2, rect.Height / 5));

                g.DrawLine(p, x1, centerY - gap, x2, centerY - gap);
                g.DrawLine(p, x1, centerY, x2, centerY);
                g.DrawLine(p, x1, centerY + gap, x2, centerY + gap);
            }
        }

        private static void DrawRoundedRect(Graphics g, Pen pen, Rectangle rect, int radius)
        {
            int r = Math.Max(1, radius);
            int d = r * 2;
            if (rect.Width < d + 1 || rect.Height < d + 1)
            {
                g.DrawRectangle(pen, rect);
                return;
            }

            using var path = new GraphicsPath();
            path.AddArc(rect.Left, rect.Top, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Top, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.Left, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            g.DrawPath(pen, path);
        }

        // ──────────────────────────────────────────────
        // RESIZE / DRAG
        // ──────────────────────────────────────────────

        private void ResizeCorner_MouseDown(object? sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            resizing = true;
            resizeStartMouse = Cursor.Position;
            resizeStartSize = Size;
        }

        private void ResizeCorner_MouseMove(object? sender, MouseEventArgs e)
        {
            if (!resizing) return;
            var dx = Cursor.Position.X - resizeStartMouse.X;
            var dy = Cursor.Position.Y - resizeStartMouse.Y;
            Size = new Size(
                Math.Max(MinimumSize.Width, resizeStartSize.Width + dx),
                Math.Max(MinimumSize.Height, resizeStartSize.Height + dy));
        }

        private void BeginDrag()
        {
            NativeMethods.ReleaseCapture();
            NativeMethods.SendMessage(Handle, NativeMethods.WM_NCLBUTTONDOWN, (IntPtr)NativeMethods.HTCAPTION, IntPtr.Zero);
        }

        private void MoveToBottomRight()
        {
            var wa = Screen.FromControl(this).WorkingArea;
            Left = wa.Right - Width;
            Top = wa.Bottom - Height;
        }

        private void ApplyDefaultWindowPlacement()
        {
            Size = new Size(1200, 800);
            MoveToBottomRight();
        }

        // ──────────────────────────────────────────────
        // CONTEXT MENU
        // ──────────────────────────────────────────────

        private void ShowMenu()
        {
            float ui = GetUiScale();
            var menu = new ContextMenuStrip
            {
                BackColor = MENU_BG,
                ForeColor = MENU_FG,
                RenderMode = ToolStripRenderMode.Professional,
                ShowImageMargin = false,
                Padding = new Padding(ScaledInt(6, ui)),
                Font = ScaleFont(SystemFonts.MessageBoxFont ?? this.Font, ui),
            };

            menu.Renderer = new RoundedMenuRenderer(MENU_BG, MENU_BORDER);
            EnableRoundedToolStrip(menu, MENU_RADIUS);

            var closeTimer = new System.Windows.Forms.Timer { Interval = 220 };
            closeTimer.Tick += (_, __) =>
            {
                if (!menu.Visible)
                {
                    closeTimer.Stop();
                    closeTimer.Dispose();
                    return;
                }

                var menuRect = menu.Bounds;
                var cornerRect = menuCorner.RectangleToScreen(menuCorner.ClientRectangle);
                var safe = Rectangle.Union(menuRect, cornerRect);
                safe.Inflate(10, 10);

                var cursor = Cursor.Position;

                try
                {
                    var sel = (menu as ToolStripDropDown)?.GetItemAt(menu.PointToClient(cursor));
                    if (sel is ToolStripMenuItem mi && mi.DropDown?.Visible == true)
                        safe = Rectangle.Union(safe, mi.DropDown.Bounds);
                }
                catch { }

                if (!safe.Contains(cursor))
                {
                    closeTimer.Stop();
                    closeTimer.Dispose();
                    menu.Close(ToolStripDropDownCloseReason.AppClicked);
                }
            };

            menu.Opened += (_, __) => closeTimer.Start();
            menu.Closed += (_, __) => menuOpen = false;

            // Most used action at the top
            menu.Items.Add("Refresh page (F5)", null, (_, __) =>
            {
                if (webView.CoreWebView2 != null) webView.CoreWebView2.Reload();
                else webView.Reload();
            });

            menu.Items.Add("Open config folder", null, (_, __) =>
            {
                var folder = Path.GetDirectoryName(ConfigPath)!;
                Directory.CreateDirectory(folder);
                Process.Start(new ProcessStartInfo("explorer.exe", folder) { UseShellExecute = true });
            });

            menu.Items.Add(isFullscreen ? "Exit fullscreen (Esc)" : "Open fullscreen (F11)",
                null, (_, __) => ToggleFullscreen());

            menu.Items.Add("Save window position/size", null, (_, __) => SaveWindowPlacement());

            menu.Items.Add("Reset to defaults (this session)", null, (_, __) =>
            {
                if (MessageBox.Show(this,
                    "Reset saved settings and restore defaults now?",
                    "StarGate WebView", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                    return;

                if (isFullscreen) ToggleFullscreen();
                DeleteConfigSafe();
                EnsureConfigFileExists();
                ApplyDefaultWindowPlacement();

                var defaultUri = new Uri($"http://{DefaultHost}{PathDial9}");
                if (webView.CoreWebView2 != null) webView.CoreWebView2.Navigate(defaultUri.ToString());
                else webView.Source = defaultUri;
            });

            menu.Items.Add(new ToolStripSeparator());

            var legendItem = new ToolStripMenuItem("Legend / Shortcuts")
            {
                ForeColor = MENU_FG
            };
            var legendDropDown = CreateLegendPopup(ui);
            EnableRoundedToolStrip(legendDropDown, LEGEND_RADIUS);
            legendItem.DropDown = legendDropDown;
            legendItem.DropDownDirection = ToolStripDropDownDirection.Right;
            legendItem.MouseEnter += (_, __) =>
            {
                if (!legendItem.DropDown.Visible)
                    legendItem.ShowDropDown();
            };
            menu.Items.Add(legendItem);

            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, (_, __) => Close());

            var screenPoint = menuCorner.PointToScreen(new Point(0, 0));
            menu.Show(screenPoint.X, screenPoint.Y - menu.Height);
        }

        private ToolStripDropDown CreateLegendPopup(float uiScale)
        {
            var dd = new ToolStripDropDown
            {
                AutoClose = true,
                Padding = new Padding(ScaledInt(10, uiScale)),
                BackColor = MENU_BG,
                ForeColor = MENU_FG,
                Renderer = new RoundedMenuRenderer(MENU_BG, MENU_BORDER),
                Font = ScaleFont(SystemFonts.MessageBoxFont ?? this.Font, uiScale),
            };

            var panel = new Panel
            {
                BackColor = MENU_BG,
                ForeColor = MENU_FG,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(ScaledInt(6, uiScale)),
            };

            var text =
@"Legend:
• Top-left corner   → Drag window
• Bottom-right corner → Resize window
• Bottom-left corner → Menu (hover or click)

Keyboard shortcuts:
• F5               → Refresh page
• F11              → Toggle fullscreen
• ESC              → Exit fullscreen
• Alt + F4         → Close application
• Alt + ← / →      → Back / Forward";

            int legendMaxWidth = Math.Min(LEGEND_MAX_WIDTH, Math.Max(260, ScaledInt(LEGEND_MAX_WIDTH, uiScale)));

            var lbl = new Label
            {
                AutoSize = true,
                MaximumSize = new Size(legendMaxWidth, 0),
                Text = text,
                ForeColor = MENU_FG,
                BackColor = MENU_BG,
                Font = ScaleFont(SystemFonts.MessageBoxFont ?? this.Font, uiScale),
            };

            panel.Controls.Add(lbl);

            var host = new ToolStripControlHost(panel)
            {
                Margin = Padding.Empty,
                Padding = Padding.Empty,
                AutoSize = true,
            };

            dd.Items.Add(host);
            return dd;
        }

        private static void EnableRoundedToolStrip(ToolStripDropDown dropDown, int radius)
        {
            void Apply()
            {
                try
                {
                    var rect = new Rectangle(0, 0, dropDown.Width, dropDown.Height);
                    dropDown.Region = new Region(CreateRoundedRectPath(rect, radius));
                }
                catch { }
            }

            dropDown.Opened += (_, __) => Apply();
            dropDown.SizeChanged += (_, __) => Apply();
        }

        private static GraphicsPath CreateRoundedRectPath(Rectangle rect, int radius)
        {
            int r = Math.Max(1, radius);
            int d = r * 2;
            var path = new GraphicsPath();

            if (rect.Width < d + 1 || rect.Height < d + 1)
            {
                path.AddRectangle(rect);
                return path;
            }

            path.AddArc(rect.Left, rect.Top, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Top, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.Left, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private sealed class RoundedMenuRenderer : ToolStripProfessionalRenderer
        {
            private readonly Color _bg;
            private readonly Color _border;

            public RoundedMenuRenderer(Color bg, Color border) : base(new RoundedMenuColorTable(bg))
            {
                _bg = bg;
                _border = border;
                RoundedEdges = false;
            }

            protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
            {
                using var b = new SolidBrush(_bg);
                e.Graphics.FillRectangle(b, e.AffectedBounds);
            }

            protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
            {
                using var p = new Pen(_border, 1f);
                var r = new Rectangle(0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
                e.Graphics.DrawRectangle(p, r);
            }

            protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
            {
                var g = e.Graphics;
                var r = new Rectangle(Point.Empty, e.Item.Size);

                if (e.Item.Selected)
                {
                    using var brush = new LinearGradientBrush(
                        r,
                        Color.FromArgb(28, 255, 255, 255),
                        Color.FromArgb(8, 255, 255, 255),
                        LinearGradientMode.Vertical);
                    g.FillRectangle(brush, r);
                }
                else
                {
                    using var b = new SolidBrush(_bg);
                    g.FillRectangle(b, r);
                }
            }

            protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
            {
                var r = e.Item.ContentRectangle;
                int y = r.Top + r.Height / 2;
                using var p = new Pen(Color.FromArgb(40, 255, 255, 255), 1f);
                e.Graphics.DrawLine(p, r.Left + 8, y, r.Right - 8, y);
            }
        }

        private sealed class RoundedMenuColorTable : ProfessionalColorTable
        {
            private readonly Color _bg;

            public RoundedMenuColorTable(Color bg) => _bg = bg;

            public override Color ToolStripDropDownBackground => _bg;
            public override Color ImageMarginGradientBegin => _bg;
            public override Color ImageMarginGradientMiddle => _bg;
            public override Color ImageMarginGradientEnd => _bg;
            public override Color MenuBorder => _bg;
            public override Color SeparatorDark => Color.FromArgb(40, 255, 255, 255);
            public override Color SeparatorLight => Color.FromArgb(10, 255, 255, 255);
            public override Color MenuItemBorder => Color.Transparent;
            public override Color MenuItemSelected => Color.FromArgb(18, 255, 255, 255);
            public override Color MenuItemSelectedGradientBegin => Color.FromArgb(18, 255, 255, 255);
            public override Color MenuItemSelectedGradientEnd => Color.FromArgb(8, 255, 255, 255);
        }

        // ──────────────────────────────────────────────
        // HOTKEYS
        // ──────────────────────────────────────────────

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);

            f11Registered = NativeHotKey.RegisterHotKey(Handle, HOTKEY_ID_F11, 0, Keys.F11);

            if (!f11Registered)
            {
                bool fallback = NativeHotKey.RegisterHotKey(Handle, HOTKEY_ID_F11_FALLBACK,
                    NativeHotKey.MOD_CONTROL | NativeHotKey.MOD_ALT, Keys.F11);

                if (!fallback)
                {
                    MessageBox.Show("Could not register fullscreen hotkeys.",
                        "StarGate WebView", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                else
                {
                    MessageBox.Show("F11 is already in use – use Ctrl+Alt+F11 instead.",
                        "StarGate WebView", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
            }
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            NativeHotKey.UnregisterHotKey(Handle, HOTKEY_ID_F11);
            NativeHotKey.UnregisterHotKey(Handle, HOTKEY_ID_F11_FALLBACK);
            NativeHotKey.UnregisterHotKey(Handle, HOTKEY_ID_ESC);
            base.OnHandleDestroyed(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == NativeHotKey.WM_HOTKEY)
            {
                int id = m.WParam.ToInt32();

                if (id == HOTKEY_ID_F11 || id == HOTKEY_ID_F11_FALLBACK)
                {
                    ToggleFullscreen();
                    return;
                }

                if (id == HOTKEY_ID_ESC && isFullscreen)
                {
                    ToggleFullscreen();
                    return;
                }
            }
            base.WndProc(ref m);
        }

        private void ToggleFullscreen()
        {
            if (!isFullscreen)
            {
                prevBorderStyle = FormBorderStyle;
                prevWindowState = WindowState;
                prevBounds = Bounds;
                prevTopMost = TopMost;

                isFullscreen = true;
                TopMost = true;
                FormBorderStyle = FormBorderStyle.None;
                WindowState = FormWindowState.Normal;
                Bounds = Screen.FromControl(this).Bounds;

                NativeHotKey.RegisterHotKey(Handle, HOTKEY_ID_ESC, 0, Keys.Escape);
                Activate();
            }
            else
            {
                isFullscreen = false;
                NativeHotKey.UnregisterHotKey(Handle, HOTKEY_ID_ESC);

                TopMost = prevTopMost;
                FormBorderStyle = prevBorderStyle;
                Bounds = prevBounds;
                WindowState = prevWindowState;

                Activate();
            }
        }

        private static class NativeHotKey
        {
            public const int WM_HOTKEY = 0x0312;
            public const uint MOD_ALT = 0x0001;
            public const uint MOD_CONTROL = 0x0002;
            public const uint MOD_NOREPEAT = 0x4000;

            [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
            private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

            [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
            public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

            public static bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, Keys key)
            {
                return RegisterHotKey(hwnd, id, modifiers | MOD_NOREPEAT, (uint)key);
            }
        }

        private static class NativeMethods
        {
            public const int WM_NCLBUTTONDOWN = 0xA1;
            public const int HTCAPTION = 0x2;

            [System.Runtime.InteropServices.DllImport("user32.dll")]
            public static extern bool ReleaseCapture();

            [System.Runtime.InteropServices.DllImport("user32.dll")]
            public static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);
        }

        // ──────────────────────────────────────────────
        // CONFIG & WINDOW PLACEMENT
        // ──────────────────────────────────────────────

        private sealed class AppConfig
        {
            public string LastIpOrHost { get; set; } = "";
            public int? LastPort { get; set; }
            public string LastMode { get; set; } = "LocalDial9";
            public bool SetupCompleted { get; set; }
            public int? WindowX { get; set; }
            public int? WindowY { get; set; }
            public int? WindowW { get; set; }
            public int? WindowH { get; set; }
        }

        private static void EnsureConfigFileExists()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
                if (File.Exists(ConfigPath)) return;

                var cfg = new AppConfig();
                File.WriteAllText(ConfigPath, JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        private static AppConfig LoadConfigSafe()
        {
            try
            {
                if (File.Exists(ConfigPath))
                    return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath)) ?? new AppConfig();
            }
            catch { }
            return new AppConfig();
        }

        private static void SaveConfigSafe(AppConfig cfg)
        {
            try
            {
                File.WriteAllText(ConfigPath, JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        private static void DeleteConfigSafe()
        {
            try { File.Delete(ConfigPath); }
            catch { }
        }

        private void SaveWindowPlacement()
        {
            var cfg = LoadConfigSafe();
            var bounds = Bounds;
            cfg.WindowX = bounds.X;
            cfg.WindowY = bounds.Y;
            cfg.WindowW = bounds.Width;
            cfg.WindowH = bounds.Height;
            SaveConfigSafe(cfg);
        }

        private bool RestoreWindowPlacement()
        {
            var cfg = LoadConfigSafe();
            if (!cfg.WindowX.HasValue || !cfg.WindowY.HasValue || !cfg.WindowW.HasValue || !cfg.WindowH.HasValue)
                return false;

            int x = cfg.WindowX.Value, y = cfg.WindowY.Value;
            int w = Math.Max(MinimumSize.Width, cfg.WindowW.Value);
            int h = Math.Max(MinimumSize.Height, cfg.WindowH.Value);

            var wa = Screen.FromPoint(new Point(x, y)).WorkingArea;
            if (x + w > wa.Right) x = wa.Right - w;
            if (y + h > wa.Bottom) y = wa.Bottom - h;
            if (x < wa.Left) x = wa.Left;
            if (y < wa.Top) y = wa.Top;

            Bounds = new Rectangle(x, y, w, h);
            return true;
        }

        // ──────────────────────────────────────────────
        // WEBVIEW2 INITIALIZATION
        // ──────────────────────────────────────────────

        private bool WebViewUnavailable => IsDisposed || Disposing || webView.IsDisposed || webView.Disposing;

        private static bool IsFan113Uri(Uri? uri) =>
            uri != null && uri.IsAbsoluteUri &&
            uri.AbsolutePath.EndsWith("/fan113.html", StringComparison.OrdinalIgnoreCase);

        // Restrict presentation changes to the top-level FAN113 document.
        private const string Fan113TouchScript = """
            (() => {
                if (window !== window.top || !location.pathname.toLowerCase().endsWith('/fan113.html')) return;
                const apply = () => {
                    if (document.getElementById('native-fan113-touch-style')) return;
                    const style = document.createElement('style');
                    style.id = 'native-fan113-touch-style';
                    style.textContent = '*,*::before,*::after{-webkit-tap-highlight-color:transparent!important;}' +
                        ':focus:not(:focus-visible){outline:none!important;box-shadow:none!important;}';
                    (document.head || document.documentElement).appendChild(style);
                };
                if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', apply, {once:true});
                else apply();
            })();
            """;

        // FAN113's desktop cover/min-height rules crop short windows.
        // Fit its existing composition once, leaving WebView native zoom at 100%.
        private const string Fan113FitScript = """
            (() => {
                if (window !== window.top || !location.pathname.toLowerCase().endsWith('/fan113.html')) return;
                const install = () => {
                    const world = document.querySelector('.world');
                    const composition = document.querySelector('#scene-composition');
                    const status = document.querySelector('.status-bar');
                    if (!world || !composition || !status) return;
                    const style = document.createElement('style');
                    style.id = 'native-fan113-fit-style';
                    style.textContent = `
                        .world{min-height:0!important;}
                        .status-bar{background:transparent!important;border-color:transparent!important;
                            box-shadow:none!important;backdrop-filter:none!important;}
                        .status-bar>.status-place,.status-bar>.status-state,.status-bar>.status-actions{
                            border-color:transparent!important;}
                        .status-place,.status-state{ text-shadow:0 1px 3px #000,0 0 6px #000; }
                        @media(max-width:900px),(max-height:600px){
                            .status-bar{left:7px!important;right:7px!important;bottom:7px!important;
                                min-height:0!important;grid-template-columns:minmax(70px,1fr) auto auto!important;}
                            .status-bar>*{box-sizing:border-box;min-height:0!important;padding:5px 7px!important;}
                            .status-place .eyebrow{font-size:7px!important;}
                            #gate-name{font-size:11px!important;margin-top:1px!important;}
                            .status-state{display:flex!important;gap:5px!important;font-size:9px!important;
                                flex-wrap:wrap;letter-spacing:.04em!important;}
                            .status-lamp{width:6px!important;height:6px!important;}
                            .status-actions{gap:6px!important;justify-content:flex-end!important;}
                            .status-actions button{min-height:28px!important;padding:0 7px!important;
                                font-size:8px!important;letter-spacing:.02em!important;}
                            #iris-control{box-sizing:border-box;display:flex!important;align-items:center;
                                gap:5px;min-width:0!important;min-height:28px;padding:0!important;
                                font-size:10px!important;white-space:nowrap;}
                            #iris-audio-switch{position:static!important;transform:none!important;
                                display:inline-flex;align-items:center;min-height:18px!important;
                                padding:0 5px!important;font-size:6px!important;white-space:nowrap;}
                        }
                        @media(max-width:420px){
                            .status-bar{grid-template-columns:minmax(0,1fr) auto!important;}
                            .status-place,.status-state{padding:3px 7px!important;}
                            .status-state{border-right:0!important;}
                            .status-actions{grid-column:1/-1;padding:3px 7px!important;
                                border-top:1px solid rgba(157,171,161,.14);}
                            .status-actions button,#iris-control{min-height:24px!important;}
                        }
                    `;
                    document.head.appendChild(style);
                    let pending = false;
                    const fit = () => {
                        pending = false;
                        const w = world.clientWidth, h = world.clientHeight;
                        const cw = composition.offsetWidth, ch = composition.offsetHeight;
                        if (!w || !h || !cw || !ch) return;
                        const bottom = Math.max(0, parseFloat(getComputedStyle(status).bottom) || 0);
                        const gap = 12;
                        const availableHeight = Math.max(1, h - status.offsetHeight - bottom - gap * 2);
                        const scale = Math.max(0.0001, Math.min(Math.max(1, w - gap * 2) / cw, availableHeight / ch));
                        const set = (name, value) => {
                            if (composition.style.getPropertyValue(name) !== value ||
                                composition.style.getPropertyPriority(name) !== 'important')
                                composition.style.setProperty(name, value, 'important');
                        };
                        set('--scene-responsive-scale', String(scale));
                        set('top', String(gap + availableHeight / 2) + 'px');
                        composition.dataset.responsiveMode = 'native-contain';
                        composition.dataset.responsiveScale = scale.toFixed(6);
                    };
                    const schedule = () => {
                        if (!pending) { pending = true; queueMicrotask(fit); }
                    };
                    new ResizeObserver(schedule).observe(world);
                    new ResizeObserver(schedule).observe(status);
                    new MutationObserver(schedule).observe(composition, {attributes:true, attributeFilter:['style']});
                    window.addEventListener('resize', schedule, {passive:true});
                    fit();
                };
                if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', install, {once:true});
                else install();
            })();
            """;

        private void ApplyFan113NavigationSettings(Uri? uri)
        {
            if (WebViewUnavailable || webView.CoreWebView2 == null) return;
            bool fan113 = IsFan113Uri(uri);
            webView.CoreWebView2.Settings.IsZoomControlEnabled = !fan113;
            if (fan113) webView.ZoomFactor = 1.0;
        }

        private static void WriteDiagnosticSafe(string path, string text, bool append = false)
        {
            try
            {
                if (append) File.AppendAllText(path, text);
                else File.WriteAllText(path, text);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private async Task InitWebView2Async(Uri targetUri)
        {
            try
            {
                string userDataDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "StarGateWebView", "WebView2UserData");

                var env = await CoreWebView2Environment.CreateAsync(
                    browserExecutableFolder: null,
                    userDataFolder: userDataDir,
                    options: new CoreWebView2EnvironmentOptions("")
                );

                if (WebViewUnavailable) return;
                await webView.EnsureCoreWebView2Async(env);
                if (WebViewUnavailable) return;
                BringAllCornersToFront();

                var settings = webView.CoreWebView2.Settings;
                settings.AreDevToolsEnabled = false;
                settings.IsGeneralAutofillEnabled = false;
                settings.IsPasswordAutosaveEnabled = false;
                settings.IsStatusBarEnabled = false;
                settings.AreDefaultContextMenusEnabled = false;

                await webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(Fan113TouchScript);
                if (WebViewUnavailable) return;
                await webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(Fan113FitScript);
                if (WebViewUnavailable) return;

                webView.CoreWebView2.NavigationStarting += (_, e) =>
                {
                    if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri))
                        ApplyFan113NavigationSettings(uri);
                };
                ApplyFan113NavigationSettings(targetUri);

                // Log navigation results to file
                webView.CoreWebView2.NavigationCompleted += (_, e) =>
                {
                    if (WebViewUnavailable) return;
                    ApplyFan113NavigationSettings(webView.Source);
                    string msg = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | " +
                                 $"Success: {e.IsSuccess} | " +
                                 $"Status: {e.WebErrorStatus} | " +
                                 $"Url: {webView.Source}\n";

                    string logPath = Path.Combine(AppContext.BaseDirectory, "navigation.log");
                    WriteDiagnosticSafe(logPath, msg, append: true);
                };

                webView.CoreWebView2.Navigate(targetUri.ToString());
            }
            catch (Exception ex)
            {
                if (WebViewUnavailable) return;
                string logPath = Path.Combine(AppContext.BaseDirectory, "webview2_error.log");
                WriteDiagnosticSafe(logPath, ex.ToString());

                MessageBox.Show($"WebView2 initialization failed.\n\nSee:\n{logPath}",
                    "StarGate WebView", MessageBoxButtons.OK, MessageBoxIcon.Error);

                Close();
            }
        }

        // ──────────────────────────────────────────────
        // STARTUP DIALOG
        // ──────────────────────────────────────────────

        private static Uri BuildStartupAddress(string address, int? port, string fallbackPath)
        {
            string input = address.Trim();
            if (!input.Contains("://"))
                input = (input.StartsWith("fan113-test.highlandergate.com", StringComparison.OrdinalIgnoreCase)
                    ? "https://" : "http://") + input;
            var builder = new UriBuilder(input);
            if (builder.Scheme != Uri.UriSchemeHttp && builder.Scheme != Uri.UriSchemeHttps)
                throw new UriFormatException("Use an HTTP or HTTPS address.");
            if (port.HasValue) builder.Port = port.Value;
            if (string.IsNullOrEmpty(builder.Path) || builder.Path == "/") builder.Path = fallbackPath;
            return builder.Uri;
        }
        private Uri? ShowStartupChoiceAndGetUri()
        {
            var cfg = LoadConfigSafe();

            if (cfg.SetupCompleted && Enum.TryParse<StartupDialog.Mode>(cfg.LastMode, out var m))
            {
                switch (m)
                {
                    case StartupDialog.Mode.LocalDial9: return new Uri($"http://{DefaultHost}{PathDial9}");
                    case StartupDialog.Mode.LocalDial: return new Uri($"http://{DefaultHost}{PathDial}");
                    case StartupDialog.Mode.IpDial9: return BuildStartupAddress(cfg.LastIpOrHost, cfg.LastPort, PathDial9);
                    case StartupDialog.Mode.IpDial: return BuildStartupAddress(cfg.LastIpOrHost, cfg.LastPort, PathDial);
                }
            }

            using var dlg = new StartupDialog(cfg);
            if (dlg.ShowDialog(this) != DialogResult.OK) return null;

            if (dlg.ResetRequested)
            {
                DeleteConfigSafe();
                return null;
            }

            if (dlg.Remember)
            {
                cfg.LastMode = dlg.SelectedMode.ToString();
                if (dlg.SelectedMode is StartupDialog.Mode.IpDial9 or StartupDialog.Mode.IpDial)
                {
                    cfg.LastIpOrHost = dlg.IpAddress.Trim();
                    cfg.LastPort = dlg.PortValue;
                }
                cfg.SetupCompleted = true;
                SaveConfigSafe(cfg);
            }

            return dlg.SelectedMode switch
            {
                StartupDialog.Mode.LocalDial9 => new Uri($"http://{DefaultHost}{PathDial9}"),
                StartupDialog.Mode.LocalDial => new Uri($"http://{DefaultHost}{PathDial}"),
                StartupDialog.Mode.IpDial9 => BuildStartupAddress(dlg.IpAddress, dlg.PortValue, PathDial9),
                StartupDialog.Mode.IpDial => BuildStartupAddress(dlg.IpAddress, dlg.PortValue, PathDial),
                _ => null
            };
        }

        private sealed class StartupDialog : Form
        {
            public enum Mode { LocalDial9, LocalDial, IpDial9, IpDial }

            public Mode SelectedMode { get; private set; } = Mode.LocalDial9;
            public string IpAddress => txtIp.Text.Trim();
            public int? PortValue
            {
                get
                {
                    var t = txtPort.Text.Trim();
                    if (string.IsNullOrEmpty(t)) return null;
                    return int.TryParse(t, out int p) && p >= 1 && p <= 65535 ? p : null;
                }
            }

            public bool Remember => cbRemember.Checked;
            public bool ResetRequested { get; private set; }

            private readonly RadioButton rbLocalDial9 = new();
            private readonly RadioButton rbLocalDial = new();
            private readonly RadioButton rbIpDial9 = new();
            private readonly RadioButton rbIpDial = new();
            private readonly TextBox txtIp = new();
            private readonly TextBox txtPort = new();
            private readonly CheckBox cbRemember = new();
            private readonly Button btnReset = new();
            private readonly Button btnOk = new();
            private readonly Button btnCancel = new();

            private readonly AppConfig cfg;

            public StartupDialog(AppConfig cfg)
            {
                this.cfg = cfg;

                Text = "Select Stargate Address";
                FormBorderStyle = FormBorderStyle.FixedDialog;
                MaximizeBox = MinimizeBox = false;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.CenterParent;
                AutoScaleMode = AutoScaleMode.Font;
                ClientSize = new Size(640, 380);

                var lbl = new Label { Text = "Choose what to open:", Location = new Point(14, 14), AutoSize = true };
                Controls.Add(lbl);

                rbLocalDial9.Text = $"Open http://{DefaultHost}{PathDial9}";
                rbLocalDial9.Location = new Point(18, 44); rbLocalDial9.AutoSize = true;

                rbLocalDial.Text = $"Open http://{DefaultHost}{PathDial}";
                rbLocalDial.Location = new Point(18, 72); rbLocalDial.AutoSize = true;

                rbIpDial9.Text = $"Open http://IP[:PORT]{PathDial9}";
                rbIpDial9.Location = new Point(18, 114); rbIpDial9.AutoSize = true;

                rbIpDial.Text = $"Open http://IP[:PORT]{PathDial}";
                rbIpDial.Location = new Point(18, 142); rbIpDial.AutoSize = true;

                Controls.AddRange(new Control[] { rbLocalDial9, rbLocalDial, rbIpDial9, rbIpDial });

                var lblIp = new Label { Text = "IP address / Host:", Location = new Point(38, 190), AutoSize = true };
                txtIp.Location = new Point(lblIp.Left, lblIp.Bottom + 6);
                txtIp.Size = new Size(340, 23);
                txtIp.PlaceholderText = "192.168.1.100 or stargate.local";

                var lblPort = new Label { Text = "Port (optional):", Location = new Point(400, 190), AutoSize = true };
                txtPort.Location = new Point(lblPort.Left, lblPort.Bottom + 6);
                txtPort.Size = new Size(200, 23);
                txtPort.PlaceholderText = "8080";

                Controls.AddRange(new Control[] { lblIp, txtIp, lblPort, txtPort });

                cbRemember.Text = "Remember selection (save to config.json)";
                cbRemember.Location = new Point(18, 270); cbRemember.AutoSize = true;
                cbRemember.Checked = true;
                Controls.Add(cbRemember);

                btnReset.Text = "Clear saved IP/Port";
                btnReset.Size = new Size(170, 32);
                btnReset.Location = new Point(18, 310);
                Controls.Add(btnReset);

                btnOk.Text = "OK"; btnOk.Size = new Size(100, 32); btnOk.DialogResult = DialogResult.OK;
                btnCancel.Text = "Cancel"; btnCancel.Size = new Size(100, 32); btnCancel.DialogResult = DialogResult.Cancel;
                Controls.AddRange(new Control[] { btnOk, btnCancel });

                AcceptButton = btnOk;
                CancelButton = btnCancel;

                rbLocalDial9.CheckedChanged += (_, _) => UpdateIpFields();
                rbLocalDial.CheckedChanged += (_, _) => UpdateIpFields();
                rbIpDial9.CheckedChanged += (_, _) => UpdateIpFields();
                rbIpDial.CheckedChanged += (_, _) => UpdateIpFields();

                btnReset.Click += (_, _) =>
                {
                    ResetRequested = true;
                    txtIp.Text = txtPort.Text = "";
                    cbRemember.Checked = true;
                    rbIpDial9.Checked = true;
                    MessageBox.Show("Saved IP/Port has been cleared.\nEnter new values and click OK.",
                        "StarGate WebView", MessageBoxButtons.OK, MessageBoxIcon.Information);
                };

                btnOk.Click += (_, _) =>
                {
                    if ((rbIpDial9.Checked || rbIpDial.Checked) && string.IsNullOrWhiteSpace(txtIp.Text))
                    {
                        MessageBox.Show("IP address is required for remote modes.",
                            "StarGate WebView", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        DialogResult = DialogResult.None;
                    }
                    else if (txtPort.Text.Trim().Length > 0 && PortValue == null)
                    {
                        MessageBox.Show("Port must be between 1–65535 or left empty.",
                            "StarGate WebView", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        DialogResult = DialogResult.None;
                    }
                };

                FormClosing += (_, _) =>
                {
                    if (DialogResult == DialogResult.OK)
                    {
                        SelectedMode = rbLocalDial9.Checked ? Mode.LocalDial9 :
                                       rbLocalDial.Checked ? Mode.LocalDial :
                                       rbIpDial9.Checked ? Mode.IpDial9 : Mode.IpDial;
                    }
                };

                Shown += (_, _) => LayoutButtons();
                Resize += (_, _) => LayoutButtons();

                ApplyConfigDefaults();
                UpdateIpFields();
            }

            private void ApplyConfigDefaults()
            {
                if (Enum.TryParse<Mode>(cfg.LastMode, out var m))
                {
                    switch (m)
                    {
                        case Mode.LocalDial9: rbLocalDial9.Checked = true; break;
                        case Mode.LocalDial: rbLocalDial.Checked = true; break;
                        case Mode.IpDial9: rbIpDial9.Checked = true; break;
                        case Mode.IpDial: rbIpDial.Checked = true; break;
                    }
                }
                else rbLocalDial9.Checked = true;

                txtIp.Text = cfg.LastIpOrHost ?? "";
                txtPort.Text = cfg.LastPort?.ToString() ?? "";
            }

            private void LayoutButtons()
            {
                int padding = 16;
                btnCancel.Location = new Point(ClientSize.Width - btnCancel.Width - padding, ClientSize.Height - btnCancel.Height - padding);
                btnOk.Location = new Point(btnCancel.Left - btnOk.Width - 10, btnCancel.Top);
            }

            private void UpdateIpFields()
            {
                bool ipMode = rbIpDial9.Checked || rbIpDial.Checked;
                txtIp.Enabled = txtPort.Enabled = ipMode;
            }
        }
    }
}
