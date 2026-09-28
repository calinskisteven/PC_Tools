using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Collections.Generic;
using System.IO;
using System.Media;
using System.Diagnostics;



namespace WorkTimeTaskbar
{
    public class TaskbarForm : Form
    {
        // Timer + state
        private Timer timer;
        private bool tracking = true;
        private TimeSpan accumulated = TimeSpan.Zero;   // total time already worked
        private DateTime lastStart = DateTime.Now;      // when the current run started

        // UI
        private Label lblTime;
        private Button btnPlayPause;
        private Button btnReset;
        private Button btnStop;

        // Icon
        private Icon currentIcon;

        // The notification area (tray) menu is separate from the Windows taskbar menu.
        private NotifyIcon trayIcon;
        private ContextMenuStrip trayMenu;
        private ToolStripMenuItem trayToggle;
        private ToolStripMenuItem traySnooze;

        // Independent of the work timer: must continue checking while paused.
        private Timer reminderTimer;
        private Form reminderForm;
        private bool closingReminderByAction;
        private bool remindersEnabledForPause;
        private uint observedLastInputTick;
        private bool wasAwayDuringPause;
        private DateTime pauseStartedUtc = DateTime.UtcNow;
        private static readonly TimeSpan PauseGrace = TimeSpan.FromMinutes(3);
        private const uint AwayThresholdMs = 30000U;
        private int secondsSinceCheckpoint;
        private WorkLogRecorder workLog;
        private string workLogInitializationError = "";
        private Label lblLogStatus;
        private Label lblActiveTask;
        private Label lblRunState;
        private ToolTip uiTip;
        private DateTime snoozeUntilUtc = DateTime.MinValue;
        private int defaultSnoozeMinutes = 5;

        // tasks
        private List<TaskTimer> tasks = new List<TaskTimer>();
        private TaskTimer activeTask;
        private ListBox lstTasks;
        private Button btnAddTask;
        private Button btnRemoveTask;
        private Button btnSetActive;
        private Button btnRenameTask;
        private Button btnTaskPause;
        private Button btnTaskResume;
        private Button btnTaskReset;
        private CheckBox chkUseTaskIcon;
        private CheckBox chkShowRemaining;
        private CheckBox chkAlwaysOnTop;
        private Button btnSetExpected;

        private string dataFilePath;





        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool DestroyIcon(IntPtr handle);

        private const int WM_GETICON = 0x007F;
        private const int WM_SETICON = 0x0080;
        private const int ICON_SMALL = 0;
        private const int ICON_BIG = 1;
        private const int ICON_SMALL2 = 2;

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct LastInputInfo
        {
            public uint cbSize;
            public uint dwTime;
        }

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetLastInputInfo(ref LastInputInfo info);

        private static uint ReadLastInputTick()
        {
            var info = new LastInputInfo { cbSize = (uint)Marshal.SizeOf(typeof(LastInputInfo)) };
            return GetLastInputInfo(ref info) ? info.dwTime : unchecked((uint)Environment.TickCount);
        }

        public TaskbarForm()
        {
            Text = "Work Timer";
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 10f);
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            MinimumSize = new Size(440, 420);
            ClientSize = new Size(1010, 744);
            ShowInTaskbar = true;
            BackColor = UiBackground;
            ForeColor = UiForeground;
            uiTip = new ToolTip { AutoPopDelay = 12000, InitialDelay = 400 };

            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WorkTimeTimerLive");
            Directory.CreateDirectory(folder);
            dataFilePath = Path.Combine(folder, "timers.txt");

            BuildMainLayout();
            Shown += (s, e) => ReflowLayout();

            // Timer
            timer = new Timer { Interval = 1000 };
            timer.Tick += Timer_Tick;
            //timer.Start();

            // Click anywhere on the form to toggle play/pause
            //this.MouseClick += TaskbarForm_MouseClick;

            // Load previous session state (if any)
            LoadState();
            try { workLog = new WorkLogRecorder(folder); }
            catch (Exception ex)
            {
                workLogInitializationError = ex.Message;
            }
            RefreshLogStatus();

            // Never count idle time after relaunch; LoadState intentionally starts paused.
            timer.Start();
            InitializeTrayIcon();

            // This timer must NOT be stopped when the work timer is paused.
            observedLastInputTick = ReadLastInputTick();
            remindersEnabledForPause = !tracking;
            pauseStartedUtc = DateTime.UtcNow;
            reminderTimer = new Timer { Interval = 500 };
            reminderTimer.Tick += ReminderTimer_Tick;
            reminderTimer.Start();

            // Make sure UI reflects loaded state
            UpdateUiAndIcon();
            RefreshTaskListDisplay();

        }

        private static readonly Color UiBackground = Color.FromArgb(13, 20, 31);
        private static readonly Color UiCard = Color.FromArgb(25, 37, 54);
        private static readonly Color UiField = Color.FromArgb(17, 29, 44);
        private static readonly Color UiForeground = Color.FromArgb(241, 247, 252);
        private static readonly Color UiMuted = Color.FromArgb(169, 188, 207);
        private static readonly Color UiAccent = Color.FromArgb(69, 211, 189);
        private static readonly Color UiDanger = Color.FromArgb(254, 177, 115);

        // This is a vertical scrolling canvas; we never allow a narrow window to
        // squeeze buttons below their usable height or to overlap neighboring rows.
        private Panel scrollSurface;
        private TableLayoutPanel rootLayout;
        private TableLayoutPanel actionsLayout;
        private TableLayoutPanel settingsLayout;
        private TableLayoutPanel projectsLayout;
        private TableLayoutPanel projectButtonsLayout;
        private TableLayoutPanel headerLayout;
        private Panel projectCard;
        private Button btnOpenLog;
        private Label lblHeadingHint;
        private readonly List<Button> projectActions = new List<Button>();
        private readonly List<Button> mainActions = new List<Button>();
        private bool reflowInProgress;
        private int lastLayoutWidth = -1;
        private int lastLayoutHeight = -1;

        private int P(int value) => (int)Math.Ceiling(value * DeviceDpi / 96f);

