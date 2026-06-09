Imports System.Diagnostics
Imports System.Drawing
Imports System.IO
Imports System.Collections.Generic
Imports System.ComponentModel
Imports System.Globalization
Imports System.Linq
Imports System.Text
Imports System.Threading
Imports System.Threading.Tasks

Public Class DeckLinkPlayerControl
    Inherits UserControl

    Private Const LoadingNodeText As String = "Loading..."
    Private Const NoDeckLinkOutputText As String = "None"
    Private Const DefaultDeckLinkOutputDeviceName As String = "DeckLink SDI 4K"
    Private Const DurationDisplayFrameRate As Integer = 25

    Private Shared ReadOnly Property DeckLinkPlayerOutputSettingsFilePath As String
        Get
            Return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FfmpegRecorder", "decklink-player-output.txt")
        End Get
    End Property

    Private NotInheritable Class DeckLinkOutputMode
        Public Sub New(displayName As String, formatCode As String, width As Integer, height As Integer, frameRate As String, isInterlaced As Boolean)
            Me.DisplayName = displayName
            Me.FormatCode = formatCode
            Me.Width = width
            Me.Height = height
            Me.FrameRate = frameRate
            Me.IsInterlaced = isInterlaced
        End Sub

        Public ReadOnly Property DisplayName As String
        Public ReadOnly Property FormatCode As String
        Public ReadOnly Property Width As Integer
        Public ReadOnly Property Height As Integer
        Public ReadOnly Property FrameRate As String
        Public ReadOnly Property IsInterlaced As Boolean

        Public Overrides Function ToString() As String
            Return DisplayName
        End Function
    End Class

    Private Shared ReadOnly OutputModes As New List(Of DeckLinkOutputMode) From {
        New DeckLinkOutputMode("1080i50", "Hi50", 1920, 1080, "25", True),
        New DeckLinkOutputMode("1080p25", "Hp25", 1920, 1080, "25", False),
        New DeckLinkOutputMode("1080p50", "Hp50", 1920, 1080, "50", False),
        New DeckLinkOutputMode("PAL 625i50", "pal", 720, 576, "25", True)
    }

    Private Shared ReadOnly MediaExtensions As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase) From {
        ".mxf",
        ".mp4",
        ".mov",
        ".avi",
        ".mkv",
        ".mts",
        ".m2ts",
        ".ts",
        ".mpg",
        ".mpeg",
        ".wav",
        ".jpg",
        ".jpeg",
        ".png",
        ".bmp"
    }

    Private Shared ReadOnly ImageExtensions As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase) From {
        ".jpg",
        ".jpeg",
        ".png",
        ".bmp"
    }

    Private ReadOnly rootLayout As New TableLayoutPanel()
    Private ReadOnly toolbarPanel As New TableLayoutPanel()
    Private ReadOnly folderLabel As New Label()
    Private ReadOnly rootPathTextBox As New TextBox()
    Private ReadOnly refreshButton As New Button()
    Private ReadOnly openFolderButton As New Button()
    Private ReadOnly outputPanel As New TableLayoutPanel()
    Private ReadOnly outputDeviceLabel As New Label()
    Private ReadOnly outputDeviceComboBox As New ComboBox()
    Private ReadOnly outputModeLabel As New Label()
    Private ReadOnly outputModeComboBox As New ComboBox()
    Private ReadOnly browserSplit As New SplitContainer()
    Private ReadOnly folderTreeView As New TreeView()
    Private ReadOnly filesGridView As New DataGridView()
    Private ReadOnly previewPanel As New TableLayoutPanel()
    Private ReadOnly previewToolbarPanel As New FlowLayoutPanel()
    Private ReadOnly previewButton As New Button()
    Private ReadOnly stopPreviewButton As New Button()
    Private ReadOnly selectedFileLabel As New Label()
    Private ReadOnly previewStateHostPanel As New Panel()
    Private ReadOnly speedButtonsPanel As New FlowLayoutPanel()
    Private ReadOnly speedSeekPanel As New TableLayoutPanel()
    Private ReadOnly speedTrackBar As New TrackBar()
    Private ReadOnly speedValueLabel As New Label()
    Private ReadOnly previewPictureBox As New PictureBox()
    Private ReadOnly previewStateLabel As New Label()
    Private ReadOnly scrubberPanel As New TableLayoutPanel()
    Private ReadOnly scrubberTrackBar As New TrackBar()
    Private ReadOnly scrubberTimeLabel As New Label()
    Private ReadOnly playbackPositionTimer As New System.Windows.Forms.Timer()
    Private ReadOnly scrubPreviewTimer As New System.Windows.Forms.Timer()
    Private ReadOnly shuttlePlaybackTimer As New System.Windows.Forms.Timer()
    Private ReadOnly statusLabel As New Label()
    Private ReadOnly speedPresetButtons As New List(Of Button)()

    Private WithEvents previewRunner As PreviewFrameReader
    Private WithEvents outputRunner As InProcessDeckLinkOutputRunner
    Private WithEvents audioMonitorRunner As FfmpegProcessRunner
    Private darkModeEnabledValue As Boolean = True
    Private rootDirectoryPath As String
    Private selectedFilePath As String
    Private isLoadingFiles As Boolean
    Private isStoppingPreview As Boolean
    Private isStoppingOutput As Boolean
    Private isSeekingPlayback As Boolean
    Private isScrubberDragging As Boolean
    Private isScrubFrameRenderRunning As Boolean
    Private speakerMonitorEnabledValue As Boolean
    Private durationProbeGeneration As Integer
    Private hasAppliedInitialBrowserSplit As Boolean
    Private outputRunnerIsScrubHold As Boolean
    Private scrubDeckLinkOutputKey As String
    Private lastDeckLinkOutputMessage As String
    Private scrubberLoadedFilePath As String
    Private playbackStartOffset As TimeSpan = TimeSpan.Zero
    Private playbackClockOffset As TimeSpan = TimeSpan.Zero
    Private playbackClockStartedAtUtc As DateTime
    Private playbackSpeedMultiplier As Double = 1.0R
    Private shuttlePlaybackActive As Boolean
    Private shuttleClockOffset As TimeSpan = TimeSpan.Zero
    Private shuttleClockStartedAtUtc As DateTime
    Private isUpdatingSpeedControls As Boolean
    Private pendingScrubFrameOffset As TimeSpan?
    Private pendingScrubFrameShouldUpdateDeckLink As Boolean
    Private scrubFrameRequestGeneration As Integer
    Private currentScrubFrameCancellation As CancellationTokenSource
    Private playbackFirstPreviewFrameSource As TaskCompletionSource(Of Boolean)
    Private holdPreviewFrameUntilUtc As DateTime
    Private ReadOnly durationByPath As New Dictionary(Of String, TimeSpan)(StringComparer.OrdinalIgnoreCase)

    Public Sub New()
        Dock = DockStyle.Fill
        Margin = New Padding(0)
        AutoScroll = False
        BuildLayout()
        ApplyTheme()

        AddHandler Load, AddressOf OnControlLoaded
        AddHandler refreshButton.Click, AddressOf OnRefreshClicked
        AddHandler openFolderButton.Click, AddressOf OnOpenFolderClicked
        AddHandler folderTreeView.MouseDown, AddressOf OnFolderTreeMouseDown
        AddHandler folderTreeView.BeforeExpand, AddressOf OnFolderTreeBeforeExpand
        AddHandler folderTreeView.AfterSelect, AddressOf OnFolderTreeAfterSelect
        AddHandler filesGridView.SelectionChanged, AddressOf OnFilesGridSelectionChanged
        AddHandler filesGridView.CellDoubleClick, AddressOf OnFilesGridCellDoubleClick
        AddHandler filesGridView.KeyDown, AddressOf OnFilesGridKeyDown
        AddHandler previewButton.Click, AddressOf OnPreviewClicked
        AddHandler stopPreviewButton.Click, AddressOf OnStopPreviewClicked
        AddHandler speedTrackBar.ValueChanged, AddressOf OnSpeedTrackBarValueChanged
        AddHandler outputDeviceComboBox.SelectedIndexChanged, AddressOf OnOutputSelectionChanged
        AddHandler outputModeComboBox.SelectedIndexChanged, AddressOf OnOutputSelectionChanged
        AddHandler scrubberTrackBar.MouseDown, AddressOf OnScrubberMouseDown
        AddHandler scrubberTrackBar.MouseMove, AddressOf OnScrubberMouseMove
        AddHandler scrubberTrackBar.MouseUp, AddressOf OnScrubberMouseUp
        AddHandler scrubberTrackBar.Scroll, AddressOf OnScrubberScrolled
        AddHandler scrubberTrackBar.KeyUp, AddressOf OnScrubberKeyUp
        AddHandler playbackPositionTimer.Tick, AddressOf OnPlaybackPositionTimerTick
        scrubPreviewTimer.Interval = 140
        AddHandler scrubPreviewTimer.Tick, AddressOf OnScrubPreviewTimerTick
        shuttlePlaybackTimer.Interval = 80
        AddHandler shuttlePlaybackTimer.Tick, AddressOf OnShuttlePlaybackTimerTick
    End Sub

    <Browsable(False)>
    <DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)>
    Public Property DarkModeEnabled As Boolean
        Get
            Return darkModeEnabledValue
        End Get
        Set(value As Boolean)
            If darkModeEnabledValue = value Then
                Return
            End If

            darkModeEnabledValue = value
            ApplyTheme()
        End Set
    End Property

    <Browsable(False)>
    <DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)>
    Public Property SpeakerMonitorEnabled As Boolean
        Get
            Return speakerMonitorEnabledValue
        End Get
        Set(value As Boolean)
            If speakerMonitorEnabledValue = value Then
                Return
            End If

            speakerMonitorEnabledValue = value

            If Not speakerMonitorEnabledValue Then
                TearDownAudioMonitor(fast:=True)
            ElseIf previewRunner IsNot Nothing Then
                StartAudioMonitor(selectedFilePath, playbackStartOffset, playbackSpeedMultiplier)
            End If
        End Set
    End Property

    Public Sub RefreshFromRecordingDirectory()
        Dim recordingDirectory = RecordingDirectorySettings.GetRecordingDirectory()
        Directory.CreateDirectory(recordingDirectory)

        If String.Equals(rootDirectoryPath, recordingDirectory, StringComparison.OrdinalIgnoreCase) AndAlso folderTreeView.Nodes.Count > 0 Then
            RefreshSelectedFolderFiles()
            Return
        End If

        rootDirectoryPath = recordingDirectory
        rootPathTextBox.Text = rootDirectoryPath
        LoadFolderTree(rootDirectoryPath)
    End Sub

    Private Sub BuildLayout()
        rootLayout.ColumnCount = 1
        rootLayout.ColumnStyles.Add(New ColumnStyle(SizeType.Absolute, 744.0F))
        rootLayout.Dock = DockStyle.None
        rootLayout.Location = New Point(0, 0)
        rootLayout.Margin = New Padding(0)
        rootLayout.Padding = New Padding(8)
        rootLayout.RowCount = 5
        rootLayout.RowStyles.Add(New RowStyle(SizeType.Absolute, 32.0F))
        rootLayout.RowStyles.Add(New RowStyle(SizeType.Absolute, 36.0F))
        rootLayout.RowStyles.Add(New RowStyle(SizeType.Absolute, 226.0F))
        rootLayout.RowStyles.Add(New RowStyle(SizeType.Absolute, 558.0F))
        rootLayout.RowStyles.Add(New RowStyle(SizeType.Absolute, 24.0F))
        rootLayout.Size = New Size(760, 892)

        toolbarPanel.ColumnCount = 4
        toolbarPanel.ColumnStyles.Add(New ColumnStyle(SizeType.AutoSize))
        toolbarPanel.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100.0F))
        toolbarPanel.ColumnStyles.Add(New ColumnStyle(SizeType.Absolute, 74.0F))
        toolbarPanel.ColumnStyles.Add(New ColumnStyle(SizeType.Absolute, 84.0F))
        toolbarPanel.Dock = DockStyle.Fill
        toolbarPanel.Margin = New Padding(0, 0, 0, 6)
        toolbarPanel.RowCount = 1
        toolbarPanel.RowStyles.Add(New RowStyle(SizeType.Percent, 100.0F))

        folderLabel.AutoSize = True
        folderLabel.Dock = DockStyle.Fill
        folderLabel.Margin = New Padding(0, 4, 8, 0)
        folderLabel.Text = "Recording Folder"
        folderLabel.TextAlign = ContentAlignment.MiddleLeft

        rootPathTextBox.Dock = DockStyle.Fill
        rootPathTextBox.Margin = New Padding(0)
        rootPathTextBox.ReadOnly = True

        refreshButton.Dock = DockStyle.Fill
        refreshButton.Margin = New Padding(8, 0, 0, 0)
        refreshButton.Text = "Refresh"
        refreshButton.UseVisualStyleBackColor = True

        openFolderButton.Dock = DockStyle.Fill
        openFolderButton.Margin = New Padding(8, 0, 0, 0)
        openFolderButton.Text = "Open"
        openFolderButton.UseVisualStyleBackColor = True

        toolbarPanel.Controls.Add(folderLabel, 0, 0)
        toolbarPanel.Controls.Add(rootPathTextBox, 1, 0)
        toolbarPanel.Controls.Add(refreshButton, 2, 0)
        toolbarPanel.Controls.Add(openFolderButton, 3, 0)

        outputPanel.ColumnCount = 5
        outputPanel.ColumnStyles.Add(New ColumnStyle(SizeType.Absolute, 58.0F))
        outputPanel.ColumnStyles.Add(New ColumnStyle(SizeType.Absolute, 280.0F))
        outputPanel.ColumnStyles.Add(New ColumnStyle(SizeType.Absolute, 42.0F))
        outputPanel.ColumnStyles.Add(New ColumnStyle(SizeType.Absolute, 104.0F))
        outputPanel.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100.0F))
        outputPanel.Dock = DockStyle.Fill
        outputPanel.Margin = New Padding(0, 0, 0, 6)
        outputPanel.RowCount = 1
        outputPanel.RowStyles.Add(New RowStyle(SizeType.Percent, 100.0F))

        outputDeviceLabel.AutoSize = True
        outputDeviceLabel.Dock = DockStyle.Fill
        outputDeviceLabel.Margin = New Padding(0, 4, 8, 0)
        outputDeviceLabel.Text = "SDI Out"
        outputDeviceLabel.TextAlign = ContentAlignment.MiddleLeft

        outputDeviceComboBox.Dock = DockStyle.Fill
        outputDeviceComboBox.DropDownStyle = ComboBoxStyle.DropDownList
        outputDeviceComboBox.Margin = New Padding(0, 0, 8, 0)

        outputModeLabel.AutoSize = True
        outputModeLabel.Dock = DockStyle.Fill
        outputModeLabel.Margin = New Padding(0, 4, 8, 0)
        outputModeLabel.Text = "Mode"
        outputModeLabel.TextAlign = ContentAlignment.MiddleLeft

        outputModeComboBox.Dock = DockStyle.Fill
        outputModeComboBox.DropDownStyle = ComboBoxStyle.DropDownList
        outputModeComboBox.Margin = New Padding(0, 0, 8, 0)
        outputModeComboBox.Items.AddRange(OutputModes.Cast(Of Object)().ToArray())
        Dim savedOutputModeName = GetSavedDeckLinkOutputModeName()
        Dim savedOutputMode = OutputModes.FirstOrDefault(Function(outputMode) String.Equals(outputMode.DisplayName, savedOutputModeName, StringComparison.OrdinalIgnoreCase))
        outputModeComboBox.SelectedItem = If(savedOutputMode, OutputModes(0))

        outputPanel.Controls.Add(outputDeviceLabel, 0, 0)
        outputPanel.Controls.Add(outputDeviceComboBox, 1, 0)
        outputPanel.Controls.Add(outputModeLabel, 2, 0)
        outputPanel.Controls.Add(outputModeComboBox, 3, 0)

        browserSplit.Dock = DockStyle.Fill
        browserSplit.Margin = New Padding(0, 0, 0, 8)
        browserSplit.Orientation = Orientation.Vertical
        browserSplit.Size = New Size(744, 226)
        browserSplit.Panel1MinSize = 140
        browserSplit.Panel2MinSize = 180
        browserSplit.SplitterDistance = 185
        browserSplit.SplitterWidth = 6

        folderTreeView.Dock = DockStyle.Fill
        folderTreeView.HideSelection = False
        folderTreeView.Margin = New Padding(0)
        folderTreeView.ShowNodeToolTips = True

        filesGridView.AllowUserToAddRows = False
        filesGridView.AllowUserToDeleteRows = False
        filesGridView.AllowUserToResizeRows = False
        filesGridView.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
        filesGridView.BackgroundColor = Color.FromArgb(28, 31, 36)
        filesGridView.BorderStyle = BorderStyle.FixedSingle
        filesGridView.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize
        filesGridView.Dock = DockStyle.Fill
        filesGridView.Margin = New Padding(0)
        filesGridView.MultiSelect = False
        filesGridView.ReadOnly = True
        filesGridView.RowHeadersVisible = False
        filesGridView.SelectionMode = DataGridViewSelectionMode.FullRowSelect

        filesGridView.Columns.Add(New DataGridViewTextBoxColumn() With {.Name = "Name", .HeaderText = "File", .FillWeight = 180.0F})
        filesGridView.Columns.Add(New DataGridViewTextBoxColumn() With {.Name = "Duration", .HeaderText = "Duration", .FillWeight = 64.0F})
        filesGridView.Columns.Add(New DataGridViewTextBoxColumn() With {.Name = "Size", .HeaderText = "Size", .FillWeight = 58.0F})
        filesGridView.Columns.Add(New DataGridViewTextBoxColumn() With {.Name = "FullPath", .HeaderText = "FullPath", .Visible = False})

        browserSplit.Panel1.Controls.Add(folderTreeView)
        browserSplit.Panel2.Controls.Add(filesGridView)

        previewPanel.ColumnCount = 1
        previewPanel.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100.0F))
        previewPanel.Dock = DockStyle.Fill
        previewPanel.Margin = New Padding(0)
        previewPanel.RowCount = 5
        previewPanel.RowStyles.Add(New RowStyle(SizeType.Percent, 100.0F))
        previewPanel.RowStyles.Add(New RowStyle(SizeType.Absolute, 42.0F))
        previewPanel.RowStyles.Add(New RowStyle(SizeType.Absolute, 36.0F))
        previewPanel.RowStyles.Add(New RowStyle(SizeType.Absolute, 64.0F))
        previewPanel.RowStyles.Add(New RowStyle(SizeType.Absolute, 44.0F))

        previewToolbarPanel.Dock = DockStyle.Fill
        previewToolbarPanel.FlowDirection = FlowDirection.LeftToRight
        previewToolbarPanel.Margin = New Padding(0, 4, 0, 0)
        previewToolbarPanel.WrapContents = False

        previewButton.Size = New Size(76, 28)
        previewButton.Margin = New Padding(0, 0, 6, 0)
        previewButton.Text = "Play"
        previewButton.UseVisualStyleBackColor = True

        stopPreviewButton.Enabled = False
        stopPreviewButton.Size = New Size(76, 28)
        stopPreviewButton.Margin = New Padding(0, 0, 10, 0)
        stopPreviewButton.Text = "Stop"
        stopPreviewButton.UseVisualStyleBackColor = True

        selectedFileLabel.AutoEllipsis = True
        selectedFileLabel.AutoSize = False
        selectedFileLabel.Margin = New Padding(0, 6, 0, 0)
        selectedFileLabel.Size = New Size(380, 20)
        selectedFileLabel.Text = "Select a file in the grid, then Play or double-click."
        selectedFileLabel.TextAlign = ContentAlignment.MiddleLeft

        previewStateHostPanel.Margin = New Padding(0, 0, 10, 0)
        previewStateHostPanel.Size = New Size(150, 28)

        previewStateLabel.AutoEllipsis = True
        previewStateLabel.AutoSize = False
        previewStateLabel.BackColor = Color.Transparent
        previewStateLabel.Dock = DockStyle.Fill
        previewStateLabel.Margin = New Padding(0)
        previewStateLabel.Text = "Preview stopped"
        previewStateLabel.TextAlign = ContentAlignment.MiddleLeft
        previewStateHostPanel.Controls.Add(previewStateLabel)

        previewToolbarPanel.Controls.Add(previewButton)
        previewToolbarPanel.Controls.Add(stopPreviewButton)
        previewToolbarPanel.Controls.Add(previewStateHostPanel)
        previewToolbarPanel.Controls.Add(selectedFileLabel)

        speedButtonsPanel.Dock = DockStyle.Fill
        speedButtonsPanel.FlowDirection = FlowDirection.LeftToRight
        speedButtonsPanel.Margin = New Padding(0, 2, 0, 0)
        speedButtonsPanel.WrapContents = True
        For Each speed In New Double() {-20.0R, -10.0R, -5.0R, -2.0R, -1.5R, -1.0R, -0.5R, 0.0R, 0.5R, 1.0R, 1.5R, 2.0R, 5.0R, 10.0R, 20.0R}
            AddSpeedPresetButton(speed)
        Next

        speedSeekPanel.ColumnCount = 2
        speedSeekPanel.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100.0F))
        speedSeekPanel.ColumnStyles.Add(New ColumnStyle(SizeType.Absolute, 72.0F))
        speedSeekPanel.Dock = DockStyle.Fill
        speedSeekPanel.Margin = New Padding(0, 0, 0, 0)
        speedSeekPanel.RowCount = 1
        speedSeekPanel.RowStyles.Add(New RowStyle(SizeType.Percent, 100.0F))

        speedTrackBar.AutoSize = False
        speedTrackBar.Dock = DockStyle.Fill
        speedTrackBar.Height = 34
        speedTrackBar.LargeChange = 10
        speedTrackBar.Margin = New Padding(0, 3, 8, 0)
        speedTrackBar.Maximum = 200
        speedTrackBar.Minimum = -200
        speedTrackBar.SmallChange = 5
        speedTrackBar.TickFrequency = 50
        speedTrackBar.Value = 10

        speedValueLabel.AutoEllipsis = True
        speedValueLabel.AutoSize = False
        speedValueLabel.Dock = DockStyle.Fill
        speedValueLabel.Margin = New Padding(0, 9, 0, 0)
        speedValueLabel.TextAlign = ContentAlignment.TopRight

        speedSeekPanel.Controls.Add(speedTrackBar, 0, 0)
        speedSeekPanel.Controls.Add(speedValueLabel, 1, 0)

        previewPictureBox.BackColor = Color.Black
        previewPictureBox.BorderStyle = BorderStyle.FixedSingle
        previewPictureBox.Dock = DockStyle.Fill
        previewPictureBox.Margin = New Padding(0)
        previewPictureBox.SizeMode = PictureBoxSizeMode.Zoom

        Dim previewSurface As New Panel() With {
            .Dock = DockStyle.Fill,
            .Margin = New Padding(0)
        }
        previewSurface.Controls.Add(previewPictureBox)

        scrubberPanel.ColumnCount = 2
        scrubberPanel.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100.0F))
        scrubberPanel.ColumnStyles.Add(New ColumnStyle(SizeType.Absolute, 180.0F))
        scrubberPanel.Dock = DockStyle.Fill
        scrubberPanel.Margin = New Padding(0, 6, 0, 0)
        scrubberPanel.RowCount = 1
        scrubberPanel.RowStyles.Add(New RowStyle(SizeType.Percent, 100.0F))

        scrubberTrackBar.AutoSize = False
        scrubberTrackBar.Dock = DockStyle.Fill
        scrubberTrackBar.Enabled = False
        scrubberTrackBar.LargeChange = DurationDisplayFrameRate * 10
        scrubberTrackBar.Margin = New Padding(0, 3, 8, 0)
        scrubberTrackBar.Maximum = 0
        scrubberTrackBar.Minimum = 0
        scrubberTrackBar.SmallChange = DurationDisplayFrameRate
        scrubberTrackBar.TickStyle = TickStyle.None

        scrubberTimeLabel.AutoEllipsis = True
        scrubberTimeLabel.AutoSize = False
        scrubberTimeLabel.Dock = DockStyle.Fill
        scrubberTimeLabel.Margin = New Padding(0, 8, 0, 0)
        scrubberTimeLabel.Text = "--:--:--:-- / --:--:--:--"
        scrubberTimeLabel.TextAlign = ContentAlignment.TopRight

        scrubberPanel.Controls.Add(scrubberTrackBar, 0, 0)
        scrubberPanel.Controls.Add(scrubberTimeLabel, 1, 0)

        previewPanel.Controls.Add(previewSurface, 0, 0)
        previewPanel.Controls.Add(scrubberPanel, 0, 1)
        previewPanel.Controls.Add(previewToolbarPanel, 0, 2)
        previewPanel.Controls.Add(speedButtonsPanel, 0, 3)
        previewPanel.Controls.Add(speedSeekPanel, 0, 4)

        statusLabel.AutoEllipsis = True
        statusLabel.Dock = DockStyle.Fill
        statusLabel.Margin = New Padding(0, 6, 0, 0)
        statusLabel.Text = "DeckLink Player ready. No playlist: grid selection is the source."
        statusLabel.TextAlign = ContentAlignment.MiddleLeft

        rootLayout.Controls.Add(toolbarPanel, 0, 0)
        rootLayout.Controls.Add(outputPanel, 0, 1)
        rootLayout.Controls.Add(browserSplit, 0, 2)
        rootLayout.Controls.Add(previewPanel, 0, 3)
        rootLayout.Controls.Add(statusLabel, 0, 4)
        Controls.Add(rootLayout)
    End Sub

    Private Sub AddSpeedPresetButton(speed As Double)
        Dim button As New Button() With {
            .Margin = New Padding(0, 0, 4, 4),
            .Size = New Size(If(Math.Abs(speed) = 0.5R OrElse Math.Abs(speed) = 1.5R, 56, 48), 26),
            .Tag = speed,
            .Text = FormatPlaybackSpeed(speed),
            .UseVisualStyleBackColor = True
        }

        AddHandler button.Click, AddressOf OnPlaybackSpeedClicked
        speedPresetButtons.Add(button)
        speedButtonsPanel.Controls.Add(button)
    End Sub

    Private Sub OnControlLoaded(sender As Object, e As EventArgs)
        BeginInvoke(New Action(AddressOf ApplyInitialBrowserSplit))
        LoadDeckLinkOutputDevices()
        RefreshFromRecordingDirectory()
    End Sub

    Private Sub ApplyInitialBrowserSplit()
        If hasAppliedInitialBrowserSplit OrElse IsDisposed OrElse browserSplit.Width <= 0 Then
            Return
        End If

        hasAppliedInitialBrowserSplit = True
        Dim availableWidth = Math.Max(0, browserSplit.Width - browserSplit.SplitterWidth)
        Dim halfWidth = availableWidth \ 4
        Dim maxDistance = Math.Max(browserSplit.Panel1MinSize, availableWidth - browserSplit.Panel2MinSize)
        browserSplit.SplitterDistance = Math.Max(browserSplit.Panel1MinSize, Math.Min(maxDistance, halfWidth))
    End Sub

    Private Sub OnRefreshClicked(sender As Object, e As EventArgs)
        LoadDeckLinkOutputDevices()
        RefreshFromRecordingDirectory()
    End Sub

    Private Sub LoadDeckLinkOutputDevices()
        Dim selectedDevice = TryCast(outputDeviceComboBox.SelectedItem, String)
        Dim savedDevice = GetSavedDeckLinkOutputDeviceName()
        Dim ffmpegPath = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe")
        Dim deviceNames = GetDeckLinkOutputDeviceNames(ffmpegPath)

        outputDeviceComboBox.Items.Clear()
        outputDeviceComboBox.Items.Add(NoDeckLinkOutputText)

        If deviceNames.Count = 0 Then
            outputDeviceComboBox.SelectedIndex = 0
            SetStatus("No DeckLink output device found. Local preview is still available.", warning:=True)
            UpdatePreviewButtons()
            Return
        End If

        For Each deviceName In deviceNames
            outputDeviceComboBox.Items.Add(deviceName)
        Next

        Dim targetDevice = selectedDevice

        If String.IsNullOrWhiteSpace(targetDevice) OrElse outputDeviceComboBox.Items.IndexOf(targetDevice) < 0 Then
            If Not String.IsNullOrWhiteSpace(savedDevice) AndAlso outputDeviceComboBox.Items.IndexOf(savedDevice) >= 0 Then
                targetDevice = savedDevice
            Else
                targetDevice = If(outputDeviceComboBox.Items.IndexOf(DefaultDeckLinkOutputDeviceName) >= 0, DefaultDeckLinkOutputDeviceName, deviceNames(0))
            End If
        End If

        outputDeviceComboBox.SelectedItem = targetDevice
        If IsNoDeckLinkOutputDevice(targetDevice) Then
            SetStatus("DeckLink output disabled. Local preview only.")
        Else
            SetStatus($"DeckLink output ready: {targetDevice}.")
        End If
        UpdatePreviewButtons()
    End Sub

    Private Shared Function GetDeckLinkOutputDeviceNames(ffmpegPath As String) As List(Of String)
        Dim deviceNames As New List(Of String)()

        If Not File.Exists(ffmpegPath) Then
            Return deviceNames
        End If

        Try
            Dim startInfo As New ProcessStartInfo() With {
                .FileName = ffmpegPath,
                .Arguments = "-hide_banner -sinks decklink",
                .WorkingDirectory = AppContext.BaseDirectory,
                .UseShellExecute = False,
                .RedirectStandardOutput = True,
                .RedirectStandardError = True,
                .CreateNoWindow = True
            }

            Using process As New Process() With {.StartInfo = startInfo}
                Dim outputBuilder As New StringBuilder()

                process.Start()
                outputBuilder.AppendLine(process.StandardOutput.ReadToEnd())
                outputBuilder.AppendLine(process.StandardError.ReadToEnd())

                If Not process.WaitForExit(3000) Then
                    process.Kill(True)
                    Return deviceNames
                End If

                Dim lines = outputBuilder.ToString().Split({ControlChars.Cr, ControlChars.Lf}, StringSplitOptions.RemoveEmptyEntries)

                For Each rawLine In lines
                    Dim startBracket = rawLine.IndexOf("["c)
                    Dim endBracket = rawLine.IndexOf("]"c, startBracket + 1)

                    If startBracket < 0 OrElse endBracket <= startBracket Then
                        Continue For
                    End If

                    Dim deviceName = rawLine.Substring(startBracket + 1, endBracket - startBracket - 1).Trim()

                    If Not String.IsNullOrWhiteSpace(deviceName) AndAlso Not deviceNames.Any(Function(existingDeviceName) String.Equals(existingDeviceName, deviceName, StringComparison.OrdinalIgnoreCase)) Then
                        deviceNames.Add(deviceName)
                    End If
                Next
            End Using
        Catch
        End Try

        Return deviceNames
    End Function

    Private Sub OnOpenFolderClicked(sender As Object, e As EventArgs)
        Dim folderPath = If(String.IsNullOrWhiteSpace(rootDirectoryPath), RecordingDirectorySettings.GetRecordingDirectory(), rootDirectoryPath)

        Try
            Directory.CreateDirectory(folderPath)
            Process.Start(New ProcessStartInfo() With {
                .FileName = folderPath,
                .UseShellExecute = True
            })
        Catch ex As Exception
            SetStatus($"Unable to open folder: {ex.Message}", warning:=True)
        End Try
    End Sub

    Private Sub OnFolderTreeBeforeExpand(sender As Object, e As TreeViewCancelEventArgs)
        If HasLoadingPlaceholder(e.Node) Then
            LoadDirectoryChildren(e.Node)
        End If
    End Sub

    Private Async Sub OnFolderTreeAfterSelect(sender As Object, e As TreeViewEventArgs)
        Dim folderPath = TryCast(e.Node.Tag, String)

        If String.IsNullOrWhiteSpace(folderPath) Then
            Return
        End If

        Await LoadFolderFilesAsync(folderPath)
    End Sub

    Private Async Sub OnFolderTreeMouseDown(sender As Object, e As MouseEventArgs)
        If e.Button <> MouseButtons.Left Then
            Return
        End If

        Dim clickedNode = folderTreeView.GetNodeAt(e.Location)

        If clickedNode Is Nothing OrElse Not Object.ReferenceEquals(clickedNode, folderTreeView.SelectedNode) Then
            Return
        End If

        Dim folderPath = TryCast(clickedNode.Tag, String)

        If String.IsNullOrWhiteSpace(folderPath) Then
            Return
        End If

        Await LoadFolderFilesAsync(folderPath)
    End Sub

    Private Sub OnFilesGridSelectionChanged(sender As Object, e As EventArgs)
        selectedFilePath = GetSelectedFilePath()

        If String.IsNullOrWhiteSpace(selectedFilePath) Then
            selectedFileLabel.Text = "Select a file in the grid, then Play or double-click."
        Else
            selectedFileLabel.Text = Path.GetFileName(selectedFilePath)
        End If

        RefreshScrubberForSelectedFile()
        UpdatePreviewButtons()
    End Sub

    Private Async Sub OnFilesGridCellDoubleClick(sender As Object, e As DataGridViewCellEventArgs)
        If e.RowIndex < 0 Then
            Return
        End If

        Await StartSelectedPlaybackAsync(resetToStart:=True)
    End Sub

    Private Async Sub OnFilesGridKeyDown(sender As Object, e As KeyEventArgs)
        If e.KeyCode <> Keys.Enter Then
            Return
        End If

        e.Handled = True
        e.SuppressKeyPress = True
        Await StartSelectedPlaybackAsync()
    End Sub

    Private Async Sub OnPreviewClicked(sender As Object, e As EventArgs)
        Await StartSelectedPlaybackAsync()
    End Sub

    Private Async Sub OnStopPreviewClicked(sender As Object, e As EventArgs)
        Await StopPlaybackAsync(clearImage:=True)
    End Sub

    Private Async Sub OnPlaybackSpeedClicked(sender As Object, e As EventArgs)
        Dim clickedButton = TryCast(sender, Button)

        If clickedButton Is Nothing OrElse clickedButton.Tag Is Nothing Then
            Return
        End If

        Dim selectedSpeed = NormalizePlaybackSpeed(Convert.ToDouble(clickedButton.Tag, CultureInfo.InvariantCulture))
        Await SetPlaybackSpeedAsync(selectedSpeed, restartIfPlaying:=True, startIfStopped:=Math.Abs(selectedSpeed) >= 0.001R)
    End Sub

    Private Async Sub OnSpeedTrackBarValueChanged(sender As Object, e As EventArgs)
        If isUpdatingSpeedControls Then
            Return
        End If

        Await SetPlaybackSpeedAsync(NormalizePlaybackSpeed(speedTrackBar.Value / 10.0R), restartIfPlaying:=True)
    End Sub

    Private Async Function SetPlaybackSpeedAsync(selectedSpeed As Double, restartIfPlaying As Boolean, Optional startIfStopped As Boolean = False) As Task
        selectedSpeed = NormalizePlaybackSpeed(selectedSpeed)
        Dim wasPlaying = IsPlaybackActive()
        Dim shouldStartPlayback = startIfStopped AndAlso Not wasPlaying AndAlso Math.Abs(selectedSpeed) >= 0.001R

        If Math.Abs(playbackSpeedMultiplier - selectedSpeed) < 0.001R Then
            UpdateSpeedControls()

            If shouldStartPlayback Then
                playbackStartOffset = GetScrubberOffset()
                Dim startFilePath = If(IsScrubberLoaded(), scrubberLoadedFilePath, Nothing)
                Await StartSelectedPlaybackAsync(filePathOverride:=startFilePath)
            End If

            Return
        End If

        playbackSpeedMultiplier = selectedSpeed
        UpdateSpeedControls()
        SetStatus($"Playback speed: {FormatPlaybackSpeed(playbackSpeedMultiplier)}")

        If Not restartIfPlaying OrElse (Not wasPlaying AndAlso Not startIfStopped) Then
            Return
        End If

        playbackStartOffset = GetScrubberOffset()
        Dim restartFilePath = If(IsScrubberLoaded(), scrubberLoadedFilePath, Nothing)

        If Math.Abs(playbackSpeedMultiplier) < 0.001R Then
            Await HoldCurrentFrameAsync()
        ElseIf shuttlePlaybackActive AndAlso playbackSpeedMultiplier < 0.0R Then
            shuttleClockOffset = playbackStartOffset
            shuttleClockStartedAtUtc = DateTime.UtcNow
            SetStatus($"Shuttle playback {FormatPlaybackSpeed(playbackSpeedMultiplier)}")
        Else
            Await StartSelectedPlaybackAsync(filePathOverride:=restartFilePath)
        End If
    End Function

    Private Sub OnOutputSelectionChanged(sender As Object, e As EventArgs)
        SaveDeckLinkOutputSelection()

        Dim deviceName = TryCast(outputDeviceComboBox.SelectedItem, String)
        Dim outputMode = TryCast(outputModeComboBox.SelectedItem, DeckLinkOutputMode)

        If IsNoDeckLinkOutputDevice(deviceName) Then
            SetStatus("DeckLink output disabled. Local preview only.")
        ElseIf outputMode IsNot Nothing Then
            SetStatus($"DeckLink output ready: {deviceName} {outputMode.DisplayName}.")
        End If

        UpdatePreviewButtons()
    End Sub

    Private Sub OnScrubberMouseDown(sender As Object, e As MouseEventArgs)
        If scrubberTrackBar.Enabled AndAlso e.Button = MouseButtons.Left Then
            StopShuttlePlaybackTimer()
            isScrubberDragging = True
            SetScrubberPositionFromMouse(e)
            QueueScrubFramePreview(GetScrubberOffset(), updateDeckLink:=True)
        End If
    End Sub

    Private Sub OnScrubberMouseMove(sender As Object, e As MouseEventArgs)
        If isScrubberDragging AndAlso scrubberTrackBar.Enabled Then
            SetScrubberPositionFromMouse(e)
            ScheduleScrubFramePreview()
        End If
    End Sub

    Private Async Sub OnScrubberMouseUp(sender As Object, e As MouseEventArgs)
        If Not scrubberTrackBar.Enabled Then
            Return
        End If

        Dim shouldStartPlayback = isScrubberDragging AndAlso e.Button = MouseButtons.Left

        If e.Button = MouseButtons.Left Then
            SetScrubberPositionFromMouse(e)
        End If

        isScrubberDragging = False
        scrubPreviewTimer.Stop()
        QueueScrubFramePreview(GetScrubberOffset(), updateDeckLink:=True)
        Await WaitForScrubFrameQueueAsync()

        If shouldStartPlayback Then
            Await StartSelectedPlaybackAsync(filePathOverride:=scrubberLoadedFilePath)
        End If
    End Sub

    Private Sub OnScrubberScrolled(sender As Object, e As EventArgs)
        If scrubberTrackBar.Enabled Then
            SetScrubberPosition(GetScrubberOffset())

            If isScrubberDragging Then
                ScheduleScrubFramePreview()
            End If
        End If
    End Sub

    Private Async Sub OnScrubberKeyUp(sender As Object, e As KeyEventArgs)
        If scrubberTrackBar.Enabled Then
            QueueScrubFramePreview(GetScrubberOffset(), updateDeckLink:=True)
            Await WaitForScrubFrameQueueAsync()
        End If
    End Sub

    Private Sub OnScrubPreviewTimerTick(sender As Object, e As EventArgs)
        scrubPreviewTimer.Stop()

        If scrubberTrackBar.Enabled AndAlso isScrubberDragging Then
            QueueScrubFramePreview(GetScrubberOffset(), updateDeckLink:=True)
        End If
    End Sub

    Private Sub OnPlaybackPositionTimerTick(sender As Object, e As EventArgs)
        If isScrubberDragging OrElse isSeekingPlayback Then
            Return
        End If

        If previewRunner Is Nothing AndAlso outputRunner Is Nothing Then
            StopPlaybackClock()
            Return
        End If

        Dim elapsed = DateTime.UtcNow - playbackClockStartedAtUtc
        Dim position = playbackClockOffset + ScaleTimeSpan(elapsed, Math.Max(0.0R, playbackSpeedMultiplier))
        Dim duration = GetSelectedDuration()

        If duration.HasValue AndAlso duration.Value > TimeSpan.Zero AndAlso position >= duration.Value Then
            SetScrubberPosition(duration.Value)
            StopPlaybackClock()
            Return
        End If

        SetScrubberPosition(position)
    End Sub

    Private Async Sub OnShuttlePlaybackTimerTick(sender As Object, e As EventArgs)
        If Not shuttlePlaybackActive OrElse isScrubberDragging OrElse isSeekingPlayback Then
            Return
        End If

        If Math.Abs(playbackSpeedMultiplier) < 0.001R Then
            StopShuttlePlaybackTimer()
            Await HoldCurrentFrameAsync()
            Return
        End If

        Dim elapsed = DateTime.UtcNow - shuttleClockStartedAtUtc
        Dim position = ClampToSelectedDuration(shuttleClockOffset + ScaleTimeSpan(elapsed, playbackSpeedMultiplier))
        Dim duration = GetSelectedDuration()

        If playbackSpeedMultiplier < 0.0R AndAlso position <= TimeSpan.Zero Then
            StopShuttlePlaybackTimer()
            SetScrubberPosition(TimeSpan.Zero)
            QueueScrubFramePreview(TimeSpan.Zero, updateDeckLink:=True)
            SetStatus("Shuttle reached start. Holding frame.")
            Return
        End If

        If playbackSpeedMultiplier > 0.0R AndAlso duration.HasValue AndAlso duration.Value > TimeSpan.Zero AndAlso position >= duration.Value Then
            StopShuttlePlaybackTimer()
            SetScrubberPosition(duration.Value)
            QueueScrubFramePreview(duration.Value, updateDeckLink:=True)
            SetStatus("Shuttle reached end. Holding frame.")
            Return
        End If

        playbackStartOffset = position
        SetScrubberPosition(position)
        QueueScrubFramePreview(position, updateDeckLink:=True)
    End Sub

    Private Sub ScheduleScrubFramePreview()
        scrubPreviewTimer.Stop()
        scrubPreviewTimer.Start()
    End Sub

    Private Sub QueueScrubFramePreview(offset As TimeSpan, updateDeckLink As Boolean)
        pendingScrubFrameOffset = ClampToSelectedDuration(offset)
        pendingScrubFrameShouldUpdateDeckLink = pendingScrubFrameShouldUpdateDeckLink OrElse updateDeckLink
        scrubFrameRequestGeneration += 1
        currentScrubFrameCancellation?.Cancel()

        If isScrubFrameRenderRunning Then
            Return
        End If

        Dim ignored = ProcessQueuedScrubFramePreviewAsync()
    End Sub

    Private Async Function WaitForScrubFrameQueueAsync() As Task
        While isScrubFrameRenderRunning OrElse pendingScrubFrameOffset.HasValue
            Await Task.Delay(25)
        End While
    End Function

    Private Async Function ProcessQueuedScrubFramePreviewAsync() As Task
        If isScrubFrameRenderRunning Then
            Return
        End If

        isScrubFrameRenderRunning = True

        Try
            While pendingScrubFrameOffset.HasValue
                Dim targetOffset = pendingScrubFrameOffset.Value
                Dim updateDeckLink = pendingScrubFrameShouldUpdateDeckLink
                Dim requestGeneration = scrubFrameRequestGeneration
                Dim cancellationSource As New CancellationTokenSource()
                currentScrubFrameCancellation = cancellationSource
                pendingScrubFrameOffset = Nothing
                pendingScrubFrameShouldUpdateDeckLink = False

                Try
                    Await ScrubToFrameAsync(targetOffset, updateDeckLink, requestGeneration, cancellationSource.Token)
                Finally
                    If Object.ReferenceEquals(currentScrubFrameCancellation, cancellationSource) Then
                        currentScrubFrameCancellation = Nothing
                    End If

                    cancellationSource.Dispose()
                End Try
            End While
        Catch ex As OperationCanceledException
        Catch ex As Exception
            SetStatus($"Unable to show scrub frame: {ex.Message}", warning:=True)
        Finally
            isScrubFrameRenderRunning = False

            If pendingScrubFrameOffset.HasValue Then
                Dim ignored = ProcessQueuedScrubFramePreviewAsync()
            End If
        End Try
    End Function

    Private Async Function ScrubToFrameAsync(offset As TimeSpan, updateDeckLink As Boolean, requestGeneration As Integer, cancellationToken As CancellationToken) As Task
        If isSeekingPlayback Then
            Return
        End If

        cancellationToken.ThrowIfCancellationRequested()
        playbackStartOffset = ClampToSelectedDuration(offset)
        SetScrubberPosition(playbackStartOffset)

        Dim filePath = scrubberLoadedFilePath

        If String.IsNullOrWhiteSpace(filePath) Then
            Return
        End If

        isSeekingPlayback = True
        StopPlaybackClock()

        Try
            SetStatus($"Showing frame at {FormatDuration(playbackStartOffset)}...")
            Await StopPlaybackForScrubAsync(clearImage:=False, stopDeckLinkOutput:=False)
            cancellationToken.ThrowIfCancellationRequested()
            Await RenderStillPreviewFrameAsync(filePath, playbackStartOffset, requestGeneration, cancellationToken)
            cancellationToken.ThrowIfCancellationRequested()
            Dim sdiFrameReady = False
            If updateDeckLink Then
                sdiFrameReady = Await StartScrubDeckLinkFrameAsync(filePath, playbackStartOffset, requestGeneration, cancellationToken)
            End If

            If requestGeneration <> scrubFrameRequestGeneration OrElse cancellationToken.IsCancellationRequested Then
                Return
            End If

            If sdiFrameReady Then
                SetStatus($"Preview and SDI frame ready at {FormatDuration(playbackStartOffset)}. Press Play to start from here.")
            Else
                SetStatus($"Preview frame ready at {FormatDuration(playbackStartOffset)}. Press Play to start from here.")
            End If
        Finally
            isSeekingPlayback = False
            UpdatePreviewButtons()
        End Try
    End Function

    Private Shared Function GetSavedDeckLinkOutputDeviceName() As String
        Return GetSavedDeckLinkOutputSetting("Device")
    End Function

    Private Shared Function GetSavedDeckLinkOutputModeName() As String
        Return GetSavedDeckLinkOutputSetting("Mode")
    End Function

    Private Shared Function GetSavedDeckLinkOutputSetting(settingName As String) As String
        Try
            If Not File.Exists(DeckLinkPlayerOutputSettingsFilePath) Then
                Return String.Empty
            End If

            For Each rawLine In File.ReadAllLines(DeckLinkPlayerOutputSettingsFilePath)
                Dim separatorIndex = rawLine.IndexOf("="c)

                If separatorIndex <= 0 Then
                    Continue For
                End If

                Dim name = rawLine.Substring(0, separatorIndex).Trim()

                If String.Equals(name, settingName, StringComparison.OrdinalIgnoreCase) Then
                    Return rawLine.Substring(separatorIndex + 1).Trim()
                End If
            Next
        Catch
        End Try

        Return String.Empty
    End Function

    Private Sub SaveDeckLinkOutputSelection()
        Dim deviceName = TryCast(outputDeviceComboBox.SelectedItem, String)
        Dim outputMode = TryCast(outputModeComboBox.SelectedItem, DeckLinkOutputMode)

        If String.IsNullOrWhiteSpace(deviceName) OrElse outputMode Is Nothing Then
            Return
        End If

        Try
            Directory.CreateDirectory(Path.GetDirectoryName(DeckLinkPlayerOutputSettingsFilePath))
            File.WriteAllLines(DeckLinkPlayerOutputSettingsFilePath, {
                $"Device={deviceName}",
                $"Mode={outputMode.DisplayName}"
            })
        Catch
        End Try
    End Sub

    Private Sub LoadFolderTree(folderPath As String)
        folderTreeView.BeginUpdate()

        Try
            folderTreeView.Nodes.Clear()

            If String.IsNullOrWhiteSpace(folderPath) OrElse Not Directory.Exists(folderPath) Then
                filesGridView.Rows.Clear()
                SetStatus("Recording folder is not available.", warning:=True)
                Return
            End If

            Dim rootText = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))

            If String.IsNullOrWhiteSpace(rootText) Then
                rootText = folderPath
            End If

            Dim rootNode = CreateDirectoryNode(folderPath, rootText)
            folderTreeView.Nodes.Add(rootNode)
            LoadDirectoryChildren(rootNode)
            rootNode.Expand()
            folderTreeView.SelectedNode = rootNode
            SetStatus($"Loaded recording folder: {folderPath}")
        Finally
            folderTreeView.EndUpdate()
        End Try
    End Sub

    Private Shared Function CreateDirectoryNode(folderPath As String, text As String) As TreeNode
        Dim node As New TreeNode(text) With {
            .Tag = folderPath,
            .ToolTipText = folderPath
        }

        node.Nodes.Add(New TreeNode(LoadingNodeText))
        Return node
    End Function

    Private Sub LoadDirectoryChildren(node As TreeNode)
        Dim folderPath = TryCast(node.Tag, String)

        node.Nodes.Clear()

        If String.IsNullOrWhiteSpace(folderPath) OrElse Not Directory.Exists(folderPath) Then
            Return
        End If

        Try
            For Each directoryPath In Directory.EnumerateDirectories(folderPath).
                Where(Function(path) Not IsIgnoredDirectory(path)).
                OrderBy(Function(childPath) Path.GetFileName(childPath), StringComparer.OrdinalIgnoreCase)

                node.Nodes.Add(CreateDirectoryNode(directoryPath, Path.GetFileName(directoryPath)))
            Next
        Catch ex As UnauthorizedAccessException
            node.Nodes.Add(New TreeNode("Access denied"))
        Catch ex As IOException
            node.Nodes.Add(New TreeNode($"Unable to read: {ex.Message}"))
        End Try
    End Sub

    Private Shared Function HasLoadingPlaceholder(node As TreeNode) As Boolean
        Return node.Nodes.Count = 1 AndAlso
            node.Nodes(0).Tag Is Nothing AndAlso
            String.Equals(node.Nodes(0).Text, LoadingNodeText, StringComparison.OrdinalIgnoreCase)
    End Function

    Private Async Sub RefreshSelectedFolderFiles()
        Dim selectedFolder = TryCast(folderTreeView.SelectedNode?.Tag, String)

        If String.IsNullOrWhiteSpace(selectedFolder) Then
            selectedFolder = rootDirectoryPath
        End If

        Await LoadFolderFilesAsync(selectedFolder)
    End Sub

    Private Async Function LoadFolderFilesAsync(folderPath As String) As Task
        If isLoadingFiles Then
            Return
        End If

        isLoadingFiles = True
        refreshButton.Enabled = False
        filesGridView.Rows.Clear()
        selectedFilePath = Nothing
        RefreshScrubberForSelectedFile()
        UpdatePreviewButtons()
        SetStatus($"Loading files from {folderPath}...")

        Try
            Dim files = Await Task.Run(Function() GetMediaFiles(folderPath))

            If IsDisposed Then
                Return
            End If

            PopulateFileGrid(files)
            StartDurationProbe(files.Select(Function(fileInfo) fileInfo.FullName).ToList())
            SetStatus($"{files.Count} media file(s) loaded from {folderPath}.")
        Catch ex As Exception
            If Not IsDisposed Then
                SetStatus($"Unable to load files: {ex.Message}", warning:=True)
            End If
        Finally
            isLoadingFiles = False
            refreshButton.Enabled = True
            UpdatePreviewButtons()
        End Try
    End Function

    Private Shared Function GetMediaFiles(folderPath As String) As List(Of FileInfo)
        If String.IsNullOrWhiteSpace(folderPath) OrElse Not Directory.Exists(folderPath) Then
            Return New List(Of FileInfo)()
        End If

        Return Directory.EnumerateFiles(folderPath, "*", SearchOption.TopDirectoryOnly).
            Where(AddressOf IsSupportedMediaFile).
            Select(Function(path) New FileInfo(path)).
            OrderByDescending(Function(fileInfo) fileInfo.LastWriteTime).
            ThenBy(Function(fileInfo) fileInfo.Name, StringComparer.OrdinalIgnoreCase).
            ToList()
    End Function

    Private Sub PopulateFileGrid(files As IReadOnlyList(Of FileInfo))
        filesGridView.Rows.Clear()

        For Each fileInfo In files
            Dim rowIndex = filesGridView.Rows.Add(
                fileInfo.Name,
                "--",
                FormatBytes(fileInfo.Length),
                fileInfo.FullName)

            filesGridView.Rows(rowIndex).Tag = fileInfo.FullName
        Next

        If filesGridView.Rows.Count > 0 Then
            filesGridView.Rows(0).Selected = True
            filesGridView.CurrentCell = filesGridView.Rows(0).Cells("Name")
        End If
    End Sub

    Private Function GetRelativeFolderText(folderPath As String) As String
        If String.IsNullOrWhiteSpace(folderPath) OrElse String.IsNullOrWhiteSpace(rootDirectoryPath) Then
            Return String.Empty
        End If

        Try
            Dim relativePath = Path.GetRelativePath(rootDirectoryPath, folderPath)

            If String.Equals(relativePath, ".", StringComparison.OrdinalIgnoreCase) Then
                Return "Root"
            End If

            Return relativePath
        Catch
            Return folderPath
        End Try
    End Function

    Private Function GetSelectedFilePath() As String
        If filesGridView.CurrentRow IsNot Nothing AndAlso filesGridView.CurrentRow.Tag IsNot Nothing Then
            Dim currentPath = TryCast(filesGridView.CurrentRow.Tag, String)

            If Not String.IsNullOrWhiteSpace(currentPath) AndAlso File.Exists(currentPath) Then
                Return currentPath
            End If
        End If

        If filesGridView.SelectedRows.Count > 0 AndAlso filesGridView.SelectedRows(0).Tag IsNot Nothing Then
            Dim selectedPath = TryCast(filesGridView.SelectedRows(0).Tag, String)

            If Not String.IsNullOrWhiteSpace(selectedPath) AndAlso File.Exists(selectedPath) Then
                Return selectedPath
            End If
        End If

        Return Nothing
    End Function

    Private Async Sub StartDurationProbe(filePaths As IReadOnlyList(Of String))
        durationProbeGeneration += 1
        Dim probeGeneration = durationProbeGeneration
        Dim ffprobePath = Path.Combine(AppContext.BaseDirectory, "ffprobe.exe")

        If filePaths.Count = 0 OrElse Not File.Exists(ffprobePath) Then
            Return
        End If

        For Each filePath In filePaths
            If probeGeneration <> durationProbeGeneration OrElse IsDisposed Then
                Return
            End If

            Dim duration = Await Task.Run(Function() ProbeDuration(ffprobePath, filePath))
            Dim durationText = If(duration.HasValue, FormatDuration(duration.Value), If(IsImageFile(filePath), "Still", "--"))

            If probeGeneration <> durationProbeGeneration OrElse IsDisposed Then
                Return
            End If

            UpdateDurationCell(filePath, durationText, duration)
        Next
    End Sub

    Private Sub UpdateDurationCell(filePath As String, durationText As String, duration As TimeSpan?)
        If InvokeRequired Then
            BeginInvoke(New Action(Of String, String, TimeSpan?)(AddressOf UpdateDurationCell), filePath, durationText, duration)
            Return
        End If

        If duration.HasValue Then
            durationByPath(filePath) = duration.Value
        Else
            durationByPath.Remove(filePath)
        End If

        For Each row As DataGridViewRow In filesGridView.Rows
            If row.Tag IsNot Nothing AndAlso String.Equals(TryCast(row.Tag, String), filePath, StringComparison.OrdinalIgnoreCase) Then
                row.Cells("Duration").Value = durationText
                Exit For
            End If
        Next

        If String.Equals(scrubberLoadedFilePath, filePath, StringComparison.OrdinalIgnoreCase) Then
            RefreshScrubberForSelectedFile()
        End If
    End Sub

    Private Shared Function ProbeDuration(ffprobePath As String, filePath As String) As TimeSpan?
        If IsImageFile(filePath) Then
            Return Nothing
        End If

        Try
            Dim startInfo As New ProcessStartInfo() With {
                .FileName = ffprobePath,
                .Arguments = $"-v error -show_entries format=duration -of default=nw=1:nk=1 {Quote(filePath)}",
                .WorkingDirectory = AppContext.BaseDirectory,
                .UseShellExecute = False,
                .RedirectStandardOutput = True,
                .RedirectStandardError = True,
                .CreateNoWindow = True
            }

            Using process As New Process() With {.StartInfo = startInfo}
                If Not process.Start() Then
                    Return Nothing
                End If

                Dim output = process.StandardOutput.ReadToEnd().Trim()

                If Not process.WaitForExit(3000) Then
                    process.Kill(True)
                    Return Nothing
                End If

                Dim seconds As Double

                If process.ExitCode <> 0 OrElse Not Double.TryParse(output, NumberStyles.Float, CultureInfo.InvariantCulture, seconds) OrElse seconds < 0 Then
                    Return Nothing
                End If

                Return TimeSpan.FromSeconds(seconds)
            End Using
        Catch
            Return Nothing
        End Try
    End Function

    Private Shared Function FormatDuration(duration As TimeSpan) As String
        Dim totalFrames = CLng(Math.Round(duration.TotalSeconds * DurationDisplayFrameRate, MidpointRounding.AwayFromZero))
        Dim framesPerHour = DurationDisplayFrameRate * 60 * 60
        Dim framesPerMinute = DurationDisplayFrameRate * 60
        Dim hours = totalFrames \ framesPerHour
        Dim remainingFrames = totalFrames Mod framesPerHour
        Dim minutes = remainingFrames \ framesPerMinute
        remainingFrames = remainingFrames Mod framesPerMinute
        Dim seconds = remainingFrames \ DurationDisplayFrameRate
        Dim frames = remainingFrames Mod DurationDisplayFrameRate

        Return $"{hours:00}:{minutes:00}:{seconds:00}:{frames:00}"
    End Function

    Private Shared Function NormalizePlaybackSpeed(speed As Double) As Double
        If Double.IsNaN(speed) OrElse Double.IsInfinity(speed) Then
            Return 1.0R
        End If

        Dim clamped = Math.Max(-20.0R, Math.Min(20.0R, speed))

        If Math.Abs(clamped) < 0.001R Then
            Return 0.0R
        End If

        Return Math.Round(clamped, 1, MidpointRounding.AwayFromZero)
    End Function

    Private Shared Function FormatPlaybackSpeed(speed As Double) As String
        Dim normalized = NormalizePlaybackSpeed(speed)

        If Math.Abs(normalized) < 0.001R Then
            Return "0x"
        End If

        Dim prefix = If(normalized > 0.0R, "+", String.Empty)
        Return $"{prefix}{normalized.ToString("0.#", CultureInfo.InvariantCulture)}x"
    End Function

    Private Shared Function FormatFilterNumber(value As Double) As String
        Return Math.Max(0.001R, Math.Abs(value)).ToString("0.###", CultureInfo.InvariantCulture)
    End Function

    Private Shared Function BuildAudioSpeedFilterChain(playbackSpeed As Double) As String
        Dim speed = Math.Abs(NormalizePlaybackSpeed(playbackSpeed))

        If speed < 0.001R OrElse Math.Abs(speed - 1.0R) < 0.001R Then
            Return String.Empty
        End If

        Dim remaining = speed
        Dim filters As New List(Of String)()

        While remaining > 2.0R
            filters.Add("atempo=2")
            remaining /= 2.0R
        End While

        While remaining < 0.5R
            filters.Add("atempo=0.5")
            remaining /= 0.5R
        End While

        If Math.Abs(remaining - 1.0R) >= 0.001R Then
            filters.Add($"atempo={FormatFilterNumber(remaining)}")
        End If

        Return String.Join(",", filters)
    End Function

    Private Function GetSelectedDuration() As TimeSpan?
        If Not IsScrubberLoaded() Then
            Return Nothing
        End If

        Dim duration As TimeSpan

        If durationByPath.TryGetValue(scrubberLoadedFilePath, duration) AndAlso duration > TimeSpan.Zero Then
            Return duration
        End If

        Return Nothing
    End Function

    Private Function IsScrubberLoaded() As Boolean
        Return Not String.IsNullOrWhiteSpace(scrubberLoadedFilePath)
    End Function

    Private Sub RefreshScrubberForSelectedFile()
        If Not IsScrubberLoaded() Then
            ClearScrubber()
            Return
        End If

        Dim duration = GetSelectedDuration()

        If Not duration.HasValue Then
            scrubberTrackBar.Enabled = False
            scrubberTrackBar.Value = 0
            scrubberTrackBar.Maximum = 0
            scrubberTimeLabel.Text = If(IsImageFile(scrubberLoadedFilePath), "Still image", "--:--:--:-- / --:--:--:--")
            Return
        End If

        Dim maxFrame = Math.Max(1, TimeSpanToFrame(duration.Value))
        scrubberTrackBar.Maximum = maxFrame
        scrubberTrackBar.Enabled = True
        If scrubberTrackBar.Enabled Then
            playbackStartOffset = GetScrubberOffset()
        Else
            playbackStartOffset = ClampToSelectedDuration(playbackStartOffset)
        End If

        SetScrubberPosition(playbackStartOffset)
    End Sub

    Private Sub ClearScrubber()
        scrubberTrackBar.Enabled = False
        scrubberTrackBar.Value = 0
        scrubberTrackBar.Maximum = 0
        scrubberTimeLabel.Text = "--:--:--:-- / --:--:--:--"
    End Sub

    Private Sub SetScrubberPosition(position As TimeSpan)
        Dim duration = GetSelectedDuration()

        If Not duration.HasValue Then
            scrubberTimeLabel.Text = "--:--:--:-- / --:--:--:--"
            Return
        End If

        Dim clampedPosition = ClampToSelectedDuration(position)
        Dim frame = Math.Min(scrubberTrackBar.Maximum, TimeSpanToFrame(clampedPosition))

        If scrubberTrackBar.Value <> frame Then
            scrubberTrackBar.Value = frame
        End If

        scrubberTimeLabel.Text = $"{FormatDuration(clampedPosition)} / {FormatDuration(duration.Value)}"
    End Sub

    Private Function GetScrubberOffset() As TimeSpan
        Return ClampToSelectedDuration(FrameToTimeSpan(scrubberTrackBar.Value))
    End Function

    Private Sub SetScrubberPositionFromMouse(e As MouseEventArgs)
        If Not scrubberTrackBar.Enabled OrElse scrubberTrackBar.Maximum <= scrubberTrackBar.Minimum Then
            Return
        End If

        Dim width = Math.Max(1, scrubberTrackBar.ClientSize.Width)
        Dim ratio = Math.Max(0.0R, Math.Min(1.0R, e.X / CDbl(width)))
        Dim frame = scrubberTrackBar.Minimum + CInt(Math.Round(ratio * (scrubberTrackBar.Maximum - scrubberTrackBar.Minimum), MidpointRounding.AwayFromZero))
        scrubberTrackBar.Value = Math.Max(scrubberTrackBar.Minimum, Math.Min(scrubberTrackBar.Maximum, frame))
        SetScrubberPosition(GetScrubberOffset())
    End Sub

    Private Function ClampToSelectedDuration(position As TimeSpan) As TimeSpan
        If position < TimeSpan.Zero Then
            Return TimeSpan.Zero
        End If

        Dim duration = GetSelectedDuration()

        If duration.HasValue AndAlso duration.Value > TimeSpan.Zero AndAlso position > duration.Value Then
            Return duration.Value
        End If

        Return position
    End Function

    Private Function ClampToPlayableStartOffset(position As TimeSpan) As TimeSpan
        Dim clampedPosition = ClampToSelectedDuration(position)
        Dim duration = GetSelectedDuration()

        If duration.HasValue AndAlso duration.Value > TimeSpan.Zero AndAlso clampedPosition >= duration.Value Then
            Dim oneFrame = TimeSpan.FromSeconds(1.0R / DurationDisplayFrameRate)
            Return If(duration.Value > oneFrame, duration.Value - oneFrame, TimeSpan.Zero)
        End If

        Return clampedPosition
    End Function

    Private Shared Function TimeSpanToFrame(duration As TimeSpan) As Integer
        Dim frame = Math.Round(Math.Max(0, duration.TotalSeconds) * DurationDisplayFrameRate, MidpointRounding.AwayFromZero)
        Return CInt(Math.Min(Integer.MaxValue, frame))
    End Function

    Private Shared Function FrameToTimeSpan(frame As Integer) As TimeSpan
        Return TimeSpan.FromSeconds(Math.Max(0, frame) / CDbl(DurationDisplayFrameRate))
    End Function

    Private Shared Function ScaleTimeSpan(value As TimeSpan, multiplier As Double) As TimeSpan
        Dim scaledTicks = value.Ticks * multiplier

        If scaledTicks > Long.MaxValue Then
            Return TimeSpan.FromTicks(Long.MaxValue)
        End If

        If scaledTicks < Long.MinValue Then
            Return TimeSpan.FromTicks(Long.MinValue)
        End If

        Return TimeSpan.FromTicks(CLng(scaledTicks))
    End Function

    Private Sub StartPlaybackClock(startOffset As TimeSpan)
        playbackClockOffset = ClampToSelectedDuration(startOffset)
        playbackClockStartedAtUtc = DateTime.UtcNow
        playbackPositionTimer.Interval = 200
        playbackPositionTimer.Start()
        SetScrubberPosition(playbackClockOffset)
    End Sub

    Private Sub StopPlaybackClock()
        playbackPositionTimer.Stop()
    End Sub

    Private Sub StopShuttlePlaybackTimer()
        shuttlePlaybackTimer.Stop()
        shuttlePlaybackActive = False

        If Not IsDisposed Then
            UpdatePreviewButtons()
        End If
    End Sub

    Private Async Function HoldCurrentFrameAsync() As Task
        StopPlaybackClock()
        StopShuttlePlaybackTimer()
        playbackStartOffset = GetScrubberOffset()
        Await StopPlaybackForScrubAsync(clearImage:=False, stopDeckLinkOutput:=False)
        QueueScrubFramePreview(playbackStartOffset, updateDeckLink:=True)
        Await WaitForScrubFrameQueueAsync()
        SetStatus($"Holding frame at {FormatDuration(playbackStartOffset)}.")
    End Function

    Private Async Function StartShuttlePlaybackAsync(Optional resetToStart As Boolean = False, Optional filePathOverride As String = Nothing) As Task
        Dim filePath = If(String.IsNullOrWhiteSpace(filePathOverride), GetSelectedFilePath(), filePathOverride)
        Dim updateSelection = String.IsNullOrWhiteSpace(filePathOverride)

        If String.IsNullOrWhiteSpace(filePath) Then
            SetStatus("Select a file from the grid first.", warning:=True)
            Return
        End If

        If updateSelection Then
            selectedFilePath = filePath
        End If

        If Not String.Equals(scrubberLoadedFilePath, filePath, StringComparison.OrdinalIgnoreCase) Then
            scrubberLoadedFilePath = filePath
            playbackStartOffset = TimeSpan.Zero
        End If
        RefreshScrubberForSelectedFile()

        If resetToStart Then
            playbackStartOffset = TimeSpan.Zero
            SetScrubberPosition(TimeSpan.Zero)
        End If

        StopShuttlePlaybackTimer()
        scrubPreviewTimer.Stop()
        pendingScrubFrameOffset = Nothing
        pendingScrubFrameShouldUpdateDeckLink = False
        Await WaitForScrubFrameQueueAsync()

        StopPlaybackClock()
        Await StopPreviewAsync(clearImage:=False)
        TearDownAudioMonitor(fast:=True)

        playbackStartOffset = ClampToSelectedDuration(GetScrubberOffset())
        shuttleClockOffset = playbackStartOffset
        shuttleClockStartedAtUtc = DateTime.UtcNow
        shuttlePlaybackActive = True
        shuttlePlaybackTimer.Start()
        QueueScrubFramePreview(playbackStartOffset, updateDeckLink:=True)
        SetStatus($"Shuttle playback {FormatPlaybackSpeed(playbackSpeedMultiplier)}: {Path.GetFileName(filePath)}")
        UpdatePreviewButtons()
    End Function

    Private Async Function StartSelectedPlaybackAsync(Optional resetToStart As Boolean = False, Optional filePathOverride As String = Nothing) As Task
        Dim filePath = If(String.IsNullOrWhiteSpace(filePathOverride), GetSelectedFilePath(), filePathOverride)
        Dim updateSelection = String.IsNullOrWhiteSpace(filePathOverride)

        If String.IsNullOrWhiteSpace(filePath) Then
            SetStatus("Select a file from the grid first.", warning:=True)
            Return
        End If

        If updateSelection Then
            selectedFilePath = filePath
        End If

        If Not String.Equals(scrubberLoadedFilePath, filePath, StringComparison.OrdinalIgnoreCase) Then
            scrubberLoadedFilePath = filePath
            playbackStartOffset = TimeSpan.Zero
        End If
        RefreshScrubberForSelectedFile()

        If resetToStart Then
            playbackStartOffset = TimeSpan.Zero
            SetScrubberPosition(TimeSpan.Zero)
        End If

        playbackSpeedMultiplier = NormalizePlaybackSpeed(playbackSpeedMultiplier)
        UpdateSpeedControls()

        If Math.Abs(playbackSpeedMultiplier) < 0.001R Then
            Await HoldCurrentFrameAsync()
            Return
        End If

        If playbackSpeedMultiplier < 0.0R Then
            Await StartShuttlePlaybackAsync(resetToStart, filePath)
            Return
        End If

        StopShuttlePlaybackTimer()
        scrubPreviewTimer.Stop()
        pendingScrubFrameOffset = Nothing
        pendingScrubFrameShouldUpdateDeckLink = False
        Await WaitForScrubFrameQueueAsync()

        playbackStartOffset = ClampToPlayableStartOffset(playbackStartOffset)
        SetScrubberPosition(playbackStartOffset)

        Dim selectedOutputDevice = TryCast(outputDeviceComboBox.SelectedItem, String)
        Dim keepDeckLinkOutputUntilPreviewReady = outputRunner IsNot Nothing AndAlso Not IsNoDeckLinkOutputDevice(selectedOutputDevice)
        Dim firstPreviewFrameSource As TaskCompletionSource(Of Boolean) = Nothing

        If keepDeckLinkOutputUntilPreviewReady Then
            StopPlaybackClock()
            TearDownAudioMonitor(fast:=True)
            Await StopPreviewAsync(clearImage:=False)
            firstPreviewFrameSource = PrepareFirstPreviewFrameWait()
        Else
            Await StopPlaybackAsync(clearImage:=False)
            firstPreviewFrameSource = PrepareFirstPreviewFrameWait()
        End If

        Await StartPreviewAsync(filePath, playbackStartOffset)

        If keepDeckLinkOutputUntilPreviewReady Then
            Await WaitForFirstPreviewFrameAsync(firstPreviewFrameSource, TimeSpan.FromSeconds(3))
        End If

        Await StartOutputAsync(filePath, playbackStartOffset)
        StartAudioMonitor(filePath, playbackStartOffset, playbackSpeedMultiplier)
        StartPlaybackClock(playbackStartOffset)
    End Function

    Private Function PrepareFirstPreviewFrameWait() As TaskCompletionSource(Of Boolean)
        Dim source As New TaskCompletionSource(Of Boolean)(TaskCreationOptions.RunContinuationsAsynchronously)
        playbackFirstPreviewFrameSource = source
        holdPreviewFrameUntilUtc = If(previewPictureBox.Image IsNot Nothing, DateTime.UtcNow.AddSeconds(3), DateTime.MinValue)
        Return source
    End Function

    Private Shared Async Function WaitForFirstPreviewFrameAsync(source As TaskCompletionSource(Of Boolean), timeout As TimeSpan) As Task
        If source Is Nothing Then
            Return
        End If

        Dim completedTask = Await Task.WhenAny(source.Task, Task.Delay(timeout))

        If Not Object.ReferenceEquals(completedTask, source.Task) Then
            source.TrySetResult(False)
        End If
    End Function

    Private Sub CompleteFirstPreviewFrameWait(value As Boolean)
        Dim source = playbackFirstPreviewFrameSource
        playbackFirstPreviewFrameSource = Nothing
        holdPreviewFrameUntilUtc = DateTime.MinValue
        source?.TrySetResult(value)
    End Sub

    Private Async Function StartPreviewAsync(filePath As String, startOffset As TimeSpan) As Task
        Await StopPreviewAsync(clearImage:=False)

        Dim ffmpegPath = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe")

        If Not File.Exists(ffmpegPath) Then
            SetStatus($"ffmpeg.exe not found in {AppContext.BaseDirectory}", warning:=True)
            Return
        End If

        Try
            previewStateLabel.Text = $"Previewing {Path.GetFileName(filePath)}"
            previewStateLabel.Visible = True
            SetStatus($"Starting preview: {Path.GetFileName(filePath)}")

            Dim fileHasAudio = Await Task.Run(Function() ProbeHasAudioStream(filePath))
            Dim runner As New PreviewFrameReader()
            previewRunner = runner
            runner.Start(ffmpegPath, BuildPreviewArguments(filePath, fileHasAudio, startOffset, playbackSpeedMultiplier), AppContext.BaseDirectory)
            UpdatePreviewButtons()
        Catch ex As Exception
            previewRunner = Nothing
            CompleteFirstPreviewFrameWait(False)
            previewStateLabel.Text = "Preview unavailable"
            previewStateLabel.Visible = True
            SetStatus($"Unable to start preview: {ex.Message}", warning:=True)
            UpdatePreviewButtons()
        End Try
    End Function

    Private Async Function RenderStillPreviewFrameAsync(filePath As String, startOffset As TimeSpan, requestGeneration As Integer, cancellationToken As CancellationToken) As Task
        Dim ffmpegPath = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe")

        If Not File.Exists(ffmpegPath) Then
            SetStatus($"ffmpeg.exe not found in {AppContext.BaseDirectory}", warning:=True)
            Return
        End If

        Try
            Dim frame = Await Task.Run(Function() CapturePreviewFrame(ffmpegPath, filePath, startOffset, cancellationToken), cancellationToken)

            If frame Is Nothing OrElse IsDisposed OrElse cancellationToken.IsCancellationRequested OrElse requestGeneration <> scrubFrameRequestGeneration Then
                frame?.Dispose()
                Return
            End If

            Dim previousImage = previewPictureBox.Image
            previewPictureBox.Image = frame
            previewStateLabel.Visible = False

            If previousImage IsNot Nothing Then
                previousImage.Dispose()
            End If
        Catch ex As OperationCanceledException
        Catch ex As Exception
            previewStateLabel.Text = "Frame unavailable"
            previewStateLabel.Visible = True
            SetStatus($"Unable to show scrub frame: {ex.Message}", warning:=True)
        End Try
    End Function

    Private Async Function StopPreviewAsync(Optional clearImage As Boolean = False, Optional showStateLabel As Boolean = True) As Task
        Dim runner = previewRunner

        If runner Is Nothing OrElse isStoppingPreview Then
            If clearImage Then
                ClearPreviewImage()
            End If

            Return
        End If

        isStoppingPreview = True
        previewRunner = Nothing

        If showStateLabel Then
            previewStateLabel.Text = "Stopping preview..."
            previewStateLabel.Visible = True
        Else
            previewStateLabel.Visible = False
        End If

        UpdatePreviewButtons()

        Try
            Await Task.Run(Sub() runner.Stop())
            runner.Dispose()
            SetStatus("Preview stopped.")
        Catch ex As Exception
            SetStatus($"Preview stop failed: {ex.Message}", warning:=True)
        Finally
            isStoppingPreview = False

            If clearImage Then
                ClearPreviewImage()
            End If

            If showStateLabel OrElse clearImage Then
                previewStateLabel.Text = "Preview stopped"
                previewStateLabel.Visible = True
            Else
                previewStateLabel.Visible = False
            End If

            UpdatePreviewButtons()
        End Try
    End Function

    Private Async Function StartOutputAsync(filePath As String, startOffset As TimeSpan) As Task
        Dim ffmpegPath = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe")

        If Not File.Exists(ffmpegPath) Then
            SetStatus($"ffmpeg.exe not found in {AppContext.BaseDirectory}", warning:=True)
            Return
        End If

        Dim deviceName = TryCast(outputDeviceComboBox.SelectedItem, String)

        If IsNoDeckLinkOutputDevice(deviceName) Then
            If outputRunner IsNot Nothing Then
                Await StopOutputAsync()
            End If

            SetStatus($"Playing local preview only: {Path.GetFileName(filePath)}")
            Return
        End If

        Dim outputMode = TryCast(outputModeComboBox.SelectedItem, DeckLinkOutputMode)

        If outputMode Is Nothing Then
            SetStatus("Choose a DeckLink output mode first.", warning:=True)
            Return
        End If

        If outputRunner IsNot Nothing Then
            If outputRunnerIsScrubHold Then
                Dim outputKey = BuildScrubDeckLinkOutputKey(filePath, deviceName, outputMode)

                If Not String.Equals(scrubDeckLinkOutputKey, outputKey, StringComparison.OrdinalIgnoreCase) Then
                    Await StopOutputAsync()
                End If
            End If
        End If

        Try
            Dim fileHasAudio = Await Task.Run(Function() ProbeHasAudioStream(filePath))
            Dim runner = outputRunner

            If runner Is Nothing Then
                runner = New InProcessDeckLinkOutputRunner()
                outputRunner = runner
            End If

            outputRunnerIsScrubHold = False
            scrubDeckLinkOutputKey = BuildScrubDeckLinkOutputKey(filePath, deviceName, outputMode)
            lastDeckLinkOutputMessage = String.Empty
            SetStatus($"Starting SDI: {Path.GetFileName(filePath)} -> {deviceName} {outputMode.DisplayName}")
            Await runner.StartPlaybackAsync(ffmpegPath, filePath, deviceName, outputMode.FormatCode, outputMode.Width, outputMode.Height, outputMode.FrameRate, outputMode.IsInterlaced, fileHasAudio, startOffset, playbackSpeedMultiplier)
            SetStatus($"Playing SDI through DeckLink API: {Path.GetFileName(filePath)} -> {deviceName} {outputMode.DisplayName}")
        Catch ex As Exception
            If outputRunner IsNot Nothing Then
                outputRunner.Dispose()
            End If

            outputRunner = Nothing
            outputRunnerIsScrubHold = False
            scrubDeckLinkOutputKey = Nothing
            SetStatus($"Unable to start DeckLink output: {ex.Message}", warning:=True)
        Finally
            UpdatePreviewButtons()
        End Try
    End Function

    Private Async Function StartScrubDeckLinkFrameAsync(filePath As String, startOffset As TimeSpan, requestGeneration As Integer, cancellationToken As CancellationToken) As Task(Of Boolean)
        Dim ffmpegPath = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe")

        If Not File.Exists(ffmpegPath) Then
            SetStatus($"ffmpeg.exe not found in {AppContext.BaseDirectory}", warning:=True)
            Return False
        End If

        Dim deviceName = TryCast(outputDeviceComboBox.SelectedItem, String)

        If IsNoDeckLinkOutputDevice(deviceName) Then
            Return False
        End If

        Dim outputMode = TryCast(outputModeComboBox.SelectedItem, DeckLinkOutputMode)

        If outputMode Is Nothing Then
            SetStatus("Choose a DeckLink output mode first.", warning:=True)
            Return False
        End If

        Dim outputKey = BuildScrubDeckLinkOutputKey(filePath, deviceName, outputMode)
        Dim runner = outputRunner

        If runner IsNot Nothing AndAlso Not String.Equals(scrubDeckLinkOutputKey, outputKey, StringComparison.OrdinalIgnoreCase) Then
            Await StopOutputAsync()
            runner = Nothing
        End If

        If runner Is Nothing Then
            runner = New InProcessDeckLinkOutputRunner()
            outputRunner = runner
        End If

        Try
            cancellationToken.ThrowIfCancellationRequested()
            Await runner.DisplayScrubFrameAsync(ffmpegPath, filePath, deviceName, outputMode.FormatCode, outputMode.Width, outputMode.Height, outputMode.FrameRate, outputMode.IsInterlaced, startOffset, cancellationToken)

            If cancellationToken.IsCancellationRequested OrElse requestGeneration <> scrubFrameRequestGeneration Then
                Return False
            End If

            outputRunnerIsScrubHold = True
            scrubDeckLinkOutputKey = outputKey
            lastDeckLinkOutputMessage = String.Empty
            SetStatus($"DeckLink scrub frame held: {Path.GetFileName(filePath)} -> {deviceName} {outputMode.DisplayName}")
            Return True
        Catch ex As OperationCanceledException
            Return False
        Catch ex As Exception
            runner.Dispose()
            outputRunner = Nothing
            outputRunnerIsScrubHold = False
            scrubDeckLinkOutputKey = Nothing
            SetStatus($"Unable to show DeckLink scrub frame: {ex.Message}", warning:=True)
            Return False
        Finally
            UpdatePreviewButtons()
        End Try
    End Function

    Private Async Function StopOutputAsync() As Task
        Dim runner = outputRunner

        If runner Is Nothing OrElse isStoppingOutput Then
            Return
        End If

        isStoppingOutput = True
        outputRunner = Nothing
        outputRunnerIsScrubHold = False
        scrubDeckLinkOutputKey = Nothing
        SetStatus("Stopping DeckLink output...")
        UpdatePreviewButtons()

        Try
            Await Task.Run(Sub() runner.Stop())
            runner.Dispose()
            SetStatus("DeckLink output stopped.")
        Catch ex As Exception
            SetStatus($"DeckLink output stop failed: {ex.Message}", warning:=True)
        Finally
            isStoppingOutput = False
            UpdatePreviewButtons()
        End Try
    End Function

    Private Async Function StopPlaybackAsync(Optional clearImage As Boolean = False) As Task
        StopPlaybackClock()
        StopShuttlePlaybackTimer()
        scrubPreviewTimer.Stop()
        pendingScrubFrameOffset = Nothing
        pendingScrubFrameShouldUpdateDeckLink = False
        currentScrubFrameCancellation?.Cancel()
        TearDownAudioMonitor(fast:=True)
        Await StopOutputAsync()
        Await StopPreviewAsync(clearImage, showStateLabel:=clearImage)
    End Function

    Private Async Function StopPlaybackForScrubAsync(Optional clearImage As Boolean = False, Optional stopDeckLinkOutput As Boolean = True) As Task
        StopPlaybackClock()
        TearDownAudioMonitor(fast:=True)

        If stopDeckLinkOutput AndAlso Not outputRunnerIsScrubHold Then
            Await StopOutputAsync()
        End If

        Await StopPreviewAsync(clearImage, showStateLabel:=clearImage)
    End Function

    Private Sub StartAudioMonitor(filePath As String, startOffset As TimeSpan, playbackSpeed As Double)
        TearDownAudioMonitor(fast:=True)

        If Not speakerMonitorEnabledValue OrElse String.IsNullOrWhiteSpace(filePath) OrElse Not File.Exists(filePath) OrElse Not ProbeHasAudioStream(filePath) Then
            Return
        End If

        Dim ffplayPath = Path.Combine(AppContext.BaseDirectory, "ffplay.exe")

        If Not File.Exists(ffplayPath) Then
            SetStatus($"ffplay.exe was not found in {AppContext.BaseDirectory}. Player audio listen is unavailable.", warning:=True)
            Return
        End If

        Try
            Dim runner As New FfmpegProcessRunner()
            audioMonitorRunner = runner
            runner.Start(ffplayPath, BuildAudioMonitorArguments(filePath, startOffset, playbackSpeed), AppContext.BaseDirectory)
            SetStatus($"Player audio listen active at {FormatPlaybackSpeed(playbackSpeed)}: {Path.GetFileName(filePath)}")
        Catch ex As Exception
            audioMonitorRunner = Nothing
            SetStatus($"Unable to start player audio listen: {ex.Message}", warning:=True)
        End Try
    End Sub

    Private Sub TearDownAudioMonitor(Optional fast As Boolean = False)
        If audioMonitorRunner Is Nothing Then
            Return
        End If

        Dim runner = audioMonitorRunner
        audioMonitorRunner = Nothing

        If fast Then
            runner.Dispose()
        Else
            runner.Stop()
            runner.Dispose()
        End If
    End Sub

    Private Shared Function BuildAudioMonitorArguments(filePath As String, startOffset As TimeSpan, playbackSpeed As Double) As String
        Dim builder As New StringBuilder("-hide_banner -loglevel warning -nostats -nodisp -autoexit -volume 100 ")
        Dim audioSpeedFilter = BuildAudioSpeedFilterChain(playbackSpeed)

        If startOffset > TimeSpan.Zero AndAlso Not IsImageFile(filePath) Then
            builder.Append("-ss ").Append(FormatFfmpegTimestamp(startOffset)).Append(" ")
        End If

        If Not String.IsNullOrWhiteSpace(audioSpeedFilter) Then
            builder.Append("-af ").Append(Quote(audioSpeedFilter)).Append(" ")
        End If

        builder.Append("-i ").Append(Quote(filePath))
        Return builder.ToString()
    End Function

    Private Shared Function BuildScrubDeckLinkOutputKey(filePath As String, deviceName As String, outputMode As DeckLinkOutputMode) As String
        Return $"{deviceName}|{outputMode.FormatCode}|{outputMode.Width}x{outputMode.Height}|{outputMode.FrameRate}"
    End Function

    Private Shared Function IsNoDeckLinkOutputDevice(deviceName As String) As Boolean
        Return String.IsNullOrWhiteSpace(deviceName) OrElse String.Equals(deviceName, NoDeckLinkOutputText, StringComparison.OrdinalIgnoreCase)
    End Function

    Private Shared Function BuildPreviewArguments(filePath As String, hasAudioStream As Boolean, startOffset As TimeSpan, playbackSpeed As Double) As String
        Dim previewWidth = 900
        Dim previewHeight = 540
        Dim meterChannelWidth = 96
        Dim meterOutputWidth = 30
        Dim normalizedSpeed = Math.Max(0.1R, Math.Abs(NormalizePlaybackSpeed(playbackSpeed)))
        Dim speedNumber = FormatFilterNumber(normalizedSpeed)
        Dim audioSpeedFilter = BuildAudioSpeedFilterChain(normalizedSpeed)
        Dim audioFilterPrefix = If(String.IsNullOrWhiteSpace(audioSpeedFilter), String.Empty, audioSpeedFilter & ",")
        Dim meterRailFilter = BuildAudioMeterRailFilter(meterOutputWidth)
        Dim audioInputLabel = If(hasAudioStream, "[0:a]", "[1:a]")
        Dim rightMeterPan = "mono|c0=c1"
        Dim filterGraph = $"{audioInputLabel}{audioFilterPrefix}aresample=48000,aformat=sample_fmts=s16:channel_layouts=stereo,apad,asetpts=N/SR/TB,asplit=2[left_meter_src][right_meter_src];" &
            $"[0:v]setpts=PTS/{speedNumber},scale={previewWidth}:{previewHeight}:force_original_aspect_ratio=decrease,pad={previewWidth}:{previewHeight}:(ow-iw)/2:(oh-ih)/2,fps=25,setpts=N/(25*TB),format=yuv420p[video];" &
            $"[left_meter_src]pan=mono|c0=c0,showvolume=r=25:w={meterChannelWidth}:h={previewHeight}:f=0.92:b=2:t=0:v=1:dm=1:o=v:ds=log:p=0.18:m=r[left_bar_src];" &
            $"[left_bar_src]scale={meterOutputWidth}:{previewHeight},format=yuv420p,{meterRailFilter}[left_bar];" &
            $"[right_meter_src]pan={rightMeterPan},showvolume=r=25:w={meterChannelWidth}:h={previewHeight}:f=0.92:b=2:t=0:v=1:dm=1:o=v:ds=log:p=0.18:m=r[right_bar_src];" &
            $"[right_bar_src]scale={meterOutputWidth}:{previewHeight},format=yuv420p,{meterRailFilter}[right_bar];" &
            "[left_bar][video][right_bar]hstack=inputs=3:shortest=1[out]"
        Dim builder As New StringBuilder()

        builder.Append("-hide_banner -loglevel warning ")

        If IsImageFile(filePath) Then
            builder.Append("-loop 1 -framerate 25 ")
        ElseIf Math.Abs(normalizedSpeed - 1.0R) < 0.001R Then
            builder.Append("-re ")
        Else
            builder.Append("-readrate ").Append(speedNumber).Append(" ")
        End If

        If startOffset > TimeSpan.Zero AndAlso Not IsImageFile(filePath) Then
            builder.Append("-ss ").Append(FormatFfmpegTimestamp(startOffset)).Append(" ")
        End If

        builder.Append("-i ").Append(Quote(filePath)).Append(" ")
        If Not hasAudioStream Then
            builder.Append("-f lavfi -i anullsrc=channel_layout=stereo:sample_rate=48000 ")
        End If
        builder.Append("-filter_complex ").Append(Quote(filterGraph)).Append(" ")
        builder.Append("-map ").Append(Quote("[out]")).Append(" ")
        builder.Append("-flush_packets 1 -c:v mjpeg -q:v 6 -f mjpeg pipe:1")

        Return builder.ToString()
    End Function

    Private Shared Function CapturePreviewFrame(ffmpegPath As String, filePath As String, startOffset As TimeSpan, cancellationToken As CancellationToken) As Bitmap
        Dim startInfo As New ProcessStartInfo() With {
            .FileName = ffmpegPath,
            .Arguments = BuildStillPreviewFrameArguments(filePath, startOffset),
            .WorkingDirectory = AppContext.BaseDirectory,
            .UseShellExecute = False,
            .RedirectStandardOutput = True,
            .RedirectStandardError = True,
            .CreateNoWindow = True
        }

        Using process As New Process() With {.StartInfo = startInfo}
            cancellationToken.ThrowIfCancellationRequested()

            If Not process.Start() Then
                Return Nothing
            End If

            Using cancellationToken.Register(Sub() TryKillProcess(process))
            Using memory As New MemoryStream()
                Dim outputTask = process.StandardOutput.BaseStream.CopyToAsync(memory, cancellationToken)
                Dim errorTask = process.StandardError.ReadToEndAsync(cancellationToken)
                Dim waitStartedAt = DateTime.UtcNow

                While Not process.WaitForExit(25)
                    cancellationToken.ThrowIfCancellationRequested()

                    If DateTime.UtcNow - waitStartedAt >= TimeSpan.FromSeconds(5) Then
                        Exit While
                    End If
                End While

                If Not process.HasExited Then
                    process.Kill(True)

                    Try
                        process.WaitForExit(1000)
                    Catch
                    End Try

                    Try
                        outputTask.GetAwaiter().GetResult()
                    Catch
                    End Try

                    Try
                        errorTask.GetAwaiter().GetResult()
                    Catch
                    End Try

                    Return Nothing
                End If

                cancellationToken.ThrowIfCancellationRequested()
                outputTask.GetAwaiter().GetResult()
                errorTask.GetAwaiter().GetResult()

                If process.ExitCode <> 0 OrElse memory.Length = 0 Then
                    Return Nothing
                End If

                memory.Position = 0

                Using sourceImage = Image.FromStream(memory)
                    Return New Bitmap(sourceImage)
                End Using
            End Using
            End Using
        End Using
    End Function

    Private Shared Sub TryKillProcess(process As Process)
        Try
            If process IsNot Nothing AndAlso Not process.HasExited Then
                process.Kill(True)
            End If
        Catch
        End Try
    End Sub

    Private Shared Function BuildStillPreviewFrameArguments(filePath As String, startOffset As TimeSpan) As String
        Dim previewWidth = 900
        Dim previewHeight = 540
        Dim meterChannelWidth = 96
        Dim meterOutputWidth = 30
        Dim meterRailFilter = BuildAudioMeterRailFilter(meterOutputWidth)
        Dim rightMeterPan = "mono|c0=c1"
        Dim stillPreviewFilter = $"[1:a]aresample=48000,aformat=sample_fmts=s16:channel_layouts=stereo,asetpts=N/SR/TB,asplit=2[left_meter_src][right_meter_src];" &
            $"[0:v]scale={previewWidth}:{previewHeight}:force_original_aspect_ratio=decrease,pad={previewWidth}:{previewHeight}:(ow-iw)/2:(oh-ih)/2,format=yuv420p[video];" &
            $"[left_meter_src]pan=mono|c0=c0,showvolume=r=25:w={meterChannelWidth}:h={previewHeight}:f=0.92:b=2:t=0:v=1:dm=1:o=v:ds=log:p=0.18:m=r[left_bar_src];" &
            $"[left_bar_src]scale={meterOutputWidth}:{previewHeight},format=yuv420p,{meterRailFilter}[left_bar];" &
            $"[right_meter_src]pan={rightMeterPan},showvolume=r=25:w={meterChannelWidth}:h={previewHeight}:f=0.92:b=2:t=0:v=1:dm=1:o=v:ds=log:p=0.18:m=r[right_bar_src];" &
            $"[right_bar_src]scale={meterOutputWidth}:{previewHeight},format=yuv420p,{meterRailFilter}[right_bar];" &
            "[left_bar][video][right_bar]hstack=inputs=3:shortest=1[out]"
        Dim builder As New StringBuilder("-hide_banner -loglevel error ")

        If startOffset > TimeSpan.Zero AndAlso Not IsImageFile(filePath) Then
            builder.Append("-ss ").Append(FormatFfmpegTimestamp(startOffset)).Append(" ")
        End If

        If IsImageFile(filePath) Then
            builder.Append("-loop 1 ")
        End If

        builder.Append("-i ").Append(Quote(filePath)).Append(" ")
        builder.Append("-f lavfi -i anullsrc=channel_layout=stereo:sample_rate=48000 ")
        builder.Append("-filter_complex ").Append(Quote(stillPreviewFilter)).Append(" ")
        builder.Append("-map ").Append(Quote("[out]")).Append(" ")
        builder.Append("-frames:v 1 ")
        builder.Append("-an -c:v mjpeg -q:v 3 -f image2pipe pipe:1")
        Return builder.ToString()
    End Function

    Private Shared Function BuildAudioMeterRailFilter(meterOutputWidth As Integer) As String
        Return "drawbox=x=0:y=0:w=iw:h=ih:color=0x56616d:t=2"
    End Function

    Private Shared Function ProbeHasAudioStream(filePath As String) As Boolean
        Dim ffprobePath = Path.Combine(AppContext.BaseDirectory, "ffprobe.exe")

        If Not File.Exists(ffprobePath) OrElse String.IsNullOrWhiteSpace(filePath) OrElse Not File.Exists(filePath) Then
            Return False
        End If

        Try
            Dim startInfo As New ProcessStartInfo() With {
                .FileName = ffprobePath,
                .Arguments = $"-v error -select_streams a:0 -show_entries stream=index -of csv=p=0 {Quote(filePath)}",
                .WorkingDirectory = AppContext.BaseDirectory,
                .UseShellExecute = False,
                .RedirectStandardOutput = True,
                .RedirectStandardError = True,
                .CreateNoWindow = True
            }

            Using process As New Process() With {.StartInfo = startInfo}
                If Not process.Start() Then
                    Return False
                End If

                Dim output = process.StandardOutput.ReadToEnd()

                If Not process.WaitForExit(2500) Then
                    process.Kill(True)
                    Return False
                End If

                Return process.ExitCode = 0 AndAlso Not String.IsNullOrWhiteSpace(output)
            End Using
        Catch
            Return False
        End Try
    End Function

    Private Sub previewRunner_FrameReady(frame As Bitmap) Handles previewRunner.FrameReady
        If IsDisposed Then
            frame.Dispose()
            Return
        End If

        If InvokeRequired Then
            BeginInvoke(New Action(Of Bitmap)(AddressOf previewRunner_FrameReady), frame)
            Return
        End If

        If ShouldHoldExistingPreviewFrame(frame) Then
            frame.Dispose()
            Return
        End If

        Dim previousImage = previewPictureBox.Image
        previewPictureBox.Image = frame
        previewStateLabel.Visible = False
        CompleteFirstPreviewFrameWait(True)

        If previousImage IsNot Nothing Then
            previousImage.Dispose()
        End If
    End Sub

    Private Sub previewRunner_LogReceived(message As String) Handles previewRunner.LogReceived
        If String.IsNullOrWhiteSpace(message) OrElse IsDisposed Then
            Return
        End If

        If InvokeRequired Then
            BeginInvoke(New Action(Of String)(AddressOf previewRunner_LogReceived), message)
            Return
        End If

        SetStatus(message, warning:=message.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0)
    End Sub

    Private Sub previewRunner_Exited(exitCode As Integer) Handles previewRunner.Exited
        If IsDisposed Then
            Return
        End If

        If InvokeRequired Then
            BeginInvoke(New Action(Of Integer)(AddressOf previewRunner_Exited), exitCode)
            Return
        End If

        previewRunner = Nothing
        CompleteFirstPreviewFrameWait(False)
        previewStateLabel.Text = If(exitCode = 0, "Preview stopped", $"Preview stopped (Exit {exitCode})")
        previewStateLabel.Visible = True
        SetStatus(previewStateLabel.Text, warning:=exitCode <> 0)
        UpdatePreviewButtons()
    End Sub

    Private Function ShouldHoldExistingPreviewFrame(frame As Bitmap) As Boolean
        If playbackFirstPreviewFrameSource Is Nothing OrElse previewPictureBox.Image Is Nothing Then
            Return False
        End If

        If DateTime.UtcNow >= holdPreviewFrameUntilUtc Then
            Return False
        End If

        Return IsMostlyBlackVideoFrame(frame)
    End Function

    Private Shared Function IsMostlyBlackVideoFrame(frame As Bitmap) As Boolean
        If frame Is Nothing OrElse frame.Width <= 0 OrElse frame.Height <= 0 Then
            Return False
        End If

        Dim left = Math.Max(0, CInt(Math.Floor(frame.Width * 0.08R)))
        Dim right = Math.Min(frame.Width - 1, CInt(Math.Ceiling(frame.Width * 0.92R)))
        Dim top = Math.Max(0, CInt(Math.Floor(frame.Height * 0.08R)))
        Dim bottom = Math.Min(frame.Height - 1, CInt(Math.Ceiling(frame.Height * 0.92R)))
        Dim xStep = Math.Max(1, (right - left) \ 24)
        Dim yStep = Math.Max(1, (bottom - top) \ 14)
        Dim samples = 0
        Dim nonBlackSamples = 0

        For y = top To bottom Step yStep
            For x = left To right Step xStep
                Dim pixel = frame.GetPixel(x, y)
                samples += 1

                If pixel.R > 24 OrElse pixel.G > 24 OrElse pixel.B > 24 Then
                    nonBlackSamples += 1
                End If
            Next
        Next

        Return samples > 0 AndAlso nonBlackSamples <= Math.Max(2, samples \ 20)
    End Function

    Private Sub outputRunner_LogReceived(message As String) Handles outputRunner.LogReceived
        If String.IsNullOrWhiteSpace(message) OrElse IsDisposed Then
            Return
        End If

        If InvokeRequired Then
            BeginInvoke(New Action(Of String)(AddressOf outputRunner_LogReceived), message)
            Return
        End If

        If IsDeckLinkOutputWarning(message) Then
            lastDeckLinkOutputMessage = message
            SetStatus($"DeckLink: {message}", warning:=True)
        End If
    End Sub

    Private Sub outputRunner_Exited(exitCode As Integer) Handles outputRunner.Exited
        If IsDisposed Then
            Return
        End If

        If InvokeRequired Then
            BeginInvoke(New Action(Of Integer)(AddressOf outputRunner_Exited), exitCode)
            Return
        End If

        outputRunner = Nothing
        outputRunnerIsScrubHold = False
        scrubDeckLinkOutputKey = Nothing
        Dim finalMessage = If(exitCode = 0,
            "DeckLink output stopped.",
            If(String.IsNullOrWhiteSpace(lastDeckLinkOutputMessage),
                $"DeckLink output stopped (Exit {exitCode}).",
                $"DeckLink output failed: {lastDeckLinkOutputMessage}"))

        lastDeckLinkOutputMessage = String.Empty
        SetStatus(finalMessage, warning:=exitCode <> 0)
        UpdatePreviewButtons()
    End Sub

    Private Sub outputRunner_PlaybackEnded(exitCode As Integer) Handles outputRunner.PlaybackEnded
        If IsDisposed Then
            Return
        End If

        If InvokeRequired Then
            BeginInvoke(New Action(Of Integer)(AddressOf outputRunner_PlaybackEnded), exitCode)
            Return
        End If

        outputRunnerIsScrubHold = True

        Dim deviceName = TryCast(outputDeviceComboBox.SelectedItem, String)
        Dim outputMode = TryCast(outputModeComboBox.SelectedItem, DeckLinkOutputMode)
        If Not IsNoDeckLinkOutputDevice(deviceName) AndAlso outputMode IsNot Nothing Then
            scrubDeckLinkOutputKey = BuildScrubDeckLinkOutputKey(String.Empty, deviceName, outputMode)
        Else
            scrubDeckLinkOutputKey = Nothing
        End If

        lastDeckLinkOutputMessage = String.Empty
        SetStatus("DeckLink playback ended. Holding last SDI frame.")
        UpdatePreviewButtons()
    End Sub

    Private Sub audioMonitorRunner_LogReceived(message As String) Handles audioMonitorRunner.LogReceived
        If String.IsNullOrWhiteSpace(message) OrElse IsDisposed Then
            Return
        End If

        If InvokeRequired Then
            BeginInvoke(New Action(Of String)(AddressOf audioMonitorRunner_LogReceived), message)
            Return
        End If

        If message.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0 OrElse
           message.IndexOf("failed", StringComparison.OrdinalIgnoreCase) >= 0 Then
            SetStatus($"Player audio listen: {message}", warning:=True)
        End If
    End Sub

    Private Sub audioMonitorRunner_Exited(exitCode As Integer) Handles audioMonitorRunner.Exited
        If IsDisposed Then
            Return
        End If

        If InvokeRequired Then
            BeginInvoke(New Action(Of Integer)(AddressOf audioMonitorRunner_Exited), exitCode)
            Return
        End If

        audioMonitorRunner = Nothing
    End Sub

    Private Sub UpdatePreviewButtons()
        Dim hasSelectedFile = Not String.IsNullOrWhiteSpace(GetSelectedFilePath())
        Dim isPreviewRunning = previewRunner IsNot Nothing
        Dim isOutputRunning = outputRunner IsNot Nothing
        Dim isShuttleRunning = shuttlePlaybackActive
        Dim isBlockingOutputRunning = isOutputRunning AndAlso Not outputRunnerIsScrubHold
        previewButton.Enabled = hasSelectedFile AndAlso Not isLoadingFiles AndAlso Not isPreviewRunning AndAlso Not isBlockingOutputRunning AndAlso Not isShuttleRunning AndAlso Not isStoppingPreview AndAlso Not isStoppingOutput AndAlso Not isSeekingPlayback
        stopPreviewButton.Enabled = (isPreviewRunning OrElse isOutputRunning OrElse isShuttleRunning) AndAlso Not isStoppingPreview AndAlso Not isStoppingOutput AndAlso Not isSeekingPlayback
        outputDeviceComboBox.Enabled = Not isOutputRunning AndAlso Not isStoppingOutput AndAlso Not isSeekingPlayback
        outputModeComboBox.Enabled = Not isOutputRunning AndAlso Not isStoppingOutput AndAlso Not isSeekingPlayback
        speedTrackBar.Enabled = Not isLoadingFiles AndAlso Not isStoppingPreview AndAlso Not isStoppingOutput

        For Each button In speedPresetButtons
            button.Enabled = speedTrackBar.Enabled
        Next
    End Sub

    Private Function IsPlaybackActive() As Boolean
        Return playbackPositionTimer.Enabled OrElse shuttlePlaybackActive OrElse previewRunner IsNot Nothing OrElse (outputRunner IsNot Nothing AndAlso Not outputRunnerIsScrubHold)
    End Function

    Private Sub UpdateSpeedControls()
        Dim speed = NormalizePlaybackSpeed(playbackSpeedMultiplier)
        Dim sliderValue = CInt(Math.Round(speed * 10.0R, MidpointRounding.AwayFromZero))
        sliderValue = Math.Max(speedTrackBar.Minimum, Math.Min(speedTrackBar.Maximum, sliderValue))

        isUpdatingSpeedControls = True

        Try
            If speedTrackBar.Value <> sliderValue Then
                speedTrackBar.Value = sliderValue
            End If

            speedValueLabel.Text = FormatPlaybackSpeed(speed)
        Finally
            isUpdatingSpeedControls = False
        End Try

        For Each button In speedPresetButtons
            Dim buttonSpeed = NormalizePlaybackSpeed(Convert.ToDouble(button.Tag, CultureInfo.InvariantCulture))
            Dim selected = Math.Abs(buttonSpeed - speed) < 0.001R
            button.UseVisualStyleBackColor = Not darkModeEnabledValue AndAlso Not selected
            button.BackColor = If(selected,
                If(darkModeEnabledValue, Color.FromArgb(73, 102, 126), Color.FromArgb(194, 223, 241)),
                If(darkModeEnabledValue, Color.FromArgb(62, 67, 74), SystemColors.Control))
            button.ForeColor = If(darkModeEnabledValue, Color.FromArgb(236, 239, 242), Color.FromArgb(44, 52, 60))

            If darkModeEnabledValue Then
                button.FlatStyle = FlatStyle.Flat
                button.FlatAppearance.BorderColor = If(selected, Color.FromArgb(124, 168, 199), Color.FromArgb(90, 96, 104))
            End If
        Next
    End Sub

    Private Sub SetStatus(message As String, Optional warning As Boolean = False)
        statusLabel.Text = message
        statusLabel.ForeColor = If(warning, Color.FromArgb(232, 181, 105), If(darkModeEnabledValue, Color.FromArgb(190, 198, 207), Color.FromArgb(72, 82, 92)))
    End Sub

    Private Shared Function IsDeckLinkOutputWarning(message As String) As Boolean
        Return message.IndexOf("error", StringComparison.OrdinalIgnoreCase) >= 0 OrElse
            message.IndexOf("failed", StringComparison.OrdinalIgnoreCase) >= 0 OrElse
            message.IndexOf("could not", StringComparison.OrdinalIgnoreCase) >= 0 OrElse
            message.IndexOf("unsupported", StringComparison.OrdinalIgnoreCase) >= 0 OrElse
            message.IndexOf("i/o error", StringComparison.OrdinalIgnoreCase) >= 0 OrElse
            message.IndexOf("invalid", StringComparison.OrdinalIgnoreCase) >= 0
    End Function

    Private Sub ApplyTheme()
        Dim background = If(darkModeEnabledValue, Color.FromArgb(35, 38, 43), Color.FromArgb(244, 240, 232))
        Dim panelBackground = If(darkModeEnabledValue, Color.FromArgb(48, 52, 58), Color.FromArgb(250, 247, 240))
        Dim inputBackground = If(darkModeEnabledValue, Color.FromArgb(28, 31, 36), Color.White)
        Dim foreground = If(darkModeEnabledValue, Color.FromArgb(236, 239, 242), Color.FromArgb(44, 52, 60))
        Dim secondaryForeground = If(darkModeEnabledValue, Color.FromArgb(190, 198, 207), Color.FromArgb(72, 82, 92))

        BackColor = background
        ForeColor = foreground
        rootLayout.BackColor = background
        toolbarPanel.BackColor = background
        outputPanel.BackColor = background
        previewToolbarPanel.BackColor = background
        previewStateHostPanel.BackColor = background
        speedButtonsPanel.BackColor = background
        speedSeekPanel.BackColor = background
        previewPanel.BackColor = background
        browserSplit.BackColor = background

        folderLabel.ForeColor = foreground
        outputDeviceLabel.ForeColor = foreground
        outputModeLabel.ForeColor = foreground
        previewStateLabel.ForeColor = secondaryForeground
        selectedFileLabel.ForeColor = secondaryForeground
        speedValueLabel.ForeColor = secondaryForeground
        statusLabel.ForeColor = secondaryForeground

        rootPathTextBox.BackColor = inputBackground
        rootPathTextBox.ForeColor = foreground
        outputDeviceComboBox.BackColor = inputBackground
        outputDeviceComboBox.ForeColor = foreground
        outputModeComboBox.BackColor = inputBackground
        outputModeComboBox.ForeColor = foreground

        folderTreeView.BackColor = inputBackground
        folderTreeView.ForeColor = foreground
        folderTreeView.LineColor = secondaryForeground

        filesGridView.BackgroundColor = inputBackground
        filesGridView.GridColor = If(darkModeEnabledValue, Color.FromArgb(70, 76, 84), Color.FromArgb(205, 210, 214))
        filesGridView.DefaultCellStyle.BackColor = inputBackground
        filesGridView.DefaultCellStyle.ForeColor = foreground
        filesGridView.DefaultCellStyle.SelectionBackColor = If(darkModeEnabledValue, Color.FromArgb(62, 83, 104), Color.FromArgb(197, 220, 239))
        filesGridView.DefaultCellStyle.SelectionForeColor = If(darkModeEnabledValue, Color.White, Color.Black)
        filesGridView.ColumnHeadersDefaultCellStyle.BackColor = If(darkModeEnabledValue, Color.FromArgb(55, 60, 67), Color.FromArgb(231, 235, 238))
        filesGridView.ColumnHeadersDefaultCellStyle.ForeColor = foreground
        filesGridView.EnableHeadersVisualStyles = Not darkModeEnabledValue

        StyleButton(refreshButton, foreground)
        StyleButton(openFolderButton, foreground)
        StyleButton(previewButton, foreground)
        StyleButton(stopPreviewButton, foreground)
        For Each button In speedPresetButtons
            StyleButton(button, foreground)
        Next
        UpdateSpeedControls()
    End Sub

    Private Sub StyleButton(button As Button, foreground As Color)
        button.UseVisualStyleBackColor = Not darkModeEnabledValue
        button.BackColor = If(darkModeEnabledValue, Color.FromArgb(62, 67, 74), SystemColors.Control)
        button.ForeColor = foreground
        button.FlatStyle = If(darkModeEnabledValue, FlatStyle.Flat, FlatStyle.Standard)

        If darkModeEnabledValue Then
            button.FlatAppearance.BorderColor = Color.FromArgb(90, 96, 104)
        End If
    End Sub

    Private Sub ClearPreviewImage()
        Dim previousImage = previewPictureBox.Image
        previewPictureBox.Image = Nothing

        If previousImage IsNot Nothing Then
            previousImage.Dispose()
        End If
    End Sub

    Private Shared Function IsSupportedMediaFile(filePath As String) As Boolean
        Return MediaExtensions.Contains(Path.GetExtension(filePath))
    End Function

    Private Shared Function IsImageFile(filePath As String) As Boolean
        Return ImageExtensions.Contains(Path.GetExtension(filePath))
    End Function

    Private Shared Function IsIgnoredDirectory(directoryPath As String) As Boolean
        Dim folderName = Path.GetFileName(directoryPath)

        Return String.Equals(folderName, ".ffmbc-temp", StringComparison.OrdinalIgnoreCase)
    End Function

    Private Shared Function FormatBytes(byteCount As Long) As String
        If byteCount < 1024L Then
            Return $"{byteCount} B"
        End If

        Dim units = {"KB", "MB", "GB", "TB"}
        Dim value = byteCount / 1024.0R
        Dim unitIndex = 0

        While value >= 1024.0R AndAlso unitIndex < units.Length - 1
            value /= 1024.0R
            unitIndex += 1
        End While

        Return $"{value:0.##} {units(unitIndex)}"
    End Function

    Private Shared Function Quote(value As String) As String
        Return """" & value.Replace("""", """""") & """"
    End Function

    Private Shared Function FormatFfmpegTimestamp(value As TimeSpan) As String
        Return value.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)
    End Function

    Private Shared Function FormatFilterSeconds(value As TimeSpan) As String
        Return value.TotalSeconds.ToString("0.######", CultureInfo.InvariantCulture)
    End Function

    Protected Overrides Sub Dispose(disposing As Boolean)
        If disposing Then
            Dim runner = previewRunner
            Dim deckLinkRunner = outputRunner
            Dim audioRunner = audioMonitorRunner
            previewRunner = Nothing
            outputRunner = Nothing
            audioMonitorRunner = Nothing
            scrubPreviewTimer.Stop()
            playbackPositionTimer.Stop()
            scrubPreviewTimer.Dispose()
            playbackPositionTimer.Dispose()

            If runner IsNot Nothing Then
                runner.Dispose()
            End If

            If deckLinkRunner IsNot Nothing Then
                deckLinkRunner.Dispose()
            End If

            If audioRunner IsNot Nothing Then
                audioRunner.Dispose()
            End If

            ClearPreviewImage()
        End If

        MyBase.Dispose(disposing)
    End Sub
End Class
