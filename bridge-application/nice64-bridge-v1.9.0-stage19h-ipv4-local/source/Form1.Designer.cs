namespace PicoSwitchController;

partial class Form1
{
    /// <summary>
    ///  Required designer variable.
    /// </summary>
    private System.ComponentModel.IContainer components = null;

    /// <summary>
    ///  Clean up any resources being used.
    /// </summary>
    /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing && (components != null))
        {
            components.Dispose();
        }
        base.Dispose(disposing);
    }

    #region Windows Form Designer generated code

    /// <summary>
    ///  Required method for Designer support - do not modify
    ///  the contents of this method with the code editor.
    /// </summary>
    private void InitializeComponent()
    {
        components = new System.ComponentModel.Container();
        menuStrip = new MenuStrip();
        toolsMenuItem = new ToolStripMenuItem();
        configureControllerMenuItem = new ToolStripMenuItem();
        videoMenuItem = new ToolStripMenuItem();
        startVideoMenuItem = new ToolStripMenuItem();
        stopVideoMenuItem = new ToolStripMenuItem();
        fullScreenVideoMenuItem = new ToolStripMenuItem();
        openExternalViewerMenuItem = new ToolStripMenuItem();
        titleLabel = new Label();
        consoleLabel = new Label();
        consoleComboBox = new ComboBox();
        portLabel = new Label();
        portComboBox = new ComboBox();
        refreshButton = new Button();
        connectButton = new Button();
        clearButton = new Button();
        forceLowButton = new Button();
        forceHighButton = new Button();
        forceOffButton = new Button();
        n64StatButton = new Button();
        n64ClearDebugButton = new Button();
        buttonPad = new Panel();
        statusLabel = new Label();
        videoGroupBox = new GroupBox();
        videoPictureBox = new PictureBox();
        videoControlsPanel = new FlowLayoutPanel();
        videoDeviceLabel = new Label();
        videoDeviceComboBox = new ComboBox();
        refreshVideoButton = new Button();
        audioDeviceLabel = new Label();
        audioDeviceComboBox = new ComboBox();
        startAudioButton = new Button();
        stopAudioButton = new Button();
        audioVolumeLabel = new Label();
        audioVolumeTrackBar = new TrackBar();
        audioVolumeValueLabel = new Label();
        videoWidthLabel = new Label();
        videoWidthNumeric = new NumericUpDown();
        videoHeightLabel = new Label();
        videoHeightNumeric = new NumericUpDown();
        videoFpsLabel = new Label();
        videoFpsNumeric = new NumericUpDown();
        startVideoButton = new Button();
        stopVideoButton = new Button();
        fullScreenVideoButton = new Button();
        menuStrip.SuspendLayout();
        videoGroupBox.SuspendLayout();
        ((System.ComponentModel.ISupportInitialize)videoPictureBox).BeginInit();
        videoControlsPanel.SuspendLayout();
        ((System.ComponentModel.ISupportInitialize)videoWidthNumeric).BeginInit();
        ((System.ComponentModel.ISupportInitialize)videoHeightNumeric).BeginInit();
        ((System.ComponentModel.ISupportInitialize)videoFpsNumeric).BeginInit();
        ((System.ComponentModel.ISupportInitialize)audioVolumeTrackBar).BeginInit();
        SuspendLayout();
        //
        // menuStrip
        //
        menuStrip.Items.AddRange(new ToolStripItem[] { toolsMenuItem, videoMenuItem });
        menuStrip.Location = new Point(0, 0);
        menuStrip.Name = "menuStrip";
        menuStrip.Size = new Size(1248, 24);
        menuStrip.TabIndex = 0;
        //
        // toolsMenuItem
        //
        toolsMenuItem.DropDownItems.AddRange(new ToolStripItem[] { configureControllerMenuItem });
        toolsMenuItem.Name = "toolsMenuItem";
        toolsMenuItem.Size = new Size(46, 20);
        toolsMenuItem.Text = "Tools";
        //
        // configureControllerMenuItem
        //
        configureControllerMenuItem.Name = "configureControllerMenuItem";
        configureControllerMenuItem.Size = new Size(190, 22);
        configureControllerMenuItem.Text = "Configure Controller...";
        configureControllerMenuItem.Click += ConfigureControllerMenuItem_Click;
        //
        // videoMenuItem
        //
        videoMenuItem.DropDownItems.AddRange(new ToolStripItem[] { startVideoMenuItem, stopVideoMenuItem, fullScreenVideoMenuItem, openExternalViewerMenuItem });
        videoMenuItem.Name = "videoMenuItem";
        videoMenuItem.Size = new Size(49, 20);
        videoMenuItem.Text = "Video";
        //
        // startVideoMenuItem
        //
        startVideoMenuItem.Name = "startVideoMenuItem";
        startVideoMenuItem.Size = new Size(145, 22);
        startVideoMenuItem.Text = "Start Preview";
        startVideoMenuItem.Click += StartVideoButton_Click;
        //
        // stopVideoMenuItem
        //
        stopVideoMenuItem.Enabled = false;
        stopVideoMenuItem.Name = "stopVideoMenuItem";
        stopVideoMenuItem.Size = new Size(145, 22);
        stopVideoMenuItem.Text = "Stop Preview";
        stopVideoMenuItem.Click += StopVideoButton_Click;
        //
        // fullScreenVideoMenuItem
        //
        fullScreenVideoMenuItem.Name = "fullScreenVideoMenuItem";
        fullScreenVideoMenuItem.Size = new Size(145, 22);
        fullScreenVideoMenuItem.Text = "Full Screen";
        fullScreenVideoMenuItem.Click += FullScreenVideoButton_Click;
        //
        // openExternalViewerMenuItem
        //
        openExternalViewerMenuItem.Name = "openExternalViewerMenuItem";
        openExternalViewerMenuItem.Size = new Size(185, 22);
        openExternalViewerMenuItem.Text = "Open External Viewer";
        openExternalViewerMenuItem.Click += OpenExternalViewerMenuItem_Click;
        //
        // titleLabel
        //
        titleLabel.AutoSize = true;
        titleLabel.Font = new Font("Segoe UI", 16F, FontStyle.Bold);
        titleLabel.Location = new Point(20, 42);
        titleLabel.Name = "titleLabel";
        titleLabel.Size = new Size(264, 30);
        titleLabel.TabIndex = 1;
        titleLabel.Text = "NICE 64/SNES Controller";
        //
        // consoleLabel
        //
        consoleLabel.AutoSize = true;
        consoleLabel.Location = new Point(24, 90);
        consoleLabel.Name = "consoleLabel";
        consoleLabel.Size = new Size(48, 15);
        consoleLabel.TabIndex = 2;
        consoleLabel.Text = "Console";
        //
        // consoleComboBox
        //
        consoleComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
        consoleComboBox.FormattingEnabled = true;
        consoleComboBox.Location = new Point(24, 112);
        consoleComboBox.Name = "consoleComboBox";
        consoleComboBox.Size = new Size(118, 23);
        consoleComboBox.TabIndex = 3;
        consoleComboBox.SelectedIndexChanged += ConsoleComboBox_SelectedIndexChanged;
        //
        // portLabel
        //
        portLabel.AutoSize = true;
        portLabel.Location = new Point(154, 90);
        portLabel.Name = "portLabel";
        portLabel.Size = new Size(58, 15);
        portLabel.TabIndex = 4;
        portLabel.Text = "COM port";
        //
        // portComboBox
        //
        portComboBox.FormattingEnabled = true;
        portComboBox.Location = new Point(154, 112);
        portComboBox.Name = "portComboBox";
        portComboBox.Size = new Size(118, 23);
        portComboBox.TabIndex = 5;
        portComboBox.Text = "COM5";
        //
        // refreshButton
        //
        refreshButton.Location = new Point(284, 111);
        refreshButton.Name = "refreshButton";
        refreshButton.Size = new Size(82, 25);
        refreshButton.TabIndex = 6;
        refreshButton.Text = "Refresh";
        refreshButton.UseVisualStyleBackColor = true;
        refreshButton.Click += RefreshButton_Click;
        //
        // connectButton
        //
        connectButton.Location = new Point(378, 111);
        connectButton.Name = "connectButton";
        connectButton.Size = new Size(100, 25);
        connectButton.TabIndex = 7;
        connectButton.Text = "Connect";
        connectButton.UseVisualStyleBackColor = true;
        connectButton.Click += ConnectButton_Click;
        //
        // clearButton
        //
        clearButton.Location = new Point(490, 111);
        clearButton.Name = "clearButton";
        clearButton.Size = new Size(82, 25);
        clearButton.TabIndex = 8;
        clearButton.Text = "Clear";
        clearButton.UseVisualStyleBackColor = true;
        clearButton.Click += ClearButton_Click;
        //
        // forceLowButton
        //
        forceLowButton.Location = new Point(24, 150);
        forceLowButton.Name = "forceLowButton";
        forceLowButton.Size = new Size(118, 25);
        forceLowButton.TabIndex = 9;
        forceLowButton.Text = "Data Low Test";
        forceLowButton.UseVisualStyleBackColor = true;
        forceLowButton.Click += ForceLowButton_Click;
        //
        // forceHighButton
        //
        forceHighButton.Location = new Point(154, 150);
        forceHighButton.Name = "forceHighButton";
        forceHighButton.Size = new Size(118, 25);
        forceHighButton.TabIndex = 10;
        forceHighButton.Text = "Data High Test";
        forceHighButton.UseVisualStyleBackColor = true;
        forceHighButton.Click += ForceHighButton_Click;
        //
        // forceOffButton
        //
        forceOffButton.Location = new Point(284, 150);
        forceOffButton.Name = "forceOffButton";
        forceOffButton.Size = new Size(118, 25);
        forceOffButton.TabIndex = 11;
        forceOffButton.Text = "Resume SNES";
        forceOffButton.UseVisualStyleBackColor = true;
        forceOffButton.Click += ForceOffButton_Click;
        //
        // n64StatButton
        //
        n64StatButton.Location = new Point(414, 150);
        n64StatButton.Name = "n64StatButton";
        n64StatButton.Size = new Size(100, 25);
        n64StatButton.TabIndex = 12;
        n64StatButton.Text = "N64 Stat";
        n64StatButton.UseVisualStyleBackColor = true;
        n64StatButton.Click += N64StatButton_Click;
        //
        // n64ClearDebugButton
        //
        n64ClearDebugButton.Location = new Point(526, 150);
        n64ClearDebugButton.Name = "n64ClearDebugButton";
        n64ClearDebugButton.Size = new Size(100, 25);
        n64ClearDebugButton.TabIndex = 13;
        n64ClearDebugButton.Text = "N64 Clear";
        n64ClearDebugButton.UseVisualStyleBackColor = true;
        n64ClearDebugButton.Click += N64ClearDebugButton_Click;
        //
        // buttonPad
        //
        buttonPad.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left;
        buttonPad.BackColor = Color.FromArgb(248, 248, 248);
        buttonPad.BorderStyle = BorderStyle.FixedSingle;
        buttonPad.Location = new Point(18, 192);
        buttonPad.Name = "buttonPad";
        buttonPad.Size = new Size(630, 320);
        buttonPad.TabIndex = 14;
        buttonPad.Paint += ButtonPad_Paint;
        //
        // statusLabel
        //
        statusLabel.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
        statusLabel.BorderStyle = BorderStyle.FixedSingle;
        statusLabel.Location = new Point(24, 588);
        statusLabel.Name = "statusLabel";
        statusLabel.Size = new Size(624, 42);
        statusLabel.TabIndex = 15;
        statusLabel.Text = "Disconnected";
        statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        //
        // videoGroupBox
        //
        videoGroupBox.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        videoGroupBox.Controls.Add(videoPictureBox);
        videoGroupBox.Controls.Add(videoControlsPanel);
        videoGroupBox.Location = new Point(672, 42);
        videoGroupBox.Name = "videoGroupBox";
        videoGroupBox.Size = new Size(552, 588);
        videoGroupBox.TabIndex = 16;
        videoGroupBox.TabStop = false;
        videoGroupBox.Text = "Capture Preview";
        //
        // videoPictureBox
        //
        videoPictureBox.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        videoPictureBox.BackColor = Color.Black;
        videoPictureBox.Location = new Point(14, 24);
        videoPictureBox.Name = "videoPictureBox";
        videoPictureBox.Size = new Size(524, 422);
        videoPictureBox.SizeMode = PictureBoxSizeMode.Zoom;
        videoPictureBox.TabIndex = 0;
        videoPictureBox.TabStop = false;
        //
        // videoControlsPanel
        //
        videoControlsPanel.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
        videoControlsPanel.Controls.Add(videoDeviceLabel);
        videoControlsPanel.Controls.Add(videoDeviceComboBox);
        videoControlsPanel.Controls.Add(refreshVideoButton);
        videoControlsPanel.Controls.Add(audioDeviceLabel);
        videoControlsPanel.Controls.Add(audioDeviceComboBox);
        videoControlsPanel.Controls.Add(startAudioButton);
        videoControlsPanel.Controls.Add(stopAudioButton);
        videoControlsPanel.Controls.Add(audioVolumeLabel);
        videoControlsPanel.Controls.Add(audioVolumeTrackBar);
        videoControlsPanel.Controls.Add(audioVolumeValueLabel);
        videoControlsPanel.Controls.Add(videoWidthLabel);
        videoControlsPanel.Controls.Add(videoWidthNumeric);
        videoControlsPanel.Controls.Add(videoHeightLabel);
        videoControlsPanel.Controls.Add(videoHeightNumeric);
        videoControlsPanel.Controls.Add(videoFpsLabel);
        videoControlsPanel.Controls.Add(videoFpsNumeric);
        videoControlsPanel.Controls.Add(startVideoButton);
        videoControlsPanel.Controls.Add(stopVideoButton);
        videoControlsPanel.Controls.Add(fullScreenVideoButton);
        videoControlsPanel.Location = new Point(14, 454);
        videoControlsPanel.Name = "videoControlsPanel";
        videoControlsPanel.Size = new Size(524, 122);
        videoControlsPanel.TabIndex = 1;
        //
        // videoDeviceLabel
        //
        videoDeviceLabel.AutoSize = true;
        videoDeviceLabel.Location = new Point(3, 7);
        videoDeviceLabel.Margin = new Padding(3, 7, 3, 0);
        videoDeviceLabel.Name = "videoDeviceLabel";
        videoDeviceLabel.Size = new Size(42, 15);
        videoDeviceLabel.TabIndex = 0;
        videoDeviceLabel.Text = "Device";
        //
        // videoDeviceComboBox
        //
        videoDeviceComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
        videoDeviceComboBox.FormattingEnabled = true;
        videoDeviceComboBox.Location = new Point(51, 3);
        videoDeviceComboBox.Name = "videoDeviceComboBox";
        videoDeviceComboBox.Size = new Size(200, 23);
        videoDeviceComboBox.TabIndex = 1;
        //
        // refreshVideoButton
        //
        refreshVideoButton.Location = new Point(257, 3);
        refreshVideoButton.Name = "refreshVideoButton";
        refreshVideoButton.Size = new Size(68, 25);
        refreshVideoButton.TabIndex = 2;
        refreshVideoButton.Text = "Refresh";
        refreshVideoButton.UseVisualStyleBackColor = true;
        refreshVideoButton.Click += RefreshVideoButton_Click;
        //
        // audioDeviceLabel
        //
        audioDeviceLabel.AutoSize = true;
        audioDeviceLabel.Location = new Point(331, 7);
        audioDeviceLabel.Margin = new Padding(3, 7, 3, 0);
        audioDeviceLabel.Name = "audioDeviceLabel";
        audioDeviceLabel.Size = new Size(39, 15);
        audioDeviceLabel.TabIndex = 3;
        audioDeviceLabel.Text = "Audio";
        //
        // audioDeviceComboBox
        //
        audioDeviceComboBox.DropDownStyle = ComboBoxStyle.DropDownList;
        audioDeviceComboBox.FormattingEnabled = true;
        audioDeviceComboBox.Location = new Point(3, 35);
        audioDeviceComboBox.Name = "audioDeviceComboBox";
        audioDeviceComboBox.Size = new Size(200, 23);
        audioDeviceComboBox.TabIndex = 4;
        //
        // startAudioButton
        //
        startAudioButton.Location = new Point(209, 35);
        startAudioButton.Name = "startAudioButton";
        startAudioButton.Size = new Size(88, 25);
        startAudioButton.TabIndex = 5;
        startAudioButton.Text = "Start Audio";
        startAudioButton.UseVisualStyleBackColor = true;
        startAudioButton.Click += StartAudioButton_Click;
        //
        // stopAudioButton
        //
        stopAudioButton.Enabled = false;
        stopAudioButton.Location = new Point(303, 35);
        stopAudioButton.Name = "stopAudioButton";
        stopAudioButton.Size = new Size(88, 25);
        stopAudioButton.TabIndex = 6;
        stopAudioButton.Text = "Stop Audio";
        stopAudioButton.UseVisualStyleBackColor = true;
        stopAudioButton.Click += StopAudioButton_Click;
        //
        // audioVolumeLabel
        //
        audioVolumeLabel.AutoSize = true;
        audioVolumeLabel.Location = new Point(397, 39);
        audioVolumeLabel.Margin = new Padding(3, 7, 3, 0);
        audioVolumeLabel.Name = "audioVolumeLabel";
        audioVolumeLabel.Size = new Size(47, 15);
        audioVolumeLabel.TabIndex = 7;
        audioVolumeLabel.Text = "Volume";
        //
        // audioVolumeTrackBar
        //
        audioVolumeTrackBar.AutoSize = false;
        audioVolumeTrackBar.Location = new Point(3, 64);
        audioVolumeTrackBar.Maximum = 100;
        audioVolumeTrackBar.Name = "audioVolumeTrackBar";
        audioVolumeTrackBar.Size = new Size(120, 25);
        audioVolumeTrackBar.TabIndex = 8;
        audioVolumeTrackBar.TickFrequency = 25;
        audioVolumeTrackBar.Value = 80;
        audioVolumeTrackBar.ValueChanged += AudioVolumeTrackBar_ValueChanged;
        //
        // audioVolumeValueLabel
        //
        audioVolumeValueLabel.AutoSize = true;
        audioVolumeValueLabel.Location = new Point(129, 71);
        audioVolumeValueLabel.Margin = new Padding(3, 7, 3, 0);
        audioVolumeValueLabel.Name = "audioVolumeValueLabel";
        audioVolumeValueLabel.Size = new Size(31, 15);
        audioVolumeValueLabel.TabIndex = 9;
        audioVolumeValueLabel.Text = "80%";
        //
        // videoWidthLabel
        //
        videoWidthLabel.AutoSize = true;
        videoWidthLabel.Location = new Point(166, 71);
        videoWidthLabel.Margin = new Padding(3, 7, 3, 0);
        videoWidthLabel.Name = "videoWidthLabel";
        videoWidthLabel.Size = new Size(39, 15);
        videoWidthLabel.TabIndex = 10;
        videoWidthLabel.Text = "Width";
        //
        // videoWidthNumeric
        //
        videoWidthNumeric.Increment = new decimal(new int[] { 160, 0, 0, 0 });
        videoWidthNumeric.Location = new Point(211, 67);
        videoWidthNumeric.Maximum = new decimal(new int[] { 3840, 0, 0, 0 });
        videoWidthNumeric.Minimum = new decimal(new int[] { 320, 0, 0, 0 });
        videoWidthNumeric.Name = "videoWidthNumeric";
        videoWidthNumeric.Size = new Size(66, 23);
        videoWidthNumeric.TabIndex = 11;
        videoWidthNumeric.Value = new decimal(new int[] { 1280, 0, 0, 0 });
        //
        // videoHeightLabel
        //
        videoHeightLabel.AutoSize = true;
        videoHeightLabel.Location = new Point(283, 71);
        videoHeightLabel.Margin = new Padding(3, 7, 3, 0);
        videoHeightLabel.Name = "videoHeightLabel";
        videoHeightLabel.Size = new Size(43, 15);
        videoHeightLabel.TabIndex = 12;
        videoHeightLabel.Text = "Height";
        //
        // videoHeightNumeric
        //
        videoHeightNumeric.Increment = new decimal(new int[] { 120, 0, 0, 0 });
        videoHeightNumeric.Location = new Point(332, 67);
        videoHeightNumeric.Maximum = new decimal(new int[] { 2160, 0, 0, 0 });
        videoHeightNumeric.Minimum = new decimal(new int[] { 240, 0, 0, 0 });
        videoHeightNumeric.Name = "videoHeightNumeric";
        videoHeightNumeric.Size = new Size(66, 23);
        videoHeightNumeric.TabIndex = 13;
        videoHeightNumeric.Value = new decimal(new int[] { 720, 0, 0, 0 });
        //
        // videoFpsLabel
        //
        videoFpsLabel.AutoSize = true;
        videoFpsLabel.Location = new Point(404, 71);
        videoFpsLabel.Margin = new Padding(3, 7, 3, 0);
        videoFpsLabel.Name = "videoFpsLabel";
        videoFpsLabel.Size = new Size(24, 15);
        videoFpsLabel.TabIndex = 14;
        videoFpsLabel.Text = "FPS";
        //
        // videoFpsNumeric
        //
        videoFpsNumeric.Location = new Point(3, 95);
        videoFpsNumeric.Maximum = new decimal(new int[] { 120, 0, 0, 0 });
        videoFpsNumeric.Minimum = new decimal(new int[] { 15, 0, 0, 0 });
        videoFpsNumeric.Name = "videoFpsNumeric";
        videoFpsNumeric.Size = new Size(48, 23);
        videoFpsNumeric.TabIndex = 15;
        videoFpsNumeric.Value = new decimal(new int[] { 60, 0, 0, 0 });
        //
        // startVideoButton
        //
        startVideoButton.Location = new Point(57, 95);
        startVideoButton.Name = "startVideoButton";
        startVideoButton.Size = new Size(96, 25);
        startVideoButton.TabIndex = 16;
        startVideoButton.Text = "Start Video";
        startVideoButton.UseVisualStyleBackColor = true;
        startVideoButton.Click += StartVideoButton_Click;
        //
        // stopVideoButton
        //
        stopVideoButton.Enabled = false;
        stopVideoButton.Location = new Point(159, 95);
        stopVideoButton.Name = "stopVideoButton";
        stopVideoButton.Size = new Size(96, 25);
        stopVideoButton.TabIndex = 17;
        stopVideoButton.Text = "Stop Video";
        stopVideoButton.UseVisualStyleBackColor = true;
        stopVideoButton.Click += StopVideoButton_Click;
        //
        // fullScreenVideoButton
        //
        fullScreenVideoButton.Location = new Point(261, 95);
        fullScreenVideoButton.Name = "fullScreenVideoButton";
        fullScreenVideoButton.Size = new Size(96, 25);
        fullScreenVideoButton.TabIndex = 18;
        fullScreenVideoButton.Text = "Full Screen";
        fullScreenVideoButton.UseVisualStyleBackColor = true;
        fullScreenVideoButton.Click += FullScreenVideoButton_Click;
        //
        // Form1
        //
        AutoScaleDimensions = new SizeF(7F, 15F);
        AutoScaleMode = AutoScaleMode.Font;
        ClientSize = new Size(1248, 650);
        Controls.Add(videoGroupBox);
        Controls.Add(statusLabel);
        Controls.Add(buttonPad);
        Controls.Add(n64ClearDebugButton);
        Controls.Add(n64StatButton);
        Controls.Add(forceOffButton);
        Controls.Add(forceHighButton);
        Controls.Add(forceLowButton);
        Controls.Add(clearButton);
        Controls.Add(connectButton);
        Controls.Add(refreshButton);
        Controls.Add(portComboBox);
        Controls.Add(portLabel);
        Controls.Add(consoleComboBox);
        Controls.Add(consoleLabel);
        Controls.Add(titleLabel);
        Controls.Add(menuStrip);
        MainMenuStrip = menuStrip;
        MinimumSize = new Size(1060, 680);
        Name = "Form1";
        StartPosition = FormStartPosition.CenterScreen;
        Text = "PicoSwitchController v1.9.0";
        videoGroupBox.ResumeLayout(false);
        ((System.ComponentModel.ISupportInitialize)videoPictureBox).EndInit();
        videoControlsPanel.ResumeLayout(false);
        videoControlsPanel.PerformLayout();
        ((System.ComponentModel.ISupportInitialize)videoWidthNumeric).EndInit();
        ((System.ComponentModel.ISupportInitialize)videoHeightNumeric).EndInit();
        ((System.ComponentModel.ISupportInitialize)videoFpsNumeric).EndInit();
        ((System.ComponentModel.ISupportInitialize)audioVolumeTrackBar).EndInit();
        menuStrip.ResumeLayout(false);
        menuStrip.PerformLayout();
        ResumeLayout(false);
        PerformLayout();
    }

    #endregion

    private MenuStrip menuStrip;
    private ToolStripMenuItem toolsMenuItem;
    private ToolStripMenuItem configureControllerMenuItem;
    private ToolStripMenuItem videoMenuItem;
    private ToolStripMenuItem startVideoMenuItem;
    private ToolStripMenuItem stopVideoMenuItem;
    private ToolStripMenuItem fullScreenVideoMenuItem;
    private ToolStripMenuItem openExternalViewerMenuItem;
    private Label titleLabel;
    private Label consoleLabel;
    private ComboBox consoleComboBox;
    private Label portLabel;
    private ComboBox portComboBox;
    private Button refreshButton;
    private Button connectButton;
    private Button clearButton;
    private Button forceLowButton;
    private Button forceHighButton;
    private Button forceOffButton;
    private Button n64StatButton;
    private Button n64ClearDebugButton;
    private Panel buttonPad;
    private Label statusLabel;
    private GroupBox videoGroupBox;
    private PictureBox videoPictureBox;
    private FlowLayoutPanel videoControlsPanel;
    private Label videoDeviceLabel;
    private ComboBox videoDeviceComboBox;
    private Button refreshVideoButton;
    private Label audioDeviceLabel;
    private ComboBox audioDeviceComboBox;
    private Button startAudioButton;
    private Button stopAudioButton;
    private Label audioVolumeLabel;
    private TrackBar audioVolumeTrackBar;
    private Label audioVolumeValueLabel;
    private Label videoWidthLabel;
    private NumericUpDown videoWidthNumeric;
    private Label videoHeightLabel;
    private NumericUpDown videoHeightNumeric;
    private Label videoFpsLabel;
    private NumericUpDown videoFpsNumeric;
    private Button startVideoButton;
    private Button stopVideoButton;
    private Button fullScreenVideoButton;
}