        private static Label TextLabel(string text, float size, bool bold = false)
        {
            return new Label
            {
                Text = text,
                Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular),
                ForeColor = UiForeground,
                TextAlign = ContentAlignment.MiddleLeft,
                Dock = DockStyle.Fill,
                AutoEllipsis = true,
                AutoSize = false,
                Margin = new Padding(0),
                UseMnemonic = false
            };
        }

        private static Button ActionButton(string text, bool primary = false, bool destructive = false)
        {
            var button = new SoftButton
            {
                Text = text,
                BackColor = primary ? UiAccent : destructive ? Color.FromArgb(78, 46, 53) : Color.FromArgb(43, 60, 79),
                ForeColor = primary ? UiBackground : UiForeground,
                Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
                Dock = DockStyle.Fill,
                AutoSize = false,
                Margin = new Padding(4),
                Cursor = Cursors.Hand,
                TabStop = true
            };
            return button;
        }

        private static Panel Card(bool accent = false)
        {
            return new SoftCard(accent)
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(18),
                Margin = new Padding(0, 0, 0, 10)
            };
        }

        // Paints only its background; normal WinForms controls inside remain
        // accessible, keyboard-focusable, and DPI-aware.
        private sealed class SoftCard : Panel
        {
            private readonly bool accent;

            public SoftCard(bool accent)
            {
                this.accent = accent;
                BackColor = UiBackground;
                SetStyle(ControlStyles.OptimizedDoubleBuffer |
                         ControlStyles.AllPaintingInWmPaint |
                         ControlStyles.UserPaint, true);
            }
            protected override void OnPaintBackground(PaintEventArgs e)
            {
                e.Graphics.Clear(UiBackground);
            }
            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                var rect = new Rectangle(1, 1, Math.Max(1, Width - 3), Math.Max(1, Height - 3));
                using (var path = RoundPath(rect, 12))
                using (var brush = new SolidBrush(UiCard))
                using (var outline = new Pen(Color.FromArgb(45, 66, 86), 1f))
                {
                    e.Graphics.FillPath(brush, path);
                    e.Graphics.DrawPath(outline, path);
                }
                if (accent)
                {
                    using (var accentBrush = new SolidBrush(UiAccent))
                        e.Graphics.FillRectangle(accentBrush, 24, 2, Math.Max(0, Width - 48), 3);
                }
                base.OnPaint(e);
            }
        }

        private sealed class SoftButton : Button
        {
            private bool hovering;
            private bool pressing;
            public SoftButton()
            {
                FlatStyle = FlatStyle.Flat;
                FlatAppearance.BorderSize = 0;
                UseVisualStyleBackColor = false;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                         ControlStyles.OptimizedDoubleBuffer, true);
            }
            protected override void OnMouseEnter(EventArgs e) { hovering = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { hovering = false; pressing = false; Invalidate(); base.OnMouseLeave(e); }
            protected override void OnMouseDown(MouseEventArgs e) { pressing = true; Invalidate(); base.OnMouseDown(e); }
            protected override void OnMouseUp(MouseEventArgs e) { pressing = false; Invalidate(); base.OnMouseUp(e); }
            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.Clear(UiBackground);
                Color background = Enabled ? BackColor : Color.FromArgb(45, 53, 65);
                if (hovering && Enabled) background = ControlPaint.Light(background, 0.10f);
                if (pressing && Enabled) background = ControlPaint.Dark(background, 0.07f);
                using (var path = RoundPath(new Rectangle(1, 1, Math.Max(1, Width - 3), Math.Max(1, Height - 3)), 8))
                using (var fill = new SolidBrush(background))
                {
                    e.Graphics.FillPath(fill, path);
                    if (Focused && ShowFocusCues)
                        using (var edge = new Pen(UiAccent, 1.4f)) e.Graphics.DrawPath(edge, path);
                }
                var textBounds = new Rectangle(7, 2, Math.Max(1, Width - 14), Math.Max(1, Height - 4));
                TextRenderer.DrawText(e.Graphics, Text, Font, textBounds,
                    Enabled ? ForeColor : UiMuted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                    TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine |
                    TextFormatFlags.NoPrefix);
            }
        }

        private static GraphicsPath RoundPath(Rectangle bounds, int radius)
        {
            int d = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
            var path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
            path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
            path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        private void BuildMainLayout()
        {
            // Unlike Dock=Fill with percentage rows, this canvas has a *minimum
            // content height*. When content needs more room it scrolls vertically.
            scrollSurface = new Panel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                BackColor = UiBackground,
                TabStop = true
            };
            Controls.Add(scrollSurface);
            rootLayout = new TableLayoutPanel
            {
                Location = Point.Empty,
                BackColor = UiBackground,
                Padding = new Padding(20, 12, 20, 9),
                ColumnCount = 1,
                RowCount = 7,
                GrowStyle = TableLayoutPanelGrowStyle.FixedSize,
                Margin = new Padding(0)
            };
            rootLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
            rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 164));
            rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 65));
            rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 98));
            rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 49));
            rootLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            rootLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
            scrollSurface.Controls.Add(rootLayout);

            headerLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1,
                BackColor = UiBackground, Margin = new Padding(0)
            };
            headerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            headerLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 107));
            var headings = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2,
                BackColor = UiBackground, Margin = new Padding(0)
            };
            headings.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            headings.RowStyles.Add(new RowStyle(SizeType.Percent, 56));
            headings.RowStyles.Add(new RowStyle(SizeType.Percent, 44));
            var heading = TextLabel("WORK TIMER", 16.5f, true);
            heading.ForeColor = UiAccent;
            lblHeadingHint = TextLabel("Your time, clearly accounted for", 9);
            lblHeadingHint.ForeColor = UiMuted;
            var version = TextLabel("VERSION 1.6.1", 9, true);
            version.ForeColor = UiMuted;
            version.TextAlign = ContentAlignment.MiddleRight;
            headings.Controls.Add(heading, 0, 0);
            headings.Controls.Add(lblHeadingHint, 0, 1);
            headerLayout.Controls.Add(headings, 0, 0);
            headerLayout.Controls.Add(version, 1, 0);
            rootLayout.Controls.Add(headerLayout, 0, 0);

            var hero = Card(true);
            var heroGrid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3,
                BackColor = UiCard, Margin = new Padding(0)
            };
            heroGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            heroGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 59));
            heroGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 19));
            heroGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 22));
            lblTime = TextLabel("00:00:00", 35, true);
            lblTime.TextAlign = ContentAlignment.MiddleCenter;
            lblRunState = TextLabel("PAUSED", 10, true);
            lblRunState.ForeColor = UiDanger;
            lblRunState.TextAlign = ContentAlignment.MiddleCenter;
            lblActiveTask = TextLabel("No active project", 10);
            lblActiveTask.ForeColor = UiMuted;
            lblActiveTask.TextAlign = ContentAlignment.MiddleCenter;
            uiTip.SetToolTip(lblActiveTask, "The project currently being tracked");
            heroGrid.Controls.Add(lblTime, 0, 0);
            heroGrid.Controls.Add(lblRunState, 0, 1);
            heroGrid.Controls.Add(lblActiveTask, 0, 2);
            hero.Controls.Add(heroGrid);
            rootLayout.Controls.Add(hero, 0, 1);

            actionsLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, BackColor = UiBackground,
                Margin = new Padding(0, 0, 0, 8), GrowStyle = TableLayoutPanelGrowStyle.FixedSize
            };
            btnPlayPause = ActionButton("Resume", true);
            btnReset = ActionButton("Reset", false, true);
            btnStop = ActionButton("Stop", false, true);
            btnOpenLog = ActionButton("Open Work Log");
            btnPlayPause.Click += BtnPlayPause_Click;
            btnReset.Click += BtnReset_Click;
            btnStop.Click += BtnStop_Click;
            btnOpenLog.Click += (s, e) =>
            {
                try
                {
                    Directory.CreateDirectory(WorkLogRecorder.ExportFolder);
                    Process.Start(new ProcessStartInfo(WorkLogRecorder.ExportFolder) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "Unable to open Work Log", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };
            mainActions.AddRange(new[] { btnPlayPause, btnReset, btnStop, btnOpenLog });
            rootLayout.Controls.Add(actionsLayout, 0, 2);

            var settingsCard = Card();
            settingsLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, BackColor = UiCard,
                GrowStyle = TableLayoutPanelGrowStyle.FixedSize
            };
            chkUseTaskIcon = Option("Show active task in icon");
            chkShowRemaining = Option("Show remaining time in task icon");
            chkAlwaysOnTop = Option("Always on top");
            chkUseTaskIcon.CheckedChanged += (s, e) => UpdateUiAndIcon();
            chkShowRemaining.CheckedChanged += (s, e) => UpdateUiAndIcon();
            chkAlwaysOnTop.CheckedChanged += (s, e) => TopMost = chkAlwaysOnTop.Checked;
            settingsCard.Controls.Add(settingsLayout);
            rootLayout.Controls.Add(settingsCard, 0, 3);

            var projectsHeading = TextLabel("PROJECTS  /  SELECT A PROJECT TO MANAGE ITS TIME", 10.5f, true);
            projectsHeading.ForeColor = UiMuted;
            rootLayout.Controls.Add(projectsHeading, 0, 4);

            projectCard = Card();
            projectCard.Margin = new Padding(0, 0, 0, 8);
            projectsLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, BackColor = UiCard,
                GrowStyle = TableLayoutPanelGrowStyle.FixedSize
            };
            lstTasks = new ListBox
            {
                Dock = DockStyle.Fill,
                IntegralHeight = false,
                Font = new Font("Segoe UI", 10f),
                ForeColor = UiForeground,
                BackColor = UiField,
                BorderStyle = BorderStyle.None,
                DrawMode = DrawMode.OwnerDrawFixed,
                ItemHeight = 36,
                Margin = new Padding(1, 3, 1, 3),
                HorizontalScrollbar = false
            };
            lstTasks.DrawItem += DrawProjectItem;
            lstTasks.MouseMove += (s, e) =>
            {
                int index = lstTasks.IndexFromPoint(e.Location);
                if (index < 0 || index >= lstTasks.Items.Count)
                    uiTip.SetToolTip(lstTasks, "Select a project to see its full name");
                else
                    uiTip.SetToolTip(lstTasks, lstTasks.Items[index].ToString());
            };
            projectButtonsLayout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, BackColor = UiCard,
                Margin = new Padding(0), GrowStyle = TableLayoutPanelGrowStyle.FixedSize
            };
            btnAddTask = ActionButton("+ Add project", true);
            btnRemoveTask = ActionButton("Remove", false, true);
            btnSetActive = ActionButton("Set active");
            btnRenameTask = ActionButton("Rename");
            btnTaskPause = ActionButton("Pause task");
            btnTaskResume = ActionButton("Resume task");
            btnTaskReset = ActionButton("Reset task", false, true);
            btnSetExpected = ActionButton("Set expected");
            btnAddTask.Click += BtnAddTask_Click;
            btnRemoveTask.Click += BtnRemoveTask_Click;
            btnSetActive.Click += BtnSetActive_Click;
            btnRenameTask.Click += BtnRenameTask_Click;
            btnTaskPause.Click += BtnTaskPause_Click;
            btnTaskResume.Click += BtnTaskResume_Click;
            btnTaskReset.Click += BtnTaskReset_Click;
            btnSetExpected.Click += BtnSetExpected_Click;
            projectActions.AddRange(new[] { btnAddTask, btnRemoveTask, btnSetActive,
                btnRenameTask, btnTaskPause, btnTaskResume, btnTaskReset, btnSetExpected });
            projectsLayout.Controls.Add(lstTasks);
            projectsLayout.Controls.Add(projectButtonsLayout);
            projectCard.Controls.Add(projectsLayout);
            rootLayout.Controls.Add(projectCard, 0, 5);

            lblLogStatus = TextLabel("WEEKLY LOG  ·  " + WorkLogRecorder.ExportFolder, 9);
            lblLogStatus.ForeColor = UiAccent;
            uiTip.SetToolTip(lblLogStatus, WorkLogRecorder.ExportFolder);
            rootLayout.Controls.Add(lblLogStatus, 0, 6);

            scrollSurface.SizeChanged += (s, e) => ReflowLayout();
            ReflowLayout();
        }

        private static CheckBox Option(string text)
        {
            return new CheckBox
            {
                Dock = DockStyle.Fill,
                Text = text,
                AutoSize = false,
                ForeColor = UiForeground,
                BackColor = UiCard,
                FlatStyle = FlatStyle.Standard,
                Margin = new Padding(6, 0, 6, 0),
                TextAlign = ContentAlignment.MiddleLeft
            };
        }

        private void DrawProjectItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= lstTasks.Items.Count) return;
            var task = lstTasks.Items[e.Index] as TaskTimer;
            bool selected = (e.State & DrawItemState.Selected) != 0;
            bool active = ReferenceEquals(task, activeTask);
            using (var back = new SolidBrush(selected ? Color.FromArgb(43, 73, 89) : UiField))
                e.Graphics.FillRectangle(back, e.Bounds);
            if (active)
            {
                using (var marker = new SolidBrush(UiAccent))
                    e.Graphics.FillRectangle(marker, e.Bounds.Left + 1, e.Bounds.Top + 3, 3, Math.Max(1, e.Bounds.Height - 6));
            }
            var bounds = new Rectangle(e.Bounds.Left + 13, e.Bounds.Top + 1,
                Math.Max(1, e.Bounds.Width - 24), Math.Max(1, e.Bounds.Height - 2));
            TextRenderer.DrawText(e.Graphics, task != null ? task.ToString() : "",
                lstTasks.Font, bounds, selected ? UiForeground : UiMuted,
                TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine |
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        private static void RebuildGrid(TableLayoutPanel grid, int columns, int rows,
            int rowHeight, Button[] buttons)
        {
            grid.SuspendLayout();
            grid.Controls.Clear();
            grid.ColumnStyles.Clear();
            grid.RowStyles.Clear();
            grid.ColumnCount = columns;
            grid.RowCount = rows;
            for (int i = 0; i < columns; i++)
                grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / columns));
            for (int i = 0; i < rows; i++)
                grid.RowStyles.Add(new RowStyle(SizeType.Absolute, rowHeight));
            for (int i = 0; i < buttons.Length; i++)
                grid.Controls.Add(buttons[i], i % columns, i / columns);
            grid.ResumeLayout(false);
        }

        private void ReflowLayout()
        {
            if (reflowInProgress || scrollSurface == null ||
                scrollSurface.IsDisposed || rootLayout == null) return;
            reflowInProgress = true;
            try
            {
                // Reserve a scrollbar gutter even when the scrollbar is not yet
                // visible, preventing the page width from oscillating at thresholds.
                int width = Math.Max(P(250), scrollSurface.ClientSize.Width -
                    SystemInformation.VerticalScrollBarWidth - P(3));
                int viewportHeight = scrollSurface.ClientSize.Height;
                if (width == lastLayoutWidth && viewportHeight == lastLayoutHeight &&
                    rootLayout.Height > 0) return;
                lastLayoutWidth = width;
                lastLayoutHeight = viewportHeight;
                bool stackedProjects = width < P(790);
                int actionColumns = width >= P(790) ? 4 : width >= P(535) ? 2 : 1;
                int projectColumns = width >= P(535) ? 2 : 1;
                int settingsColumns = width >= P(705) ? 2 : 1;
                int actionRows = (mainActions.Count + actionColumns - 1) / actionColumns;
                int projectButtonRows = (projectActions.Count + projectColumns - 1) / projectColumns;

                rootLayout.SuspendLayout();
                actionsLayout.SuspendLayout();
                projectsLayout.SuspendLayout();
                settingsLayout.SuspendLayout();
                projectButtonsLayout.SuspendLayout();

                // Explicit pixel row sizes guarantee controls cannot paint into the
                // neighboring row. The whole document, not the controls, scrolls.
                int actionHeight = P(actionRows * 51 + 12);
                int settingsHeight = P(settingsColumns == 2 ? 100 : 133);
                int projectButtonHeight = P(projectButtonRows * 47 + 4);
                int projectHeight = stackedProjects
                    ? P(148 + 16 + 39) + projectButtonHeight
                    : Math.Max(P(265), projectButtonHeight + P(47));
                rootLayout.RowStyles[0].Height = P(65);
                rootLayout.RowStyles[1].Height = P(168);
                rootLayout.RowStyles[2].Height = actionHeight;
                rootLayout.RowStyles[3].Height = settingsHeight;
                rootLayout.RowStyles[4].Height = P(49);
                rootLayout.RowStyles[6].Height = P(42);

                RebuildGrid(actionsLayout, actionColumns, actionRows, P(51), mainActions.ToArray());
                actionsLayout.BackColor = UiBackground;
                settingsLayout.Controls.Clear();
                settingsLayout.ColumnStyles.Clear();
                settingsLayout.RowStyles.Clear();
                settingsLayout.ColumnCount = settingsColumns;
                settingsLayout.RowCount = settingsColumns == 2 ? 2 : 3;
                for (int i = 0; i < settingsColumns; i++)
                    settingsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / settingsColumns));
                for (int i = 0; i < settingsLayout.RowCount; i++)
                    settingsLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / settingsLayout.RowCount));
                settingsLayout.Controls.Add(chkUseTaskIcon, 0, 0);
                settingsLayout.Controls.Add(chkShowRemaining, settingsColumns == 2 ? 1 : 0,
                    settingsColumns == 2 ? 0 : 1);
                settingsLayout.Controls.Add(chkAlwaysOnTop, 0,
                    settingsColumns == 2 ? 1 : 2);

                RebuildGrid(projectButtonsLayout, projectColumns, projectButtonRows,
                    P(47), projectActions.ToArray());
                projectsLayout.Controls.Clear();
                projectsLayout.ColumnStyles.Clear();
                projectsLayout.RowStyles.Clear();
                projectsLayout.ColumnCount = stackedProjects ? 1 : 2;
                projectsLayout.RowCount = stackedProjects ? 2 : 1;
                if (stackedProjects)
                {
                    projectsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                    projectsLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, P(148)));
                    projectsLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                    lstTasks.Margin = new Padding(0, 0, 0, P(8));
                    projectButtonsLayout.Margin = new Padding(0);
                    projectsLayout.Controls.Add(lstTasks, 0, 0);
                    projectsLayout.Controls.Add(projectButtonsLayout, 0, 1);
                }
                else
                {
                    projectsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 59));
                    projectsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 41));
                    projectsLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                    lstTasks.Margin = new Padding(0, 3, P(12), 3);
                    projectButtonsLayout.Margin = new Padding(0);
                    projectsLayout.Controls.Add(lstTasks, 0, 0);
                    projectsLayout.Controls.Add(projectButtonsLayout, 1, 0);
                }

                // No minimum row is ever allowed to collapse because of a short
                // window: the scroll canvas grows instead.
                int contentMinHeight = P(23) + P(65 + 168 + 49 + 42) +
                    actionHeight + settingsHeight + projectHeight;
                int targetHeight = Math.Max(contentMinHeight, scrollSurface.ClientSize.Height);
                rootLayout.SetBounds(0, 0, width, targetHeight);
                scrollSurface.AutoScrollMinSize = new Size(0, targetHeight);
                float clockSize = width < P(510) ? 26f : width < P(700) ? 31f : 37f;
                if (Math.Abs(lblTime.Font.Size - clockSize) > 0.01f)
                {
                    var previousFont = lblTime.Font;
                    lblTime.Font = new Font("Segoe UI", clockSize, FontStyle.Bold);
                    previousFont.Dispose();
                }
                lblHeadingHint.Visible = width >= P(495);
                lstTasks.ItemHeight = P(36);
            }
            finally
            {
                projectButtonsLayout.ResumeLayout(true);
                settingsLayout.ResumeLayout(true);
                projectsLayout.ResumeLayout(true);
                actionsLayout.ResumeLayout(true);
                rootLayout.ResumeLayout(true);
                reflowInProgress = false;
            }
        }

        private void RefreshLogStatus()
        {
            if (lblLogStatus == null) return;
            if (workLog == null)
            {
                lblLogStatus.Text = "WORK LOG UNAVAILABLE: " + workLogInitializationError;
                lblLogStatus.ForeColor = UiDanger;
                uiTip.SetToolTip(lblLogStatus, workLogInitializationError);
            }
            else if (!string.IsNullOrEmpty(workLog.LastError))
            {
                lblLogStatus.Text = "Work log NOT SAVED: " + workLog.LastError;
                lblLogStatus.ForeColor = UiDanger;
                uiTip.SetToolTip(lblLogStatus, workLog.LastError);
            }
            else
            {
                lblLogStatus.Text = "WEEKLY LOG  •  " + WorkLogRecorder.ExportFolder;
                lblLogStatus.ForeColor = UiAccent;
                uiTip.SetToolTip(lblLogStatus, WorkLogRecorder.ExportFolder);
            }
        }

        // ----- Event handlers -------------------------------------------------
        private void BtnSetExpected_Click(object sender, EventArgs e)
        {
            if (lstTasks.SelectedItem is TaskTimer t)
            {
                string current = t.Expected > TimeSpan.Zero
                    ? t.Expected.ToString(@"hh\:mm\:ss")
                    : "01:00:00";

                string input = PromptForText(
                    "Expected Time",
                    "Enter expected duration (hh:mm, hh:mm:ss, or minutes):",
                    current);

                if (string.IsNullOrWhiteSpace(input))
                    return;

                TimeSpan value;
                if (!TryParseTimeSpanFlexible(input.Trim(), out value))
                {
                    MessageBox.Show(this,
                        "Could not parse time. Use hh:mm, hh:mm:ss, or a number of minutes.",
                        "Invalid time",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    return;
                }

                t.Expected = value;
                RefreshTaskListDisplay();
                UpdateUiAndIcon();
                SaveState();
            }
        }

        private bool TryParseTimeSpanFlexible(string text, out TimeSpan result)
        {
            // Try standard TimeSpan parse first (hh:mm or hh:mm:ss)
            if (TimeSpan.TryParse(text, out result))
                return true;

            // If it's just a number, treat it as minutes
            int minutes;
            if (int.TryParse(text, out minutes))
            {
                result = TimeSpan.FromMinutes(minutes);
                return true;
            }

            result = TimeSpan.Zero;
            return false;
        }

        private void TaskbarForm_MouseClick(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
                ToggleTracking();
        }

        private void Timer_Tick(object sender, EventArgs e)
        {
            if (!tracking) return;
            UpdateUiAndIcon();
            RefreshTaskListDisplay();
            if (++secondsSinceCheckpoint >= 15)
            {
                secondsSinceCheckpoint = 0;
                SaveState();
                workLog?.Checkpoint();
                RefreshLogStatus();
            }
        }

        private void InitializeTrayIcon()
        {
            trayToggle = new ToolStripMenuItem("Pause Timer");
            trayToggle.Click += (s, e) => ToggleTracking();
            traySnooze = new ToolStripMenuItem("Snooze reminder (5 min)");
            traySnooze.Click += (s, e) => SnoozeReminder(defaultSnoozeMinutes);

            trayMenu = new ContextMenuStrip();
            trayMenu.Items.Add(trayToggle);
            trayMenu.Items.Add(traySnooze);
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add("Show Work Timer", null, (s, e) => ShowMainWindow());
            trayMenu.Items.Add("Exit", null, (s, e) => Close());
            trayMenu.Opening += (s, e) =>
            {
                trayToggle.Text = tracking ? "Pause Timer" : "Resume Timer";
                traySnooze.Text = "Snooze reminder (" + defaultSnoozeMinutes + " min)";
                traySnooze.Enabled = !tracking && remindersEnabledForPause;
            };
            trayIcon = new NotifyIcon
            {
                Icon = currentIcon ?? SystemIcons.Application,
                Text = "Work Timer",
                ContextMenuStrip = trayMenu,
                Visible = true
            };
            trayIcon.DoubleClick += (s, e) => ShowMainWindow();
        }

        private void ShowMainWindow()
        {
            if (WindowState == FormWindowState.Minimized)
                WindowState = FormWindowState.Normal;
            Show();
            BringToFront();
            Activate();
        }

        private void ReminderTimer_Tick(object sender, EventArgs e)
        {
            if (tracking || !remindersEnabledForPause) return;

            uint inputTick = ReadLastInputTick();
            uint idleMs = unchecked((uint)Environment.TickCount - inputTick);
            bool userActive = idleMs < AwayThresholdMs;

            // An absence of at least 30 seconds unlocks an EARLY reminder, but
            // only on fresh input after the user returns. No popup while away.
            if (!userActive) wasAwayDuringPause = true;
            bool returnedNow = wasAwayDuringPause && userActive && inputTick != observedLastInputTick;
            observedLastInputTick = inputTick;
            if (returnedNow) wasAwayDuringPause = false;

            if (!userActive || DateTime.UtcNow < snoozeUntilUtc ||
                (reminderForm != null && !reminderForm.IsDisposed)) return;

            if (returnedNow || DateTime.UtcNow - pauseStartedUtc >= PauseGrace)
                ShowPausedReminder();
        }

        private void ShowPausedReminder()
        {
            if (tracking || !remindersEnabledForPause ||
                (reminderForm != null && !reminderForm.IsDisposed)) return;

            var pop = new Form
            {
                Text = "Work Timer  •  Paused",
                ClientSize = new Size(535, 286),
                MinimumSize = new Size(535, 320),
                FormBorderStyle = FormBorderStyle.Sizable,
                MaximizeBox = false,
                MinimizeBox = false,
                ShowInTaskbar = true,
                TopMost = true,
                StartPosition = FormStartPosition.Manual,
                AutoScaleMode = AutoScaleMode.Dpi,
                Font = new Font("Segoe UI", 10),
                BackColor = UiBackground,
                ForeColor = UiForeground
            };
            reminderForm = pop;
            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(22, 16, 22, 13),
                ColumnCount = 1,
                RowCount = 5,
                BackColor = UiBackground
            };
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 49));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            var title = TextLabel("YOUR WORK TIMER IS PAUSED", 14, true);
            title.ForeColor = UiAccent;
            layout.Controls.Add(title, 0, 0);

            var description = TextLabel(
                "Working again? Resume tracking your hours. If you're using the PC for something else, snooze this reminder.", 10);
            description.AutoEllipsis = false;
            layout.Controls.Add(description, 0, 1);

            var snoozeRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1 };
            snoozeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            snoozeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84));
            snoozeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 76));
            var lblMinutes = TextLabel("Remind me again in", 10);
            var minutes = new NumericUpDown
            {
                Minimum = 1, Maximum = 1440, Value = defaultSnoozeMinutes,
                Dock = DockStyle.Fill,
                Margin = new Padding(3, 11, 3, 10),
                BackColor = UiCard, ForeColor = UiForeground,
                TextAlign = HorizontalAlignment.Center
            };
            var suffix = TextLabel("minutes", 10);
            suffix.TextAlign = ContentAlignment.MiddleCenter;
            snoozeRow.Controls.Add(lblMinutes, 0, 0);
            snoozeRow.Controls.Add(minutes, 1, 0);
            snoozeRow.Controls.Add(suffix, 2, 0);
            layout.Controls.Add(snoozeRow, 0, 2);

            var actions = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1 };
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 166));
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 138));
            var resume = ActionButton("Resume timer", true);
            var snooze = ActionButton("Snooze");
            actions.Controls.Add(resume, 1, 0);
            actions.Controls.Add(snooze, 2, 0);
            layout.Controls.Add(actions, 0, 3);
            var footer = TextLabel("Closing this window also snoozes your reminder.", 8.5f);
            footer.ForeColor = UiMuted;
            layout.Controls.Add(footer, 0, 4);

            resume.Click += (s, e) => { if (!tracking) ToggleTracking(); };
            snooze.Click += (s, e) => SnoozeReminder((int)minutes.Value);
            pop.FormClosing += (s, e) =>
            {
                // X / Alt+F4 snoozes rather than causing an instant new reminder.
                if (!closingReminderByAction && !tracking && remindersEnabledForPause)
                {
                    defaultSnoozeMinutes = (int)minutes.Value;
                    snoozeUntilUtc = DateTime.UtcNow.AddMinutes(defaultSnoozeMinutes);
                    SaveState();
                }
            };
            pop.FormClosed += (s, e) =>
            {
                if (ReferenceEquals(reminderForm, pop)) reminderForm = null;
                pop.Dispose();
            };
            pop.Controls.Add(layout);
            pop.AcceptButton = resume;
            pop.CancelButton = snooze;
            Rectangle workArea = Screen.FromPoint(Cursor.Position).WorkingArea;
            pop.Location = new Point(
                workArea.Left + (workArea.Width - pop.Width) / 2,
                workArea.Top + (workArea.Height - pop.Height) / 2);
            SystemSounds.Exclamation.Play();
            pop.Show();
            pop.BringToFront();
            pop.Activate();
        }

        private void SnoozeReminder(int minutes)
        {
            defaultSnoozeMinutes = Math.Max(1, Math.Min(1440, minutes));
            snoozeUntilUtc = DateTime.UtcNow.AddMinutes(defaultSnoozeMinutes);
            CloseReminder();
            SaveState();
        }

        private void CloseReminder()
        {
            if (reminderForm == null || reminderForm.IsDisposed) return;
            closingReminderByAction = true;
            try { reminderForm.Close(); }
            finally { closingReminderByAction = false; }
        }

        private void OnPausedByUser()
        {
            remindersEnabledForPause = true;
            snoozeUntilUtc = DateTime.MinValue;
            pauseStartedUtc = DateTime.UtcNow;
            observedLastInputTick = ReadLastInputTick();
            wasAwayDuringPause = false;
        }

        private void OnResumedOrStopped()
        {
            remindersEnabledForPause = false;
            snoozeUntilUtc = DateTime.MinValue;
            wasAwayDuringPause = false;
            CloseReminder();
        }


        private void BtnPlayPause_Click(object sender, EventArgs e)
        {
            ToggleTracking();
        }

        private void BtnRenameTask_Click(object sender, EventArgs e)
        {
            if (lstTasks.SelectedItem is TaskTimer task)
            {
                string newName = PromptForText(
                    "Rename Task",
                    $"Rename task '{task.Name}' to:",
                    task.Name);

                if (!string.IsNullOrWhiteSpace(newName))
                {
                    task.Name = newName.Trim();
                    if (ReferenceEquals(activeTask, task) && tracking)
                        workLog?.SwitchProject(task.Name);
                    RefreshTaskListDisplay();
                    SaveState();
                    RefreshLogStatus();
                }
            }
        }

        private string PromptForText(string title, string caption, string defaultValue)
        {
            using (var form = new Form())
            {
                form.Text = title;
                form.ClientSize = new Size(510, 192);
                form.MinimumSize = new Size(510, 235);
                form.FormBorderStyle = FormBorderStyle.Sizable;
                form.StartPosition = FormStartPosition.CenterParent;
                form.MinimizeBox = false;
                form.MaximizeBox = false;
                form.AutoScaleMode = AutoScaleMode.Dpi;
                form.Font = new Font("Segoe UI", 10);
                form.BackColor = UiBackground;
                var grid = new TableLayoutPanel
                {
                    Dock = DockStyle.Fill, Padding = new Padding(18),
                    ColumnCount = 1, RowCount = 3
                };
                grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 43));
                grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
                var label = TextLabel(caption, 10);
                label.AutoEllipsis = false;
                var textBox = new TextBox
                {
                    Text = defaultValue, Dock = DockStyle.Fill,
                    BackColor = UiCard, ForeColor = UiForeground,
                    Margin = new Padding(0, 5, 0, 5)
                };
                var buttons = new TableLayoutPanel
                {
                    Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1
                };
                buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 106));
                buttons.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 106));
                var ok = ActionButton("OK", true);
                var cancel = ActionButton("Cancel");
                ok.DialogResult = DialogResult.OK;
                cancel.DialogResult = DialogResult.Cancel;
                buttons.Controls.Add(ok, 1, 0);
                buttons.Controls.Add(cancel, 2, 0);
                grid.Controls.Add(label, 0, 0);
                grid.Controls.Add(textBox, 0, 1);
                grid.Controls.Add(buttons, 0, 2);
                form.Controls.Add(grid);
                form.AcceptButton = ok;
                form.CancelButton = cancel;
                return form.ShowDialog(this) == DialogResult.OK ? textBox.Text : null;
            }
        }

        private void SaveState()
        {
            if (string.IsNullOrEmpty(dataFilePath))
                return;

            try
            {
                // If now running, fold in current run so we save up-to-date time
                if (tracking)
                {
                    accumulated += DateTime.Now - lastStart;
                    lastStart = DateTime.Now;
                }

                int activeIdx = -1;
                if (activeTask != null)
                    activeIdx = tasks.IndexOf(activeTask);

                int useTaskIconFlag = (chkUseTaskIcon != null && chkUseTaskIcon.Checked) ? 1 : 0;
                int showRemFlag = (chkShowRemaining != null && chkShowRemaining.Checked) ? 1 : 0;
                int alwaysOnTopFlag = (chkAlwaysOnTop != null && chkAlwaysOnTop.Checked) ? 1 : 0;

                long globalSec = (long)accumulated.TotalSeconds;

                var lines = new List<string>();

                // GLOBAL line: accumSeconds | activeTaskIndex | useTaskIcon | showRemaining | alwaysOnTop
                lines.Add($"GLOBAL|{globalSec}|{activeIdx}|{useTaskIconFlag}|{showRemFlag}|{alwaysOnTopFlag}|{defaultSnoozeMinutes}");

                // TASK lines: name | accumSeconds | expectedSeconds
                foreach (var t in tasks)
                {
                    long accSec = (long)t.Accumulated.TotalSeconds;
                    long expSec = (long)t.Expected.TotalSeconds;
                    string name = Uri.EscapeDataString(t.Name ?? "");
                    // Store live task time too, not just the accumulated amount from
                    // the previous pause. Avoid throwing away the current run on exit.
                    if (t.IsRunning)
                        accSec = (long)t.GetElapsed().TotalSeconds;
                    lines.Add($"TASK|{name}|{accSec}|{expSec}");
                }

                File.WriteAllLines(dataFilePath, lines);
            }
            catch
            {
                // ignore I/O errors for now
            }
        }

        private void LoadState()
        {
            // Default: paused, zeroed unless file says otherwise
            tracking = false;
            accumulated = TimeSpan.Zero;
            lastStart = DateTime.Now;
            tasks.Clear();
            activeTask = null;

            if (string.IsNullOrEmpty(dataFilePath) || !File.Exists(dataFilePath))
                return;

            try
            {
                string[] lines = File.ReadAllLines(dataFilePath);
                int activeIdx = -1;

                foreach (string line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    var parts = line.Split('|');
                    if (parts.Length < 2)
                        continue;

                    if (parts[0] == "GLOBAL")
                    {
                        if (parts.Length > 1 && long.TryParse(parts[1], out var sec))
                            accumulated = TimeSpan.FromSeconds(sec);

                        if (parts.Length > 2 && int.TryParse(parts[2], out var idx))
                            activeIdx = idx;

                        if (parts.Length > 3 && chkUseTaskIcon != null && int.TryParse(parts[3], out var useFlag))
                            chkUseTaskIcon.Checked = (useFlag != 0);

                        if (parts.Length > 4 && chkShowRemaining != null && int.TryParse(parts[4], out var remFlag))
                            chkShowRemaining.Checked = (remFlag != 0);

                        if (parts.Length > 5 && chkAlwaysOnTop != null && int.TryParse(parts[5], out var topFlag))
                        {
                            chkAlwaysOnTop.Checked = (topFlag != 0);
                            this.TopMost = chkAlwaysOnTop.Checked;
                        }
                        if (parts.Length > 6 && int.TryParse(parts[6], out var minutes))
                            defaultSnoozeMinutes = Math.Max(1, Math.Min(1440, minutes));
                    }
                    else if (parts[0] == "TASK" && parts.Length >= 4)
                    {
                        string name = Uri.UnescapeDataString(parts[1]);

                        long accSec = 0;
                        long expSec = 0;
                        long.TryParse(parts[2], out accSec);
                        long.TryParse(parts[3], out expSec);

                        var t = new TaskTimer
                        {
                            Name = name,
                            Accumulated = TimeSpan.FromSeconds(accSec),
                            Expected = TimeSpan.FromSeconds(expSec)
                        };

                        tasks.Add(t);
                    }
                }

                if (activeIdx >= 0 && activeIdx < tasks.Count)
                    activeTask = tasks[activeIdx];

                // After load we start paused; user hits Play when ready
                tracking = false;
                btnPlayPause.Text = "Play";
                lastStart = DateTime.Now;

                RefreshTaskListDisplay();
            }
            catch
            {
                // ignore parse errors, fall back to defaults
            }
        }


        private void BtnTaskPause_Click(object sender, EventArgs e)
        {
            if (lstTasks.SelectedItem is TaskTimer t)
            {
                t.Pause();
                if (tracking && ReferenceEquals(activeTask, t)) workLog?.SwitchProject(null);
                RefreshTaskListDisplay();
                RefreshLogStatus();
                SaveState();
            }
        }

        private void BtnTaskResume_Click(object sender, EventArgs e)
        {
            if (lstTasks.SelectedItem is TaskTimer t)
            {
                t.Start();
                if (tracking && ReferenceEquals(activeTask, t)) workLog?.SwitchProject(t.Name);
                RefreshTaskListDisplay();
                RefreshLogStatus();
                SaveState();
            }
        }

        private void BtnTaskReset_Click(object sender, EventArgs e)
        {
            if (lstTasks.SelectedItem is TaskTimer t)
            {
                if (tracking && ReferenceEquals(activeTask, t)) workLog?.SwitchProject(null);
                t.Reset();
                RefreshTaskListDisplay();
                SaveState();
                RefreshLogStatus();
            }
        }


        private void BtnReset_Click(object sender, EventArgs e)
        {
            accumulated = TimeSpan.Zero;
            lastStart = DateTime.Now;
            tracking = true;
            timer.Start();
            btnPlayPause.Text = "Pause";
            OnResumedOrStopped();
            workLog?.Finish();
            workLog?.Start(activeTask?.Name);
            RefreshLogStatus();

            activeTask?.Reset();
            activeTask?.Start();
            SaveState();
            UpdateUiAndIcon();
        }


        private void BtnStop_Click(object sender, EventArgs e)
        {
            accumulated = GetWorked();
            tracking = false;
            timer.Stop();
            activeTask?.Pause();
            OnResumedOrStopped(); // Stop should not nag the user to keep working.
            workLog?.Finish();
            RefreshLogStatus();
            btnPlayPause.Text = "Resume";
            SaveState();
            UpdateUiAndIcon();
        }

        private void BtnAddTask_Click(object sender, EventArgs e)
        {
            string name = $"Task {tasks.Count + 1}";
            var task = new TaskTimer { Name = name };
            tasks.Add(task);

            if (activeTask == null)
            {
                activeTask = task;
                if (tracking)
                {
                    activeTask.Start();
                    workLog?.SwitchProject(task.Name);
                }
            }

            RefreshTaskListDisplay();
            SaveState();
        }

        private void BtnRemoveTask_Click(object sender, EventArgs e)
        {
            if (lstTasks.SelectedItem is TaskTimer t)
            {
                if (ReferenceEquals(activeTask, t))
                {
                    activeTask?.Pause();
                    activeTask = null;
                    if (tracking) workLog?.SwitchProject(null);
                }
                tasks.Remove(t);
                RefreshTaskListDisplay();
                SaveState();
            }
        }

        private void BtnSetActive_Click(object sender, EventArgs e)
        {
            if (lstTasks.SelectedItem is TaskTimer t)
            {
                if (activeTask != null && !ReferenceEquals(activeTask, t))
                    activeTask.Pause();
                activeTask = t;

                // If main timer is running, make sure this task is running too
                if (tracking)
                {
                    activeTask.Start();
                    workLog?.SwitchProject(activeTask.Name);
                }

                RefreshTaskListDisplay();
                SaveState();
                RefreshLogStatus();
            }
        }

        private void RefreshTaskListDisplay()
        {
            var selectedTask = lstTasks.SelectedItem as TaskTimer;
            lstTasks.BeginUpdate();
            lstTasks.Items.Clear();
            foreach (var t in tasks)
                lstTasks.Items.Add(t);
            if (selectedTask != null && tasks.Contains(selectedTask))
                lstTasks.SelectedItem = selectedTask;
            lstTasks.EndUpdate();
        }


        // ----- Logic ----------------------------------------------------------

        private void ToggleTracking()
        {
            if (tracking)
            {
                // Running -> Paused
                accumulated += DateTime.Now - lastStart;
                tracking = false;
                timer.Stop();

                // pause active task timer
                activeTask?.Pause();
                workLog?.Finish();
                OnPausedByUser();
            }
            else
            {
                // Paused -> Running
                lastStart = DateTime.Now;
                tracking = true;
                timer.Start();

                // resume active task timer
                activeTask?.Start();
                workLog?.Start(activeTask?.Name);
                OnResumedOrStopped();
            }

            btnPlayPause.Text = tracking ? "Pause" : "Resume";
            SaveState();
            RefreshLogStatus();
            UpdateUiAndIcon();
        }


        private TimeSpan GetWorked()
        {
            if (tracking)
                return accumulated + (DateTime.Now - lastStart);
            else
                return accumulated;
        }

        private void UpdateUiAndIcon()
        {
            TimeSpan mainTime = GetWorked();

            lblTime.Text = string.Format("{0:D2}:{1:D2}:{2:D2}",
                (int)mainTime.TotalHours, mainTime.Minutes, mainTime.Seconds);
            lblRunState.Text = tracking ? "●  TRACKING WORK" : "●  PAUSED";
            lblRunState.ForeColor = tracking ? UiAccent : UiDanger;
            if (activeTask != null)
            {
                var at = activeTask.GetElapsed();
                lblActiveTask.Text = activeTask.Name + "   ·   " +
                    string.Format("{0:D2}:{1:D2}:{2:D2}",
                        (int)at.TotalHours, at.Minutes, at.Seconds);
                uiTip.SetToolTip(lblActiveTask, activeTask.Name);
            }
            else
            {
                lblActiveTask.Text = "No active project — work will be logged as Unassigned";
                uiTip.SetToolTip(lblActiveTask, lblActiveTask.Text);
            }

            // -------- Decide what goes into the taskbar icon --------
            TimeSpan iconTime = mainTime; // default = whole timer

            if (chkUseTaskIcon != null && chkUseTaskIcon.Checked && activeTask != null)
            {
                // In task mode: choose elapsed or remaining based on second checkbox
                if (chkShowRemaining != null && chkShowRemaining.Checked && activeTask.Expected > TimeSpan.Zero)
                    iconTime = activeTask.GetRemaining();
                else
                    iconTime = activeTask.GetElapsed();
            }


            string compact = FormatCompact(iconTime);

            Icon oldIcon = currentIcon;
            currentIcon = CreateIcon(compact);
            ApplyWindowIcon(currentIcon);
            if (trayIcon != null)
            {
                trayIcon.Icon = currentIcon;
                string status = tracking ? "running" : "paused";
                string tooltip = "Work Timer " + status + ": " +
                    string.Format("{0:D2}:{1:D2}:{2:D2}",
                        (int)mainTime.TotalHours, mainTime.Minutes, mainTime.Seconds);
                trayIcon.Text = tooltip.Length <= 63 ? tooltip : tooltip.Substring(0, 63);
            }
            oldIcon?.Dispose();
            this.Text = "Work Timer";
        }

        private void ApplyWindowIcon(Icon icon)
        {
            if (icon == null)
                return;

            // Form.Icon updates the title-bar icon. WM_SETICON forces Windows to also
            // use the generated icon as the large taskbar icon in published EXEs.
            this.Icon = icon;

            if (IsHandleCreated)
            {
                SendMessage(this.Handle, WM_SETICON, (IntPtr)ICON_SMALL, icon.Handle);
                SendMessage(this.Handle, WM_SETICON, (IntPtr)ICON_BIG, icon.Handle);
                SendMessage(this.Handle, WM_SETICON, (IntPtr)ICON_SMALL2, icon.Handle);
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);

            if (currentIcon != null)
                ApplyWindowIcon(currentIcon);
        }

        protected override void WndProc(ref Message m)
        {
            // Windows 11 can ask the window for ICON_SMALL2 when drawing the taskbar
            // button, especially for a Release/published EXE. Returning the generated
            // icon here keeps the taskbar from falling back to the static EXE icon.
            if (m.Msg == WM_GETICON && currentIcon != null && currentIcon.Handle != IntPtr.Zero)
            {
                int requestedIcon = m.WParam.ToInt32();
                if (requestedIcon == ICON_SMALL || requestedIcon == ICON_BIG || requestedIcon == ICON_SMALL2)
                {
                    m.Result = currentIcon.Handle;
                    return;
                }
            }

            base.WndProc(ref m);
        }


        private static string FormatCompact(TimeSpan t)
        {
            // Always 2–3 chars max so it fits nicely
            if (t.TotalHours >= 10)
            {
                return ((int)t.TotalHours).ToString("00");   // 10, 11, 12...
            }
            else if (t.TotalHours >= 1)
            {
                return t.TotalHours.ToString("0.0").Replace(',', '.'); // 1.2, 3.4
            }
            else if (t.TotalMinutes >= 1)
            {
                int m = (int)t.TotalMinutes;
                if (m < 10) return m + "m";   // 1m..9m
                return m.ToString("00");      // 10..59
            }
            else
            {
                return t.Seconds.ToString("00"); // 00..59
            }
        }

        private Icon CreateIcon(string text)
        {
            const int size = 64; // draw big, Windows will downscale

            using (Bitmap bmp = new Bitmap(size, size))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
                g.Clear(Color.Transparent);

                using (Font f = new Font("Segoe UI", 36f, FontStyle.Regular, GraphicsUnit.Pixel))
                using (Brush b = new SolidBrush(Color.White))
                {
                    RectangleF rect = new RectangleF(0, 8, size, size - 15);
                    StringFormat sf = new StringFormat
                    {
                        Alignment = StringAlignment.Center,
                        LineAlignment = StringAlignment.Center,
                        FormatFlags = StringFormatFlags.NoWrap
                    };
                    g.DrawString(text, f, b, rect, sf);
                }

                IntPtr hIcon = bmp.GetHicon();
                Icon raw = Icon.FromHandle(hIcon);
                Icon clone = (Icon)raw.Clone();
                DestroyIcon(hIcon);
                raw.Dispose();
                return clone;
            }
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            workLog?.Finish();
            SaveState();
            reminderTimer?.Stop();
            reminderTimer?.Dispose();
            OnResumedOrStopped();
            if (trayIcon != null)
            {
                trayIcon.Visible = false;
                trayIcon.Icon = null;
                trayIcon.Dispose();
            }
            trayMenu?.Dispose();
            timer?.Dispose();
            currentIcon?.Dispose();
            base.OnFormClosed(e);
        }
    }
    public class TaskTimer
    {
        public string Name { get; set; }
        public TimeSpan Accumulated { get; set; } = TimeSpan.Zero;
        public DateTime? LastStart { get; set; } = null;
        public TimeSpan Expected { get; set; } = TimeSpan.Zero;


        public bool IsRunning => LastStart.HasValue;

        public TimeSpan GetElapsed()
        {
            return Accumulated + (LastStart.HasValue ? DateTime.Now - LastStart.Value : TimeSpan.Zero);
        }

        public void Start()
        {
            if (!LastStart.HasValue)
                LastStart = DateTime.Now;
        }

        public void Pause()
        {
            if (LastStart.HasValue)
            {
                Accumulated += DateTime.Now - LastStart.Value;
                LastStart = null;
            }
        }

        public void Reset()
        {
            Accumulated = TimeSpan.Zero;
            LastStart = null;
        }

        public TimeSpan GetRemaining()
        {
            if (Expected <= TimeSpan.Zero)
                return TimeSpan.Zero;

            var remaining = Expected - GetElapsed();
            return remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining;
        }

        public override string ToString()
        {
            var t = GetElapsed();
            string text = $"{Name} - {(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2}";

            if (Expected > TimeSpan.Zero)
            {
                TimeSpan rem = GetRemaining();
                text += $" / {FormatSpan(Expected)} (left {FormatSpan(rem)})";
            }

            return text;
        }

        private static string FormatSpan(TimeSpan ts)
        {
            return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
        }

    }

}
