Imports System.Buffers.Binary
Imports System.Diagnostics
Imports System.Drawing
Imports System.Drawing.Imaging
Imports System.IO
Imports System.Collections.Generic
Imports System.ComponentModel
Imports System.Globalization
Imports System.Linq
Imports System.Runtime.InteropServices
Imports System.Text
Imports System.Threading
Imports System.Threading.Tasks

Public Class DeckLinkPlayerControl
    Inherits UserControl

    Private Const LoadingNodeText As String = "Loading..."
    Private Const NoDeckLinkOutputText As String = "None"
    Private Const DefaultDeckLinkOutputDeviceName As String = "DeckLink SDI 4K"
    Private Const DurationDisplayFrameRate As Integer = 25
    Private Const SeekPreviewBurstFrameCount As Integer = 6
    Private Const SeekPreviewProxyWidth As Integer = 640
    Private Const SeekPreviewProxyHeight As Integer = 360
    Private Const SeekPreviewFrameDelayMs As Integer = 12
    Private Const ReverseAudioChannels As Integer = 2
    Private Const GrowingDurationRefreshIntervalMs As Integer = 2000
    Private Const EmptyMarkText As String = "--:--:--:--"
    Private Shared ReadOnly GrowingFileRecentWriteWindow As TimeSpan = TimeSpan.FromSeconds(90)

    Private Shared ReadOnly SeekPreviewBurstFrameInterval As TimeSpan = TimeSpan.FromSeconds(1.0R / DurationDisplayFrameRate)

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
        New DeckLinkOutputMode("4K 2160p25", "4k25", 3840, 2160, "25", False),
        New DeckLinkOutputMode("4K 2160p50", "4k50", 3840, 2160, "50", False),
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
    Private ReadOnly searchLabel As New Label()
    Private ReadOnly searchTextBox As New TextBox()
    Private ReadOnly clearSearchButton As New Button()
    Private ReadOnly currentFolderFiles As New List(Of FileInfo)()
    Private ReadOnly browserSplit As New SplitContainer()
    Private ReadOnly folderTreeView As New TreeView()
    Private ReadOnly filesGridView As New DataGridView()
    Private ReadOnly filesContextMenu As New ContextMenuStrip()
    Private ReadOnly filesMenuPlay As New ToolStripMenuItem("Play")
    Private ReadOnly filesMenuPlayInVlc As New ToolStripMenuItem("Play in VLC")
    Private ReadOnly filesMenuFileInfo As New ToolStripMenuItem("File Information")
    Private ReadOnly filesMenuOpenFolder As New ToolStripMenuItem("Open Containing Folder")
    Private ReadOnly previewPanel As New TableLayoutPanel()
    Private ReadOnly previewToolbarPanel As New FlowLayoutPanel()
    Private ReadOnly previewButton As New Button()
    Private ReadOnly stopPreviewButton As New Button()
    Private ReadOnly fullPreviewButton As New Button()
    Private ReadOnly loopCheckBox As New CheckBox()
    Private fullscreenPreviewForm As PreviewFullscreenForm
    Private ReadOnly selectedFileLabel As New Label()
    Private ReadOnly previewStateHostPanel As New Panel()
    Private ReadOnly speedButtonsPanel As New FlowLayoutPanel()
    Private ReadOnly markControlsPanel As New FlowLayoutPanel()
    Private ReadOnly markInButton As New Button()
    Private ReadOnly markOutButton As New Button()
    Private ReadOnly markInTextBox As New TextBox()
    Private ReadOnly markOutTextBox As New TextBox()
    Private ReadOnly gotoInButton As New Button()
    Private ReadOnly gotoOutButton As New Button()
    Private ReadOnly playFromInButton As New Button()
    Private ReadOnly playFromOutButton As New Button()
    Private ReadOnly previewPictureBox As New PictureBox()
    Private ReadOnly previewStateLabel As New Label()
    Private ReadOnly scrubberPanel As New TableLayoutPanel()
    Private ReadOnly scrubberTrackBar As New TrackBar()
    Private ReadOnly scrubberTimeLabel As New Label()
    Private ReadOnly playbackPositionTimer As New System.Windows.Forms.Timer()
    Private ReadOnly scrubPreviewTimer As New System.Windows.Forms.Timer()
    Private ReadOnly shuttlePlaybackTimer As New System.Windows.Forms.Timer()
    Private ReadOnly growingDurationRefreshTimer As New System.Windows.Forms.Timer()
    Private ReadOnly statusLabel As New Label()
    Private ReadOnly speedPresetButtons As New List(Of Button)()
    Private ReadOnly markButtons As New List(Of Button)()

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
    Private reversePlaybackFrameCarry As Double
    Private reversePlaybackLastTickAtUtc As DateTime?
    Private reversePlaybackSeekRunning As Boolean
    Private reversePlaybackCancellation As CancellationTokenSource
    Private reversePlaybackTask As Task
    Private reversePreviewCache As ReverseFrameCache
    Private reverseDeckLinkCache As ReverseFrameCache
    Private scrubPreviewCache As ScrubPreviewFrameCache
    Private reverseAudio As ReverseAudioChunkQueue
    Private reverseDeckLinkAudioOutput As ReverseDeckLinkAudioOutput
    Private reverseWaveAudioOutput As ReverseWaveOutAudioOutput
    Private reverseDeckLinkAudioEnabled As Boolean
    Private reverseAudioLeftDbfs As Double = -90.0R
    Private reverseAudioRightDbfs As Double = -90.0R
    Private pendingScrubFrameOffset As TimeSpan?
    Private pendingScrubFrameShouldUpdateDeckLink As Boolean
    Private scrubFrameRequestGeneration As Integer
    Private currentScrubFrameCancellation As CancellationTokenSource
    Private lastScrubPreviewOffset As TimeSpan?
    Private markInPosition As TimeSpan?
    Private markOutPosition As TimeSpan?
    Private isGrowingDurationRefreshRunning As Boolean
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
        filesContextMenu.Items.Add(filesMenuPlay)
        filesContextMenu.Items.Add(filesMenuPlayInVlc)
        filesContextMenu.Items.Add(New ToolStripSeparator())
        filesContextMenu.Items.Add(filesMenuFileInfo)
        filesContextMenu.Items.Add(filesMenuOpenFolder)
        filesGridView.ContextMenuStrip = filesContextMenu

        AddHandler filesGridView.CellMouseDown, AddressOf OnFilesGridCellMouseDown
        AddHandler filesContextMenu.Opening, AddressOf OnFilesContextMenuOpening
        AddHandler filesMenuPlay.Click, AddressOf OnFilesMenuPlayClick
        AddHandler filesMenuPlayInVlc.Click, AddressOf OnFilesMenuPlayInVlcClick
        AddHandler filesMenuFileInfo.Click, AddressOf OnFilesMenuFileInfoClick
        AddHandler filesMenuOpenFolder.Click, AddressOf OnFilesMenuOpenFolderClick

        AddHandler filesGridView.SelectionChanged, AddressOf OnFilesGridSelectionChanged
        AddHandler filesGridView.CellDoubleClick, AddressOf OnFilesGridCellDoubleClick
        AddHandler filesGridView.KeyDown, AddressOf OnFilesGridKeyDown
        AddHandler previewButton.Click, AddressOf OnPreviewClicked
        AddHandler stopPreviewButton.Click, AddressOf OnStopPreviewClicked
        AddHandler fullPreviewButton.Click, AddressOf OnFullPreviewClicked
        AddHandler loopCheckBox.CheckedChanged, AddressOf OnLoopCheckedChanged
        AddHandler markInButton.Click, AddressOf OnMarkInClicked
        AddHandler markOutButton.Click, AddressOf OnMarkOutClicked
        AddHandler gotoInButton.Click, AddressOf OnGotoInClicked
        AddHandler gotoOutButton.Click, AddressOf OnGotoOutClicked
        AddHandler playFromInButton.Click, AddressOf OnPlayFromInClicked
        AddHandler playFromOutButton.Click, AddressOf OnPlayFromOutClicked
        AddHandler markInTextBox.Leave, AddressOf OnMarkFieldLeave
        AddHandler markOutTextBox.Leave, AddressOf OnMarkFieldLeave
        AddHandler markInTextBox.KeyDown, AddressOf OnMarkFieldKeyDown
        AddHandler markOutTextBox.KeyDown, AddressOf OnMarkFieldKeyDown
        AddHandler outputDeviceComboBox.SelectedIndexChanged, AddressOf OnOutputSelectionChanged
        AddHandler outputModeComboBox.SelectedIndexChanged, AddressOf OnOutputSelectionChanged
        AddHandler searchTextBox.TextChanged, AddressOf OnSearchTextChanged
        AddHandler searchTextBox.KeyDown, AddressOf OnSearchTextBoxKeyDown
        AddHandler clearSearchButton.Click, AddressOf OnClearSearchClicked
        AddHandler scrubberTrackBar.MouseDown, AddressOf OnScrubberMouseDown
        AddHandler scrubberTrackBar.MouseMove, AddressOf OnScrubberMouseMove
        AddHandler scrubberTrackBar.MouseUp, AddressOf OnScrubberMouseUp
        AddHandler scrubberTrackBar.Scroll, AddressOf OnScrubberScrolled
        AddHandler scrubberTrackBar.KeyUp, AddressOf OnScrubberKeyUp
        AddHandler playbackPositionTimer.Tick, AddressOf OnPlaybackPositionTimerTick
        scrubPreviewTimer.Interval = 40
        AddHandler scrubPreviewTimer.Tick, AddressOf OnScrubPreviewTimerTick
        shuttlePlaybackTimer.Interval = 80
        AddHandler shuttlePlaybackTimer.Tick, AddressOf OnShuttlePlaybackTimerTick
        growingDurationRefreshTimer.Interval = GrowingDurationRefreshIntervalMs
        AddHandler growingDurationRefreshTimer.Tick, AddressOf OnGrowingDurationRefreshTimerTick
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
            Dim selectedNode = folderTreeView.SelectedNode
            If selectedNode IsNot Nothing Then
                LoadDirectoryChildren(selectedNode)
                selectedNode.Expand()
            Else
                LoadDirectoryChildren(folderTreeView.Nodes(0))
                folderTreeView.Nodes(0).Expand()
            End If
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
        rootLayout.RowStyles.Add(New RowStyle(SizeType.Absolute, 166.0F))
        rootLayout.RowStyles.Add(New RowStyle(SizeType.Absolute, 558.0F))
        rootLayout.RowStyles.Add(New RowStyle(SizeType.Absolute, 24.0F))
        rootLayout.Size = New Size(760, 832)

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

        outputPanel.ColumnCount = 8
        outputPanel.ColumnStyles.Add(New ColumnStyle(SizeType.AutoSize))
        outputPanel.ColumnStyles.Add(New ColumnStyle(SizeType.Absolute, 142.0F))
        outputPanel.ColumnStyles.Add(New ColumnStyle(SizeType.AutoSize))
        outputPanel.ColumnStyles.Add(New ColumnStyle(SizeType.Absolute, 88.0F))
        outputPanel.ColumnStyles.Add(New ColumnStyle(SizeType.AutoSize))
        outputPanel.ColumnStyles.Add(New ColumnStyle(SizeType.Absolute, 190.0F))
        outputPanel.ColumnStyles.Add(New ColumnStyle(SizeType.Absolute, 50.0F))
        outputPanel.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100.0F))
        outputPanel.Dock = DockStyle.Fill
        outputPanel.Margin = New Padding(0, 0, 0, 6)
        outputPanel.RowCount = 1
        outputPanel.RowStyles.Add(New RowStyle(SizeType.Percent, 100.0F))

        outputDeviceLabel.AutoSize = True
        outputDeviceLabel.Dock = DockStyle.Fill
        outputDeviceLabel.Margin = New Padding(0, 4, 6, 0)
        outputDeviceLabel.Text = "SDI Out"
        outputDeviceLabel.TextAlign = ContentAlignment.MiddleLeft

        outputDeviceComboBox.Dock = DockStyle.Fill
        outputDeviceComboBox.DropDownStyle = ComboBoxStyle.DropDownList
        outputDeviceComboBox.Margin = New Padding(0, 0, 10, 0)

        outputModeLabel.AutoSize = True
        outputModeLabel.Dock = DockStyle.Fill
        outputModeLabel.Margin = New Padding(0, 4, 6, 0)
        outputModeLabel.Text = "Mode"
        outputModeLabel.TextAlign = ContentAlignment.MiddleLeft

        outputModeComboBox.Dock = DockStyle.Fill
        outputModeComboBox.DropDownStyle = ComboBoxStyle.DropDownList
        outputModeComboBox.Margin = New Padding(0, 0, 10, 0)
        outputModeComboBox.Items.AddRange(OutputModes.Cast(Of Object)().ToArray())
        Dim savedOutputModeName = GetSavedDeckLinkOutputModeName()
        Dim savedOutputMode = OutputModes.FirstOrDefault(Function(outputMode) String.Equals(outputMode.DisplayName, savedOutputModeName, StringComparison.OrdinalIgnoreCase))
        outputModeComboBox.SelectedItem = If(savedOutputMode, OutputModes(0))

        searchLabel.AutoSize = True
        searchLabel.Dock = DockStyle.Fill
        searchLabel.Margin = New Padding(0, 4, 6, 0)
        searchLabel.Text = "Search"
        searchLabel.TextAlign = ContentAlignment.MiddleLeft

        searchTextBox.Dock = DockStyle.Fill
        searchTextBox.Margin = New Padding(0, 0, 4, 0)
        searchTextBox.PlaceholderText = "Search files..."

        clearSearchButton.Dock = DockStyle.Fill
        clearSearchButton.Margin = New Padding(0)
        clearSearchButton.Text = "Clear"
        clearSearchButton.UseVisualStyleBackColor = True

        outputPanel.Controls.Add(outputDeviceLabel, 0, 0)
        outputPanel.Controls.Add(outputDeviceComboBox, 1, 0)
        outputPanel.Controls.Add(outputModeLabel, 2, 0)
        outputPanel.Controls.Add(outputModeComboBox, 3, 0)
        outputPanel.Controls.Add(searchLabel, 4, 0)
        outputPanel.Controls.Add(searchTextBox, 5, 0)
        outputPanel.Controls.Add(clearSearchButton, 6, 0)

        browserSplit.Dock = DockStyle.Fill
        browserSplit.Margin = New Padding(0, 0, 0, 8)
        browserSplit.Orientation = Orientation.Vertical
        browserSplit.Size = New Size(744, 166)
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
        previewPanel.RowStyles.Add(New RowStyle(SizeType.Absolute, 32.0F))
        previewPanel.RowStyles.Add(New RowStyle(SizeType.Absolute, 34.0F))

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
        stopPreviewButton.Margin = New Padding(0, 0, 6, 0)
        stopPreviewButton.Text = "Stop"
        stopPreviewButton.UseVisualStyleBackColor = True

        fullPreviewButton.Size = New Size(96, 28)
        fullPreviewButton.Margin = New Padding(0, 0, 10, 0)
        fullPreviewButton.Text = "Full Preview"
        fullPreviewButton.UseVisualStyleBackColor = True

        loopCheckBox.AutoSize = True
        loopCheckBox.Checked = GetSavedDeckLinkLoopSetting()
        loopCheckBox.Margin = New Padding(0, 4, 10, 0)
        loopCheckBox.Text = "Loop"
        loopCheckBox.UseVisualStyleBackColor = True

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
        previewToolbarPanel.Controls.Add(fullPreviewButton)
        previewToolbarPanel.Controls.Add(loopCheckBox)
        previewToolbarPanel.Controls.Add(previewStateHostPanel)
        previewToolbarPanel.Controls.Add(selectedFileLabel)

        speedButtonsPanel.Dock = DockStyle.Fill
        speedButtonsPanel.FlowDirection = FlowDirection.LeftToRight
        speedButtonsPanel.Margin = New Padding(0, 2, 0, 0)
        speedButtonsPanel.WrapContents = False
        For Each speed In New Double() {-20.0R, -10.0R, -5.0R, -2.0R, -1.5R, -1.0R, -0.5R, 0.0R, 0.5R, 1.0R, 1.5R, 2.0R, 5.0R, 10.0R, 20.0R}
            AddSpeedPresetButton(speed)
        Next

        markControlsPanel.Dock = DockStyle.Fill
        markControlsPanel.FlowDirection = FlowDirection.LeftToRight
        markControlsPanel.Margin = New Padding(0, 1, 0, 0)
        markControlsPanel.WrapContents = False

        ConfigureTransportButton(markInButton, "Mark In", 66)
        ConfigureTransportButton(markOutButton, "Mark Out", 72)
        ConfigureMarkTextBox(markInTextBox)
        ConfigureMarkTextBox(markOutTextBox)
        ConfigureTransportButton(gotoInButton, "Goto In", 66)
        ConfigureTransportButton(gotoOutButton, "Goto Out", 74)
        ConfigureTransportButton(playFromInButton, "Play From In", 94)
        ConfigureTransportButton(playFromOutButton, "Play From Out", 104)

        markButtons.AddRange(New Button() {
            markInButton,
            markOutButton,
            gotoInButton,
            gotoOutButton,
            playFromInButton,
            playFromOutButton
        })

        markControlsPanel.Controls.Add(markInButton)
        markControlsPanel.Controls.Add(markOutButton)
        markControlsPanel.Controls.Add(markInTextBox)
        markControlsPanel.Controls.Add(markOutTextBox)
        markControlsPanel.Controls.Add(gotoInButton)
        markControlsPanel.Controls.Add(gotoOutButton)
        markControlsPanel.Controls.Add(playFromInButton)
        markControlsPanel.Controls.Add(playFromOutButton)

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
        previewPanel.Controls.Add(markControlsPanel, 0, 4)

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
        Dim buttonText = FormatPlaybackSpeed(speed)
        Dim buttonWidth = If(buttonText.Length >= 5, 52, If(buttonText.Length >= 4, 46, 42))
        Dim button As New Button() With {
            .Margin = New Padding(0, 0, 2, 0),
            .Size = New Size(buttonWidth, 26),
            .Tag = speed,
            .Text = buttonText,
            .UseVisualStyleBackColor = True
        }

        AddHandler button.Click, AddressOf OnPlaybackSpeedClicked
        speedPresetButtons.Add(button)
        speedButtonsPanel.Controls.Add(button)
    End Sub

    Private Shared Sub ConfigureTransportButton(button As Button, text As String, width As Integer)
        button.AutoSize = False
        button.Margin = New Padding(0, 0, 2, 0)
        button.Size = New Size(width, 26)
        button.Text = text
        button.UseVisualStyleBackColor = True
    End Sub

    Private Shared Sub ConfigureMarkTextBox(textBox As TextBox)
        textBox.AutoSize = False
        textBox.Margin = New Padding(0, 1, 4, 0)
        textBox.Size = New Size(88, 24)
        textBox.Text = EmptyMarkText
        textBox.TextAlign = HorizontalAlignment.Center
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

    Private Sub OnFilesGridCellMouseDown(sender As Object, e As DataGridViewCellMouseEventArgs)
        If e.Button <> MouseButtons.Right OrElse e.RowIndex < 0 OrElse e.RowIndex >= filesGridView.Rows.Count Then
            Return
        End If

        Dim colIndex = If(e.ColumnIndex >= 0, e.ColumnIndex, 0)
        filesGridView.CurrentCell = filesGridView.Rows(e.RowIndex).Cells(colIndex)
        filesGridView.ClearSelection()
        filesGridView.Rows(e.RowIndex).Selected = True
    End Sub

    Private Sub OnFilesContextMenuOpening(sender As Object, e As System.ComponentModel.CancelEventArgs)
        Dim path = GetSelectedFilePath()
        Dim hasFile = Not String.IsNullOrWhiteSpace(path) AndAlso File.Exists(path)
        filesMenuPlay.Enabled = hasFile
        filesMenuPlayInVlc.Enabled = hasFile
        filesMenuFileInfo.Enabled = hasFile
        filesMenuOpenFolder.Enabled = hasFile
    End Sub

    Private Async Sub OnFilesMenuPlayClick(sender As Object, e As EventArgs)
        Await StartSelectedPlaybackAsync()
    End Sub

    Private Sub OnFilesMenuPlayInVlcClick(sender As Object, e As EventArgs)
        PlaySelectedFileInVlc()
    End Sub

    Private Sub OnFilesMenuFileInfoClick(sender As Object, e As EventArgs)
        Dim path = GetSelectedFilePath()
        If Not String.IsNullOrWhiteSpace(path) Then
            OpenMediaInfoForm(path)
        End If
    End Sub

    Private Sub OnFilesMenuOpenFolderClick(sender As Object, e As EventArgs)
        OpenSelectedFileFolder()
    End Sub

    Private Sub OpenMediaInfoForm(filePath As String)
        If String.IsNullOrWhiteSpace(filePath) OrElse Not File.Exists(filePath) Then
            statusLabel.Text = "Media file missing"
            Return
        End If

        Dim infoForm As New MediaInfoForm(filePath)
        Dim parentForm = FindForm()
        If parentForm IsNot Nothing Then
            infoForm.Show(parentForm)
        Else
            infoForm.Show()
        End If
        statusLabel.Text = $"Opened MediaInfo for {System.IO.Path.GetFileName(filePath)}"
    End Sub

    Private Sub PlaySelectedFileInVlc()
        Dim filePath = GetSelectedFilePath()
        If String.IsNullOrWhiteSpace(filePath) OrElse Not File.Exists(filePath) Then
            statusLabel.Text = "Choose a media file first"
            Return
        End If

        Dim vlcPath As String = Nothing
        For Each candidate In EnumerateVlcCandidates()
            If File.Exists(candidate) Then
                vlcPath = candidate
                Exit For
            End If
        Next

        Try
            If Not String.IsNullOrWhiteSpace(vlcPath) Then
                Dim psi As New ProcessStartInfo With {
                    .FileName = vlcPath,
                    .UseShellExecute = False
                }
                psi.ArgumentList.Add(filePath)
                Process.Start(psi)
                statusLabel.Text = "Opened in VLC"
            Else
                Process.Start(New ProcessStartInfo With {
                    .FileName = filePath,
                    .UseShellExecute = True
                })
                statusLabel.Text = "Opened in default media player"
            End If
        Catch ex As Exception
            statusLabel.Text = $"Launch failed: {ex.Message}"
        End Try
    End Sub

    Private Shared Iterator Function EnumerateVlcCandidates() As IEnumerable(Of String)
        Yield Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "VideoLAN", "VLC", "vlc.exe")
        Yield Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "VideoLAN", "VLC", "vlc.exe")
    End Function

    Private Sub OpenSelectedFileFolder()
        Dim path = GetSelectedFilePath()
        If String.IsNullOrWhiteSpace(path) OrElse Not File.Exists(path) Then
            Return
        End If

        Try
            Process.Start("explorer.exe", $"/select,""{path}""")
        Catch ex As Exception
            statusLabel.Text = $"Explorer launch failed: {ex.Message}"
        End Try
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

    Private Sub OnFullPreviewClicked(sender As Object, e As EventArgs)
        ToggleFullscreenPreview()
    End Sub

    Private Sub ToggleFullscreenPreview()
        If fullscreenPreviewForm IsNot Nothing AndAlso Not fullscreenPreviewForm.IsDisposed Then
            CloseFullscreenPreview()
            Return
        End If

        OpenFullscreenPreview()
    End Sub

    Private Sub OpenFullscreenPreview()
        Dim screen = System.Windows.Forms.Screen.AllScreens.FirstOrDefault(Function(candidate) Not candidate.Primary)
        If screen Is Nothing Then
            screen = System.Windows.Forms.Screen.FromControl(Me)
        End If

        Dim form As New PreviewFullscreenForm()
        fullscreenPreviewForm = form
        AddHandler form.FormClosed, Sub(s, e)
                                        If Object.ReferenceEquals(fullscreenPreviewForm, form) Then
                                            fullscreenPreviewForm = Nothing
                                        End If
                                        UpdateFullscreenPreviewButton()
                                    End Sub

        form.Bounds = screen.Bounds
        Dim topLevel = FindForm()
        If topLevel IsNot Nothing Then
            form.Show(topLevel)
        Else
            form.Show()
        End If
        form.Bounds = screen.Bounds

        If previewPictureBox.Image IsNot Nothing Then
            form.SetPreviewImage(ExtractCleanVideoFrame(previewPictureBox.Image))
        End If

        UpdateFullscreenPreviewButton()
        SetStatus($"Fullscreen preview on {screen.DeviceName}.")
    End Sub

    Private Sub CloseFullscreenPreview()
        Dim form = fullscreenPreviewForm
        fullscreenPreviewForm = Nothing

        If form IsNot Nothing AndAlso Not form.IsDisposed Then
            form.Close()
        End If

        UpdateFullscreenPreviewButton()
    End Sub

    Private Sub UpdateFullscreenPreviewButton()
        fullPreviewButton.Text = If(fullscreenPreviewForm IsNot Nothing AndAlso Not fullscreenPreviewForm.IsDisposed, "Close Preview", "Full Preview")
        StyleButton(fullPreviewButton, ForeColor)
    End Sub

    Private Sub UpdateFullscreenPreviewImage(image As Image)
        Dim form = fullscreenPreviewForm
        If form Is Nothing OrElse form.IsDisposed OrElse image Is Nothing Then
            Return
        End If

        form.SetPreviewImage(ExtractCleanVideoFrame(image))
    End Sub

    Private Shared Function ExtractCleanVideoFrame(sourceImage As Image) As Image
        If sourceImage Is Nothing Then
            Return Nothing
        End If

        Const meterWidth As Integer = 30
        If sourceImage.Width > (meterWidth * 2) Then
            Dim videoWidth = sourceImage.Width - (meterWidth * 2)
            Dim videoHeight = sourceImage.Height
            Dim cleanBitmap As New Bitmap(videoWidth, videoHeight, PixelFormat.Format24bppRgb)
            Using g As Graphics = Graphics.FromImage(cleanBitmap)
                g.CompositingQuality = Drawing2D.CompositingQuality.HighSpeed
                g.InterpolationMode = Drawing2D.InterpolationMode.NearestNeighbor
                g.PixelOffsetMode = Drawing2D.PixelOffsetMode.HighSpeed
                Dim srcRect As New Rectangle(meterWidth, 0, videoWidth, videoHeight)
                Dim destRect As New Rectangle(0, 0, videoWidth, videoHeight)
                g.DrawImage(sourceImage, destRect, srcRect, GraphicsUnit.Pixel)
            End Using
            Return cleanBitmap
        Else
            Try
                Return CType(sourceImage.Clone(), Image)
            Catch ex As Exception
                Return Nothing
            End Try
        End If
    End Function

    Private Async Sub OnPlaybackSpeedClicked(sender As Object, e As EventArgs)
        Dim clickedButton = TryCast(sender, Button)

        If clickedButton Is Nothing OrElse clickedButton.Tag Is Nothing Then
            Return
        End If

        Dim selectedSpeed = NormalizePlaybackSpeed(Convert.ToDouble(clickedButton.Tag, CultureInfo.InvariantCulture))
        Await SetPlaybackSpeedAsync(selectedSpeed, restartIfPlaying:=True, startIfStopped:=Math.Abs(selectedSpeed) >= 0.001R)
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
            Await StartSelectedPlaybackAsync(filePathOverride:=restartFilePath)
        Else
            Await StartSelectedPlaybackAsync(filePathOverride:=restartFilePath)
        End If
    End Function

    Private Sub OnMarkInClicked(sender As Object, e As EventArgs)
        MarkCurrentPosition(isMarkIn:=True)
    End Sub

    Private Sub OnMarkOutClicked(sender As Object, e As EventArgs)
        MarkCurrentPosition(isMarkIn:=False)
    End Sub

    Private Async Sub OnGotoInClicked(sender As Object, e As EventArgs)
        Dim position As TimeSpan

        If TryGetMarkPosition("In", markInTextBox, markInPosition, position) Then
            markInPosition = position
            Await GoToMarkAsync("In", position)
        End If
    End Sub

    Private Async Sub OnGotoOutClicked(sender As Object, e As EventArgs)
        Dim position As TimeSpan

        If TryGetMarkPosition("Out", markOutTextBox, markOutPosition, position) Then
            markOutPosition = position
            Await GoToMarkAsync("Out", position)
        End If
    End Sub

    Private Async Sub OnPlayFromInClicked(sender As Object, e As EventArgs)
        Dim position As TimeSpan

        If TryGetMarkPosition("In", markInTextBox, markInPosition, position) Then
            markInPosition = position
            Await PlayFromMarkAsync("In", position)
        End If
    End Sub

    Private Async Sub OnPlayFromOutClicked(sender As Object, e As EventArgs)
        Dim position As TimeSpan

        If TryGetMarkPosition("Out", markOutTextBox, markOutPosition, position) Then
            markOutPosition = position
            Await PlayFromMarkAsync("Out", position)
        End If
    End Sub

    Private Sub OnMarkFieldLeave(sender As Object, e As EventArgs)
        NormalizeMarkField(TryCast(sender, TextBox), warnInvalid:=False)
    End Sub

    Private Sub OnMarkFieldKeyDown(sender As Object, e As KeyEventArgs)
        If e.KeyCode <> Keys.Enter Then
            Return
        End If

        e.Handled = True
        e.SuppressKeyPress = True
        NormalizeMarkField(TryCast(sender, TextBox), warnInvalid:=True)
    End Sub

    Private Sub MarkCurrentPosition(isMarkIn As Boolean)
        Dim position As TimeSpan

        If Not TryGetCurrentMarkablePosition(position) Then
            Return
        End If

        position = ClampToSelectedDuration(position)

        If isMarkIn Then
            markInPosition = position
            markInTextBox.Text = FormatDuration(position)
            SetStatus($"Mark In set at {FormatDuration(position)}.")
        Else
            markOutPosition = position
            markOutTextBox.Text = FormatDuration(position)
            SetStatus($"Mark Out set at {FormatDuration(position)}.")
        End If

        UpdatePreviewButtons()
    End Sub

    Private Function TryGetCurrentMarkablePosition(ByRef position As TimeSpan) As Boolean
        position = TimeSpan.Zero

        If Not IsScrubberLoaded() OrElse Not scrubberTrackBar.Enabled Then
            SetStatus("Load a clip in the player before using marks.", warning:=True)
            Return False
        End If

        position = GetCurrentTimelinePosition()
        Return True
    End Function

    Private Function GetCurrentTimelinePosition() As TimeSpan
        If playbackPositionTimer.Enabled Then
            Dim elapsed = DateTime.UtcNow - playbackClockStartedAtUtc
            Return ClampToSelectedDuration(playbackClockOffset + ScaleTimeSpan(elapsed, Math.Max(0.0R, playbackSpeedMultiplier)))
        End If

        If scrubberTrackBar.Enabled Then
            Return GetScrubberOffset()
        End If

        Return ClampToSelectedDuration(playbackStartOffset)
    End Function

    Private Function TryGetMarkPosition(markName As String, textBox As TextBox, storedMark As TimeSpan?, ByRef position As TimeSpan) As Boolean
        position = TimeSpan.Zero

        If Not IsScrubberLoaded() OrElse Not scrubberTrackBar.Enabled Then
            SetStatus("Load a clip in the player before using marks.", warning:=True)
            Return False
        End If

        Dim text = If(textBox.Text, String.Empty).Trim()

        If String.IsNullOrWhiteSpace(text) OrElse String.Equals(text, EmptyMarkText, StringComparison.Ordinal) Then
            If storedMark.HasValue Then
                position = ClampToSelectedDuration(storedMark.Value)
                textBox.Text = FormatDuration(position)
                Return True
            End If

            SetStatus($"Mark {markName} is not set.", warning:=True)
            Return False
        End If

        Dim parsed As TimeSpan

        If Not TryParseMarkTimecode(text, parsed) Then
            SetStatus($"Mark {markName} timecode must be HH:MM:SS:FF.", warning:=True)
            Return False
        End If

        position = ClampToSelectedDuration(parsed)
        textBox.Text = FormatDuration(position)
        Return True
    End Function

    Private Sub NormalizeMarkField(textBox As TextBox, warnInvalid As Boolean)
        If textBox Is Nothing Then
            Return
        End If

        Dim normalized As TimeSpan?

        If Not TryNormalizeMarkText(If(textBox Is markInTextBox, "In", "Out"), textBox, normalized, warnInvalid) Then
            Return
        End If

        If textBox Is markInTextBox Then
            markInPosition = normalized
        ElseIf textBox Is markOutTextBox Then
            markOutPosition = normalized
        End If

        UpdatePreviewButtons()
    End Sub

    Private Function TryNormalizeMarkText(markName As String, textBox As TextBox, ByRef normalized As TimeSpan?, warnInvalid As Boolean) As Boolean
        normalized = Nothing
        Dim text = If(textBox.Text, String.Empty).Trim()

        If String.IsNullOrWhiteSpace(text) OrElse String.Equals(text, EmptyMarkText, StringComparison.Ordinal) Then
            textBox.Text = EmptyMarkText
            Return True
        End If

        If Not IsScrubberLoaded() OrElse Not scrubberTrackBar.Enabled Then
            If warnInvalid Then
                SetStatus("Load a clip in the player before entering marks.", warning:=True)
            End If

            Return False
        End If

        Dim parsed As TimeSpan

        If Not TryParseMarkTimecode(text, parsed) Then
            If warnInvalid Then
                SetStatus($"Mark {markName} timecode must be HH:MM:SS:FF.", warning:=True)
            End If

            Return False
        End If

        Dim clamped = ClampToSelectedDuration(parsed)
        normalized = clamped
        textBox.Text = FormatDuration(clamped)
        Return True
    End Function

    Private Async Function GoToMarkAsync(markName As String, position As TimeSpan) As Task
        playbackStartOffset = ClampToSelectedDuration(position)
        SetScrubberPosition(playbackStartOffset)
        QueueScrubFramePreview(playbackStartOffset, updateDeckLink:=True)
        Await WaitForScrubFrameQueueAsync()
        SetStatus($"Goto {markName}: {FormatDuration(playbackStartOffset)}.")
    End Function

    Private Async Function PlayFromMarkAsync(markName As String, position As TimeSpan) As Task
        Dim filePath = scrubberLoadedFilePath

        If String.IsNullOrWhiteSpace(filePath) Then
            SetStatus("Load a clip in the player before using marks.", warning:=True)
            Return
        End If

        playbackStartOffset = ClampToPlayableStartOffset(position)
        SetScrubberPosition(playbackStartOffset)

        If Math.Abs(playbackSpeedMultiplier) < 0.001R Then
            playbackSpeedMultiplier = 1.0R
            UpdateSpeedControls()
        End If

        Await StartSelectedPlaybackAsync(filePathOverride:=filePath)
        SetStatus($"Playing from Mark {markName}: {FormatDuration(playbackStartOffset)}.")
    End Function

    Private Sub ClearMarks()
        markInPosition = Nothing
        markOutPosition = Nothing
        markInTextBox.Text = EmptyMarkText
        markOutTextBox.Text = EmptyMarkText
    End Sub

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
            lastScrubPreviewOffset = Nothing
            SetScrubberPositionFromMouse(e)
            QueueScrubFramePreview(GetScrubberOffset(), updateDeckLink:=False)
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
        lastScrubPreviewOffset = Nothing
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
            QueueScrubFramePreview(GetScrubberOffset(), updateDeckLink:=False)
        End If
    End Sub

    Private Async Sub OnGrowingDurationRefreshTimerTick(sender As Object, e As EventArgs)
        Dim currentDuration As TimeSpan
        Dim hasCurrentDuration = IsScrubberLoaded() AndAlso durationByPath.TryGetValue(scrubberLoadedFilePath, currentDuration) AndAlso currentDuration > TimeSpan.Zero
        Await RefreshLoadedGrowingDurationAsync(forceSlowFallback:=Not hasCurrentDuration)
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

        If duration.HasValue AndAlso duration.Value > TimeSpan.Zero Then
            If loopCheckBox.Checked Then
                Dim durationTicks = duration.Value.Ticks
                Dim currentTicks = position.Ticks Mod durationTicks
                SetScrubberPosition(TimeSpan.FromTicks(currentTicks))
                Return
            ElseIf position >= duration.Value Then
                SetScrubberPosition(duration.Value)
                StopPlaybackClock()
                Return
            End If
        End If

        SetScrubberPosition(position)
    End Sub

    Private Async Sub OnShuttlePlaybackTimerTick(sender As Object, e As EventArgs)
        If Not shuttlePlaybackActive OrElse isScrubberDragging OrElse isSeekingPlayback Then
            Return
        End If

        If playbackSpeedMultiplier < 0.0R Then
            Await OnReverseShuttlePlaybackTimerTickAsync()
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

    Private Async Function OnReverseShuttlePlaybackTimerTickAsync() As Task
        If reversePlaybackSeekRunning Then
            Return
        End If

        If Math.Abs(playbackSpeedMultiplier) < 0.001R Then
            StopShuttlePlaybackTimer()
            Await HoldCurrentFrameAsync()
            Return
        End If

        Dim frameDuration = GetPlaybackFrameDuration()
        Dim framesToStep = GetReversePlaybackFrameStep(frameDuration)

        If framesToStep <= 0 Then
            Return
        End If

        Dim targetTicks = GetScrubberOffset().Ticks - (frameDuration.Ticks * CLng(framesToStep))
        Dim reachedStart = targetTicks <= 0
        Dim target = If(reachedStart, TimeSpan.Zero, ClampToSelectedDuration(TimeSpan.FromTicks(targetTicks)))

        reversePlaybackSeekRunning = True

        Try
            playbackStartOffset = target
            SetScrubberPosition(target)
            QueueScrubFramePreview(target, updateDeckLink:=True)
            Await WaitForScrubFrameQueueAsync()
        Finally
            reversePlaybackSeekRunning = False
        End Try

        If reachedStart Then
            StopShuttlePlaybackTimer()
            playbackSpeedMultiplier = 0.0R
            UpdateSpeedControls()
            SetStatus("Reverse reached start. Holding frame.")
        Else
            SetStatus($"Reverse shuttle {FormatPlaybackSpeed(playbackSpeedMultiplier)} at {FormatDuration(target)}.")
        End If
    End Function

    Private Function GetReversePlaybackFrameStep(frameDuration As TimeSpan) As Integer
        Dim now = DateTime.UtcNow
        Dim elapsed = If(reversePlaybackLastTickAtUtc.HasValue, now - reversePlaybackLastTickAtUtc.Value, TimeSpan.FromMilliseconds(shuttlePlaybackTimer.Interval))
        reversePlaybackLastTickAtUtc = now

        If elapsed <= TimeSpan.Zero Then
            elapsed = TimeSpan.FromMilliseconds(shuttlePlaybackTimer.Interval)
        End If

        If elapsed > TimeSpan.FromMilliseconds(250) Then
            elapsed = TimeSpan.FromMilliseconds(250)
        End If

        Dim frameSeconds = frameDuration.TotalSeconds

        If frameSeconds <= 0.0R Then
            frameSeconds = 1.0R / DurationDisplayFrameRate
        End If

        reversePlaybackFrameCarry += Math.Abs(playbackSpeedMultiplier) * elapsed.TotalSeconds / frameSeconds
        Dim framesToStep = CInt(Math.Floor(reversePlaybackFrameCarry))

        If framesToStep <= 0 Then
            Return 0
        End If

        reversePlaybackFrameCarry -= framesToStep
        Return framesToStep
    End Function

    Private Shared Function GetPlaybackFrameDuration() As TimeSpan
        Return TimeSpan.FromSeconds(1.0R / DurationDisplayFrameRate)
    End Function

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
            Dim sdiFrameReady = Await RenderSeekFrameAsync(filePath, playbackStartOffset, updateDeckLink, requestGeneration, cancellationToken)

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

    Private Shared Function GetSavedDeckLinkLoopSetting() As Boolean
        Dim settingValue = GetSavedDeckLinkOutputSetting("Loop")
        Dim isLoop As Boolean
        If Boolean.TryParse(settingValue, isLoop) Then
            Return isLoop
        End If
        Return False
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
                $"Mode={outputMode.DisplayName}",
                $"Loop={loopCheckBox.Checked}"
            })
        Catch
        End Try
    End Sub

    Private Async Sub OnLoopCheckedChanged(sender As Object, e As EventArgs)
        SaveDeckLinkOutputSelection()

        If loopCheckBox.Checked Then
            SetStatus("Loop playback enabled.")
        Else
            SetStatus("Loop playback disabled.")
        End If

        If IsPlaybackActive() AndAlso Not isSeekingPlayback Then
            playbackStartOffset = GetScrubberOffset()
            Dim filePath = If(IsScrubberLoaded(), scrubberLoadedFilePath, GetSelectedFilePath())
            If Not String.IsNullOrWhiteSpace(filePath) Then
                Await StartSelectedPlaybackAsync(filePathOverride:=filePath)
            End If
        End If
    End Sub

    Private Sub LoadFolderTree(folderPath As String)
        folderTreeView.BeginUpdate()

        Try
            folderTreeView.Nodes.Clear()

            If String.IsNullOrWhiteSpace(folderPath) OrElse Not Directory.Exists(folderPath) Then
                currentFolderFiles.Clear()
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
        currentFolderFiles.Clear()
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

            currentFolderFiles.AddRange(files)
            ApplyFileFilter()
            StartDurationProbe(files.Select(Function(fileInfo) fileInfo.FullName).ToList())

            Dim filterText = searchTextBox.Text.Trim()
            If String.IsNullOrWhiteSpace(filterText) Then
                SetStatus($"{files.Count} media file(s) loaded from {folderPath}.")
            Else
                SetStatus($"{filesGridView.Rows.Count} of {files.Count} media file(s) match ""{filterText}"".")
            End If
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

    Private Sub ApplyFileFilter()
        Dim filterText = searchTextBox.Text.Trim()
        Dim filteredFiles As List(Of FileInfo)

        If String.IsNullOrWhiteSpace(filterText) Then
            filteredFiles = currentFolderFiles.ToList()
        Else
            filteredFiles = currentFolderFiles.Where(Function(fi) fi.Name.IndexOf(filterText, StringComparison.OrdinalIgnoreCase) >= 0).ToList()
        End If

        Dim previousSelectedPath = selectedFilePath
        PopulateFileGrid(filteredFiles)

        If Not String.IsNullOrWhiteSpace(previousSelectedPath) Then
            For Each row As DataGridViewRow In filesGridView.Rows
                If row.Tag IsNot Nothing AndAlso String.Equals(TryCast(row.Tag, String), previousSelectedPath, StringComparison.OrdinalIgnoreCase) Then
                    row.Selected = True
                    filesGridView.CurrentCell = row.Cells("Name")
                    Exit For
                End If
            Next
        End If

        selectedFilePath = GetSelectedFilePath()
        If String.IsNullOrWhiteSpace(selectedFilePath) Then
            selectedFileLabel.Text = If(filesGridView.Rows.Count = 0 AndAlso Not String.IsNullOrWhiteSpace(filterText), "No matching files.", "Select a file in the grid, then Play or double-click.")
        Else
            selectedFileLabel.Text = Path.GetFileName(selectedFilePath)
        End If

        RefreshScrubberForSelectedFile()
        UpdatePreviewButtons()
    End Sub

    Private Sub OnSearchTextChanged(sender As Object, e As EventArgs)
        ApplyFileFilter()
        Dim filterText = searchTextBox.Text.Trim()
        If Not String.IsNullOrWhiteSpace(filterText) Then
            SetStatus($"{filesGridView.Rows.Count} of {currentFolderFiles.Count} media file(s) match ""{filterText}"".")
        ElseIf currentFolderFiles.Count > 0 Then
            SetStatus($"{currentFolderFiles.Count} media file(s) loaded.")
        End If
    End Sub

    Private Sub OnClearSearchClicked(sender As Object, e As EventArgs)
        searchTextBox.Text = String.Empty
        searchTextBox.Focus()
    End Sub

    Private Sub OnSearchTextBoxKeyDown(sender As Object, e As KeyEventArgs)
        If e.KeyCode = Keys.Escape Then
            searchTextBox.Text = String.Empty
            e.Handled = True
            e.SuppressKeyPress = True
        ElseIf e.KeyCode = Keys.Down Then
            If filesGridView.Rows.Count > 0 Then
                filesGridView.Focus()
                e.Handled = True
            End If
        End If
    End Sub

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
            Dim durationText = "--"
            Dim probedDuration As TimeSpan
            If durationByPath.TryGetValue(fileInfo.FullName, probedDuration) Then
                durationText = FormatDuration(probedDuration)
            ElseIf IsImageFile(fileInfo.FullName) Then
                durationText = "Still"
            End If

            Dim rowIndex = filesGridView.Rows.Add(
                fileInfo.Name,
                durationText,
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

            Dim duration = Await Task.Run(Function() ProbeDuration(ffprobePath, filePath, allowSlowFallback:=False))
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

    Private Sub UpdateGrowingDurationRefreshTimer()
        If Not IsScrubberLoaded() OrElse String.IsNullOrWhiteSpace(scrubberLoadedFilePath) OrElse Not IsGrowingSeekDurationCandidate(scrubberLoadedFilePath) OrElse Not File.Exists(scrubberLoadedFilePath) Then
            growingDurationRefreshTimer.Stop()
            Return
        End If

        Dim currentDuration As TimeSpan
        Dim hasCurrentDuration = durationByPath.TryGetValue(scrubberLoadedFilePath, currentDuration) AndAlso currentDuration > TimeSpan.Zero
        Dim shouldRefresh = WasFileRecentlyWritten(scrubberLoadedFilePath) OrElse Not hasCurrentDuration

        If Not shouldRefresh Then
            growingDurationRefreshTimer.Stop()
            Return
        End If

        If Not growingDurationRefreshTimer.Enabled Then
            growingDurationRefreshTimer.Start()
        End If

    End Sub

    Private Async Function RefreshLoadedGrowingDurationAsync(forceSlowFallback As Boolean) As Task
        If isGrowingDurationRefreshRunning Then
            Return
        End If

        Dim filePath = scrubberLoadedFilePath

        If String.IsNullOrWhiteSpace(filePath) OrElse Not IsGrowingSeekDurationCandidate(filePath) OrElse Not File.Exists(filePath) Then
            growingDurationRefreshTimer.Stop()
            Return
        End If

        Dim ffprobePath = Path.Combine(AppContext.BaseDirectory, "ffprobe.exe")

        If Not File.Exists(ffprobePath) Then
            Return
        End If

        isGrowingDurationRefreshRunning = True

        Try
            Dim duration = Await Task.Run(Function() ProbeDuration(ffprobePath, filePath, allowSlowFallback:=forceSlowFallback))

            If IsDisposed OrElse Not String.Equals(scrubberLoadedFilePath, filePath, StringComparison.OrdinalIgnoreCase) OrElse Not duration.HasValue Then
                Return
            End If

            Dim oldDuration As TimeSpan
            Dim oneFrame = GetPlaybackFrameDuration()

            If durationByPath.TryGetValue(filePath, oldDuration) AndAlso oldDuration > TimeSpan.Zero Then
                If duration.Value <= oldDuration + oneFrame Then
                    Return
                End If
            End If

            UpdateDurationCell(filePath, FormatDuration(duration.Value), duration.Value)
        Finally
            isGrowingDurationRefreshRunning = False

            If Not IsDisposed Then
                UpdateGrowingDurationRefreshTimer()
            End If
        End Try
    End Function

    Private Shared Function ProbeDuration(ffprobePath As String, filePath As String, Optional allowSlowFallback As Boolean = False) As TimeSpan?
        If IsImageFile(filePath) Then
            Return Nothing
        End If

        Dim duration = ProbeContainerDuration(ffprobePath, filePath)

        If duration.HasValue Then
            Return duration
        End If

        duration = EstimateGrowingRecordingDuration(filePath)

        If duration.HasValue Then
            Return duration
        End If

        If allowSlowFallback AndAlso IsGrowingSeekDurationCandidate(filePath) Then
            Return ProbeDurationFromFrameCount(ffprobePath, filePath)
        End If

        Return Nothing
    End Function

    Private Shared Function ProbeContainerDuration(ffprobePath As String, filePath As String) As TimeSpan?
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

                Dim outputTask = process.StandardOutput.ReadToEndAsync()

                If Not process.WaitForExit(3000) Then
                    process.Kill(True)
                    Return Nothing
                End If

                Dim output = outputTask.GetAwaiter().GetResult().Trim()

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

    Private Shared Function EstimateGrowingRecordingDuration(filePath As String) As TimeSpan?
        If Not IsGrowingSeekDurationCandidate(filePath) OrElse Not WasFileRecentlyWritten(filePath) Then
            Return Nothing
        End If

        Dim startTime As DateTime

        If TryGetRecordingStartTimeFromFileName(filePath, startTime) Then
            Dim duration = DateTime.Now - startTime

            If duration > TimeSpan.FromSeconds(1) Then
                Return duration
            End If
        End If

        Try
            Dim creationTime = File.GetCreationTime(filePath)
            Dim duration = DateTime.Now - creationTime

            If duration > TimeSpan.FromSeconds(1) AndAlso duration < TimeSpan.FromDays(2) Then
                Return duration
            End If
        Catch
        End Try

        Return Nothing
    End Function

    Private Shared Function TryGetRecordingStartTimeFromFileName(filePath As String, ByRef startTime As DateTime) As Boolean
        startTime = DateTime.MinValue
        Dim name = Path.GetFileNameWithoutExtension(filePath)

        If String.IsNullOrWhiteSpace(name) Then
            Return False
        End If

        Dim parts = name.Split("_"c)

        If parts.Length < 3 Then
            Return False
        End If

        For index = parts.Length - 2 To 1 Step -1
            Dim dateText = parts(index - 1)
            Dim timeText = parts(index)

            If DateTime.TryParseExact(
                $"{dateText}_{timeText}",
                "ddMMyyyy_HHmmss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal,
                startTime) Then

                Return True
            End If
        Next

        Return False
    End Function

    Private Shared Function ProbeDurationFromFrameCount(ffprobePath As String, filePath As String) As TimeSpan?
        Try
            Dim startInfo As New ProcessStartInfo() With {
                .FileName = ffprobePath,
                .UseShellExecute = False,
                .RedirectStandardOutput = True,
                .RedirectStandardError = True,
                .CreateNoWindow = True
            }

            startInfo.ArgumentList.Add("-v")
            startInfo.ArgumentList.Add("error")
            startInfo.ArgumentList.Add("-select_streams")
            startInfo.ArgumentList.Add("v:0")
            startInfo.ArgumentList.Add("-count_packets")
            startInfo.ArgumentList.Add("-show_entries")
            startInfo.ArgumentList.Add("stream=duration,nb_read_packets,r_frame_rate,avg_frame_rate")
            startInfo.ArgumentList.Add("-of")
            startInfo.ArgumentList.Add("default=nw=1")
            startInfo.ArgumentList.Add(filePath)

            Using process As New Process() With {.StartInfo = startInfo}
                If Not process.Start() Then
                    Return Nothing
                End If

                Dim outputTask = process.StandardOutput.ReadToEndAsync()

                If Not process.WaitForExit(5000) Then
                    process.Kill(True)
                    Return Nothing
                End If

                If process.ExitCode <> 0 Then
                    Return Nothing
                End If

                Return ParseFrameCountDuration(outputTask.GetAwaiter().GetResult())
            End Using
        Catch
            Return Nothing
        End Try
    End Function

    Private Shared Function ParseFrameCountDuration(output As String) As TimeSpan?
        If String.IsNullOrWhiteSpace(output) Then
            Return Nothing
        End If

        Dim durationSeconds As Double
        Dim frameCount As Long
        Dim frameRate As Double

        For Each line In output.Split({ControlChars.Cr, ControlChars.Lf}, StringSplitOptions.RemoveEmptyEntries)
            Dim separatorIndex = line.IndexOf("="c)

            If separatorIndex <= 0 Then
                Continue For
            End If

            Dim key = line.Substring(0, separatorIndex).Trim()
            Dim value = line.Substring(separatorIndex + 1).Trim()

            Select Case key
                Case "duration"
                    If Double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, durationSeconds) AndAlso durationSeconds > 0 Then
                        Return TimeSpan.FromSeconds(durationSeconds)
                    End If
                Case "nb_read_packets"
                    Long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, frameCount)
                Case "avg_frame_rate"
                    If frameRate <= 0 Then
                        TryParseFrameRate(value, frameRate)
                    End If
                Case "r_frame_rate"
                    If frameRate <= 0 Then
                        TryParseFrameRate(value, frameRate)
                    End If
            End Select
        Next

        If frameCount > 0 AndAlso frameRate > 0 Then
            Return TimeSpan.FromSeconds(frameCount / frameRate)
        End If

        Return Nothing
    End Function

    Private Shared Function TryParseFrameRate(value As String, ByRef frameRate As Double) As Boolean
        frameRate = 0.0R

        If String.IsNullOrWhiteSpace(value) OrElse String.Equals(value, "0/0", StringComparison.OrdinalIgnoreCase) Then
            Return False
        End If

        Dim parts = value.Split("/"c)

        If parts.Length = 2 Then
            Dim numerator As Double
            Dim denominator As Double

            If Double.TryParse(parts(0), NumberStyles.Float, CultureInfo.InvariantCulture, numerator) AndAlso
                Double.TryParse(parts(1), NumberStyles.Float, CultureInfo.InvariantCulture, denominator) AndAlso
                denominator > 0 Then

                frameRate = numerator / denominator
                Return frameRate > 0
            End If
        End If

        Return Double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, frameRate) AndAlso frameRate > 0
    End Function

    Private Shared Function IsGrowingSeekDurationCandidate(filePath As String) As Boolean
        Dim extension = Path.GetExtension(filePath)
        Return String.Equals(extension, ".ts", StringComparison.OrdinalIgnoreCase) OrElse
            String.Equals(extension, ".mxf", StringComparison.OrdinalIgnoreCase)
    End Function

    Private Shared Function WasFileRecentlyWritten(filePath As String) As Boolean
        Try
            Return DateTime.UtcNow - File.GetLastWriteTimeUtc(filePath) <= GrowingFileRecentWriteWindow
        Catch
            Return False
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

    Private Shared Function TryParseMarkTimecode(value As String, ByRef position As TimeSpan) As Boolean
        position = TimeSpan.Zero

        If String.IsNullOrWhiteSpace(value) Then
            Return False
        End If

        Dim text = value.Trim().Replace(";"c, ":"c)
        Dim parts = text.Split(":"c)
        Dim hours = 0
        Dim minutes = 0
        Dim seconds = 0
        Dim frames = 0

        Select Case parts.Length
            Case 4
                If Not TryParseNonNegativeInteger(parts(0), hours) OrElse
                    Not TryParseNonNegativeInteger(parts(1), minutes) OrElse
                    Not TryParseNonNegativeInteger(parts(2), seconds) OrElse
                    Not TryParseNonNegativeInteger(parts(3), frames) OrElse
                    minutes >= 60 OrElse seconds >= 60 OrElse frames >= DurationDisplayFrameRate Then
                    Return False
                End If

            Case 3
                If Not TryParseNonNegativeInteger(parts(0), hours) OrElse
                    Not TryParseNonNegativeInteger(parts(1), minutes) OrElse
                    Not TryParseNonNegativeInteger(parts(2), seconds) OrElse
                    minutes >= 60 OrElse seconds >= 60 Then
                    Return False
                End If

            Case 2
                If Not TryParseNonNegativeInteger(parts(0), minutes) OrElse
                    Not TryParseNonNegativeInteger(parts(1), seconds) OrElse
                    seconds >= 60 Then
                    Return False
                End If

            Case 1
                Dim totalSeconds As Double

                If Not Double.TryParse(parts(0), NumberStyles.Float, CultureInfo.InvariantCulture, totalSeconds) OrElse totalSeconds < 0.0R Then
                    Return False
                End If

                position = TimeSpan.FromSeconds(totalSeconds)
                Return True

            Case Else
                Return False
        End Select

        position = TimeSpan.FromHours(hours) +
            TimeSpan.FromMinutes(minutes) +
            TimeSpan.FromSeconds(seconds) +
            TimeSpan.FromSeconds(frames / CDbl(DurationDisplayFrameRate))
        Return True
    End Function

    Private Shared Function TryParseNonNegativeInteger(value As String, ByRef result As Integer) As Boolean
        Return Integer.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, result) AndAlso result >= 0
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
            UpdateGrowingDurationRefreshTimer()
            ClearScrubber()
            Return
        End If

        Dim duration = GetSelectedDuration()

        If Not duration.HasValue Then
            UpdateGrowingDurationRefreshTimer()
            If IsGrowingSeekDurationCandidate(scrubberLoadedFilePath) Then
                Dim ignored = RefreshLoadedGrowingDurationAsync(forceSlowFallback:=True)
            End If
            scrubberTrackBar.Enabled = False
            scrubberTrackBar.Value = 0
            scrubberTrackBar.Maximum = 0
            scrubberTimeLabel.Text = If(IsImageFile(scrubberLoadedFilePath), "Still image", "--:--:--:-- / --:--:--:--")
            UpdateMarkControls()
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
        UpdateGrowingDurationRefreshTimer()
        UpdateMarkControls()
    End Sub

    Private Sub ClearScrubber()
        growingDurationRefreshTimer.Stop()
        scrubberTrackBar.Enabled = False
        scrubberTrackBar.Value = 0
        scrubberTrackBar.Maximum = 0
        scrubberTimeLabel.Text = "--:--:--:-- / --:--:--:--"
        ClearMarks()
        UpdateMarkControls()
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
        ResetReversePlaybackStepState()
        StopReverseCachePlayback()

        If Not IsDisposed Then
            UpdatePreviewButtons()
        End If
    End Sub

    Private Sub ResetReversePlaybackStepState()
        reversePlaybackFrameCarry = 0.0R
        reversePlaybackLastTickAtUtc = Nothing
        reversePlaybackSeekRunning = False
    End Sub

    Private Sub StopReverseCachePlayback()
        Dim cancellation = reversePlaybackCancellation
        reversePlaybackCancellation = Nothing

        If cancellation IsNot Nothing Then
            cancellation.Cancel()
        End If

        reversePlaybackTask = Nothing
        reversePreviewCache?.Dispose()
        reversePreviewCache = Nothing
        reverseDeckLinkCache?.Dispose()
        reverseDeckLinkCache = Nothing
        DisposeReverseAudio()
    End Sub

    Private Sub DisposeScrubPreviewCache()
        scrubPreviewCache?.Dispose()
        scrubPreviewCache = Nothing
        lastScrubPreviewOffset = Nothing
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
            DisposeScrubPreviewCache()
            ClearMarks()
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
        ResetReversePlaybackStepState()
        shuttlePlaybackTimer.Interval = CInt(Math.Max(10, Math.Min(1000, GetPlaybackFrameDuration().TotalMilliseconds)))
        shuttlePlaybackActive = True
        shuttlePlaybackTimer.Start()
        QueueScrubFramePreview(playbackStartOffset, updateDeckLink:=True)
        SetStatus($"Shuttle playback {FormatPlaybackSpeed(playbackSpeedMultiplier)}: {Path.GetFileName(filePath)}")
        UpdatePreviewButtons()
    End Function

    Private Async Function StartReverseCachedPlaybackAsync(Optional resetToStart As Boolean = False, Optional filePathOverride As String = Nothing) As Task
        Dim filePath = If(String.IsNullOrWhiteSpace(filePathOverride), GetSelectedFilePath(), filePathOverride)
        Dim updateSelection = String.IsNullOrWhiteSpace(filePathOverride)

        If String.IsNullOrWhiteSpace(filePath) Then
            SetStatus("Select a file from the grid first.", warning:=True)
            Return
        End If

        If IsImageFile(filePath) Then
            SetStatus("Reverse speed is for video files.", warning:=True)
            Return
        End If

        Dim ffmpegPath = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe")

        If Not File.Exists(ffmpegPath) Then
            SetStatus($"ffmpeg.exe not found in {AppContext.BaseDirectory}", warning:=True)
            Return
        End If

        If updateSelection Then
            selectedFilePath = filePath
        End If

        If Not String.Equals(scrubberLoadedFilePath, filePath, StringComparison.OrdinalIgnoreCase) Then
            DisposeScrubPreviewCache()
            ClearMarks()
            scrubberLoadedFilePath = filePath
            playbackStartOffset = TimeSpan.Zero
        End If
        RefreshScrubberForSelectedFile()

        Dim duration = GetSelectedDuration()

        If Not duration.HasValue OrElse duration.Value <= TimeSpan.Zero Then
            SetStatus("Duration is required for reverse cache playback.", warning:=True)
            Return
        End If

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
        Await StopPreviewAsync(clearImage:=False, showStateLabel:=False)
        TearDownAudioMonitor(fast:=True)

        playbackStartOffset = ClampToSelectedDuration(GetScrubberOffset())

        Dim selectedOutputDevice = TryCast(outputDeviceComboBox.SelectedItem, String)
        Dim selectedOutputMode = TryCast(outputModeComboBox.SelectedItem, DeckLinkOutputMode)
        Dim useDeckLinkOutput = Not IsNoDeckLinkOutputDevice(selectedOutputDevice) AndAlso selectedOutputMode IsNot Nothing
        Dim fileHasAudio = Await Task.Run(Function() ProbeHasAudioStream(filePath))

        If Not useDeckLinkOutput AndAlso outputRunner IsNot Nothing Then
            Await StopOutputAsync()
        ElseIf useDeckLinkOutput AndAlso outputRunner Is Nothing Then
            outputRunner = New InProcessDeckLinkOutputRunner()
        End If

        Dim frameDuration = GetPlaybackFrameDuration()
        Dim cancellation = New CancellationTokenSource()
        Dim reverseAudioSpeed = Math.Abs(playbackSpeedMultiplier)
        Dim reverseAudioEnabled = fileHasAudio AndAlso reverseAudioSpeed <= 20.001R
        DisposeReverseAudio()

        If useDeckLinkOutput Then
            reversePreviewCache = Nothing
            reverseDeckLinkCache = New ReverseFrameCache(ffmpegPath, filePath, selectedOutputMode.Width, selectedOutputMode.Height, 2, "uyvy422", selectedOutputMode.IsInterlaced, frameDuration, playbackSpeedMultiplier, Nothing)

            Try
                reverseDeckLinkAudioEnabled = Await outputRunner.PrepareCachedFrameOutputAsync(selectedOutputDevice, selectedOutputMode.FormatCode, selectedOutputMode.Width, selectedOutputMode.Height, selectedOutputMode.FrameRate, reverseAudioEnabled, cancellation.Token)
            Catch ex As Exception
                cancellation.Dispose()
                reverseDeckLinkCache?.Dispose()
                reverseDeckLinkCache = Nothing
                SetStatus($"Unable to prepare DeckLink reverse output: {ex.Message}", warning:=True)
                Return
            End Try

            If reverseAudioEnabled AndAlso reverseDeckLinkAudioEnabled Then
                reverseDeckLinkAudioOutput = New ReverseDeckLinkAudioOutput(outputRunner, AddressOf SetReverseAudioStatus)
            End If

            outputRunnerIsScrubHold = True
            scrubDeckLinkOutputKey = BuildScrubDeckLinkOutputKey(filePath, selectedOutputDevice, selectedOutputMode)
        Else
            reversePreviewCache = New ReverseFrameCache(ffmpegPath, filePath, 900, 540, 3, "bgr24", False, frameDuration, playbackSpeedMultiplier, Nothing)
            reverseDeckLinkCache = Nothing
            outputRunnerIsScrubHold = False
            scrubDeckLinkOutputKey = Nothing

            If reverseAudioEnabled AndAlso speakerMonitorEnabledValue Then
                Try
                    reverseWaveAudioOutput = New ReverseWaveOutAudioOutput(ReverseAudioChannels, GetReverseAudioMonitorGain(reverseAudioSpeed))
                Catch ex As Exception
                    SetReverseAudioStatus($"Reverse Windows audio unavailable: {ex.Message}")
                End Try
            End If
        End If

        If reverseAudioEnabled Then
            reverseAudio = New ReverseAudioChunkQueue(ffmpegPath, filePath, reverseAudioSpeed, ReverseAudioChannels, playbackStartOffset, AddressOf SetReverseAudioStatus)
        ElseIf Not fileHasAudio Then
            reverseAudioLeftDbfs = -90.0R
            reverseAudioRightDbfs = -90.0R
        End If

        reversePlaybackCancellation = cancellation
        shuttlePlaybackActive = True
        previewStateLabel.Visible = False
        SetScrubberPosition(playbackStartOffset)
        SetStatus($"Building reverse cache {FormatPlaybackSpeed(playbackSpeedMultiplier)}: {Path.GetFileName(filePath)}")
        UpdatePreviewButtons()

        reversePlaybackTask = RunReverseCachedPlaybackAsync(filePath, duration.Value, playbackStartOffset, selectedOutputDevice, selectedOutputMode, cancellation)
    End Function

    Private Async Function RunReverseCachedPlaybackAsync(filePath As String, duration As TimeSpan, startPosition As TimeSpan, deckLinkDeviceName As String, deckLinkOutputMode As DeckLinkOutputMode, cancellationSource As CancellationTokenSource) As Task
        Dim cancellationToken = cancellationSource.Token
        Dim localPreviewCache = reversePreviewCache
        Dim localDeckLinkCache = reverseDeckLinkCache
        Dim frameDuration = GetPlaybackFrameDuration()
        Dim position = ClampToSelectedDuration(startPosition)
        Dim stopwatch As Stopwatch = Stopwatch.StartNew()
        Dim frameTicks = Math.Max(1L, CLng(Math.Round(Stopwatch.Frequency * frameDuration.TotalSeconds, MidpointRounding.AwayFromZero)))
        Dim nextFrameDueTicks = stopwatch.ElapsedTicks
        Dim sourceFrameCarry = 0.0R

        Try
            While Not cancellationToken.IsCancellationRequested AndAlso shuttlePlaybackActive AndAlso playbackSpeedMultiplier < 0.0R
                Dim requestedPosition = position
                Dim cachedFrame As ReverseDecodedFrame

                If localDeckLinkCache IsNot Nothing Then
                    cachedFrame = Await Task.Run(Function() localDeckLinkCache.GetFrame(requestedPosition, cancellationToken), cancellationToken)
                ElseIf localPreviewCache IsNot Nothing Then
                    cachedFrame = Await Task.Run(Function() localPreviewCache.GetFrame(requestedPosition, cancellationToken), cancellationToken)
                Else
                    Throw New InvalidOperationException("Reverse cache is unavailable.")
                End If

                cancellationToken.ThrowIfCancellationRequested()
                position = ClampToSelectedDuration(cachedFrame.Position)
                playbackStartOffset = position
                SetScrubberPosition(position)
                PumpReverseAudioFrame(frameDuration, previewOnly:=localDeckLinkCache Is Nothing)

                If localDeckLinkCache IsNot Nothing Then
                    DisplayReversePreviewUyvyFrame(cachedFrame.Data, deckLinkOutputMode.Width, deckLinkOutputMode.Height)
                Else
                    DisplayReversePreviewFrame(cachedFrame.Data)
                End If

                If localDeckLinkCache IsNot Nothing AndAlso deckLinkOutputMode IsNot Nothing AndAlso outputRunner IsNot Nothing Then
                    Await outputRunner.DisplayCachedFrameAsync(deckLinkDeviceName, deckLinkOutputMode.FormatCode, deckLinkOutputMode.Width, deckLinkOutputMode.Height, deckLinkOutputMode.FrameRate, cachedFrame.Data, cancellationToken)
                    outputRunnerIsScrubHold = True
                End If

                If position <= TimeSpan.Zero Then
                    playbackSpeedMultiplier = 0.0R
                    UpdateSpeedControls()
                    SetStatus("Reverse reached start. Holding frame.")
                    Exit While
                End If

                Dim speed = Math.Abs(playbackSpeedMultiplier)

                If speed <= 0.0R Then
                    Exit While
                End If

                sourceFrameCarry += speed
                Dim framesToStep = Math.Max(1, CInt(Math.Floor(sourceFrameCarry)))
                sourceFrameCarry -= framesToStep
                Dim nextTicks = position.Ticks - (frameDuration.Ticks * CLng(framesToStep))
                position = If(nextTicks > 0, TimeSpan.FromTicks(nextTicks), TimeSpan.Zero)

                nextFrameDueTicks += frameTicks
                Dim delayTicks = nextFrameDueTicks - stopwatch.ElapsedTicks

                If delayTicks > 0 Then
                    Await Task.Delay(TimeSpan.FromSeconds(delayTicks / CDbl(Stopwatch.Frequency)), cancellationToken)
                Else
                    nextFrameDueTicks = stopwatch.ElapsedTicks
                End If
            End While
        Catch ex As OperationCanceledException
        Catch ex As ObjectDisposedException
        Catch ex As Exception
            If Not IsDisposed Then
                SetStatus($"Reverse cache stopped: {ex.Message}", warning:=True)
            End If
        Finally
            localPreviewCache?.Dispose()
            localDeckLinkCache?.Dispose()

            If Object.ReferenceEquals(reversePlaybackCancellation, cancellationSource) Then
                reversePlaybackCancellation = Nothing
                reversePlaybackTask = Nothing
                reversePreviewCache = Nothing
                reverseDeckLinkCache = Nothing
                shuttlePlaybackActive = False
                ResetReversePlaybackStepState()
                DisposeReverseAudio()

                If Not IsDisposed Then
                    UpdatePreviewButtons()
                End If
            End If

            cancellationSource.Dispose()
        End Try
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
            DisposeScrubPreviewCache()
            ClearMarks()
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
            Await StartReverseCachedPlaybackAsync(resetToStart, filePath)
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

    Private Sub DisplayReversePreviewFrame(frameData As Byte())
        DisplayReversePreviewFrame(frameData, 900, 540)
    End Sub

    Private Sub DisplayReversePreviewFrame(frameData As Byte(), sourceWidth As Integer, sourceHeight As Integer)
        If frameData Is Nothing OrElse frameData.Length = 0 OrElse IsDisposed Then
            Return
        End If

        Dim frame = CreateReversePreviewBitmap(frameData, sourceWidth, sourceHeight, reverseAudioLeftDbfs, reverseAudioRightDbfs)
        Dim previousImage = previewPictureBox.Image
        previewPictureBox.Image = frame
        UpdateFullscreenPreviewImage(frame)
        previewStateLabel.Visible = False

        If previousImage IsNot Nothing Then
            previousImage.Dispose()
        End If
    End Sub

    Private Sub DisplayReversePreviewUyvyFrame(frameData As Byte(), sourceWidth As Integer, sourceHeight As Integer)
        If frameData Is Nothing OrElse frameData.Length = 0 OrElse sourceWidth <= 0 OrElse sourceHeight <= 0 OrElse IsDisposed Then
            Return
        End If

        Dim frame = CreateReversePreviewBitmapFromUyvy(frameData, sourceWidth, sourceHeight, reverseAudioLeftDbfs, reverseAudioRightDbfs)
        Dim previousImage = previewPictureBox.Image
        previewPictureBox.Image = frame
        UpdateFullscreenPreviewImage(frame)
        previewStateLabel.Visible = False

        If previousImage IsNot Nothing Then
            previousImage.Dispose()
        End If
    End Sub

    Private Shared Function CreateReversePreviewBitmap(frameData As Byte(), videoWidth As Integer, videoHeight As Integer, leftDbfs As Double, rightDbfs As Double) As Bitmap
        Const meterOutputWidth As Integer = 30
        Dim videoBitmap As New Bitmap(videoWidth, videoHeight, PixelFormat.Format24bppRgb)
        Dim bounds As New Rectangle(0, 0, videoWidth, videoHeight)
        Dim bitmapData = videoBitmap.LockBits(bounds, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb)

        Try
            Dim sourceStride = videoWidth * 3
            Dim copyBytes = Math.Min(sourceStride, Math.Abs(bitmapData.Stride))

            For row = 0 To videoHeight - 1
                Dim sourceOffset = row * sourceStride
                Dim destination = IntPtr.Add(bitmapData.Scan0, row * bitmapData.Stride)
                Marshal.Copy(frameData, sourceOffset, destination, copyBytes)
            Next
        Finally
            videoBitmap.UnlockBits(bitmapData)
        End Try

        Dim composite As New Bitmap(videoWidth + (meterOutputWidth * 2), videoHeight, PixelFormat.Format24bppRgb)

        Using graphics As Graphics = Graphics.FromImage(composite)
            graphics.Clear(Color.Black)
            DrawReverseMeterRail(graphics, New Rectangle(0, 0, meterOutputWidth, videoHeight), leftDbfs)
            graphics.DrawImage(videoBitmap, meterOutputWidth, 0, videoWidth, videoHeight)
            DrawReverseMeterRail(graphics, New Rectangle(meterOutputWidth + videoWidth, 0, meterOutputWidth, videoHeight), rightDbfs)
        End Using

        videoBitmap.Dispose()
        Return composite
    End Function

    Private Shared Function CreateReversePreviewBitmapFromUyvy(uyvyFrame As Byte(), sourceWidth As Integer, sourceHeight As Integer, leftDbfs As Double, rightDbfs As Double) As Bitmap
        Const previewVideoWidth As Integer = 900
        Const previewVideoHeight As Integer = 540
        Const meterOutputWidth As Integer = 30

        If uyvyFrame.Length < sourceWidth * sourceHeight * 2 Then
            Throw New InvalidOperationException("Reverse preview frame is smaller than expected.")
        End If

        Dim videoBitmap As New Bitmap(previewVideoWidth, previewVideoHeight, PixelFormat.Format24bppRgb)
        Dim bounds As New Rectangle(0, 0, previewVideoWidth, previewVideoHeight)
        Dim bitmapData = videoBitmap.LockBits(bounds, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb)

        Try
            Dim row(previewVideoWidth * 3 - 1) As Byte

            For y = 0 To previewVideoHeight - 1
                Dim sourceY = Math.Min(sourceHeight - 1, y * sourceHeight \ previewVideoHeight)

                For x = 0 To previewVideoWidth - 1
                    Dim sourceX = Math.Min(sourceWidth - 1, x * sourceWidth \ previewVideoWidth)
                    Dim pairX = sourceX And Not 1

                    If pairX >= sourceWidth - 1 Then
                        pairX = Math.Max(0, sourceWidth - 2)
                    End If

                    Dim sourceOffset = (sourceY * sourceWidth + pairX) * 2
                    Dim uValue = uyvyFrame(sourceOffset)
                    Dim vValue = uyvyFrame(sourceOffset + 2)
                    Dim yValue = If(sourceX = pairX, uyvyFrame(sourceOffset + 1), uyvyFrame(sourceOffset + 3))
                    Dim red As Byte = 0
                    Dim green As Byte = 0
                    Dim blue As Byte = 0
                    ConvertYuvToRgb(yValue, uValue, vValue, red, green, blue)

                    Dim destination = x * 3
                    row(destination) = blue
                    row(destination + 1) = green
                    row(destination + 2) = red
                Next

                Dim destinationRow = IntPtr.Add(bitmapData.Scan0, y * bitmapData.Stride)
                Marshal.Copy(row, 0, destinationRow, row.Length)
            Next
        Finally
            videoBitmap.UnlockBits(bitmapData)
        End Try

        Dim composite As New Bitmap(previewVideoWidth + (meterOutputWidth * 2), previewVideoHeight, PixelFormat.Format24bppRgb)

        Using graphics As Graphics = Graphics.FromImage(composite)
            graphics.Clear(Color.Black)
            DrawReverseMeterRail(graphics, New Rectangle(0, 0, meterOutputWidth, previewVideoHeight), leftDbfs)
            graphics.DrawImage(videoBitmap, meterOutputWidth, 0, previewVideoWidth, previewVideoHeight)
            DrawReverseMeterRail(graphics, New Rectangle(meterOutputWidth + previewVideoWidth, 0, meterOutputWidth, previewVideoHeight), rightDbfs)
        End Using

        videoBitmap.Dispose()
        Return composite
    End Function

    Private Shared Sub ConvertYuvToRgb(yValue As Byte, uValue As Byte, vValue As Byte, ByRef red As Byte, ByRef green As Byte, ByRef blue As Byte)
        Dim c = CInt(yValue) - 16
        Dim d = CInt(uValue) - 128
        Dim e = CInt(vValue) - 128
        red = ClampToByte((298 * c + 409 * e + 128) >> 8)
        green = ClampToByte((298 * c - 100 * d - 208 * e + 128) >> 8)
        blue = ClampToByte((298 * c + 516 * d + 128) >> 8)
    End Sub

    Private Shared Function ClampToByte(value As Integer) As Byte
        Return CByte(Math.Max(0, Math.Min(255, value)))
    End Function

    Private Shared Sub DrawReverseMeterRail(graphics As Graphics, bounds As Rectangle, dbfs As Double)
        Using fillBrush As New SolidBrush(Color.Black)
            graphics.FillRectangle(fillBrush, bounds)
        End Using

        Dim normalized = Math.Max(0.0R, Math.Min(1.0R, (dbfs + 60.0R) / 60.0R))

        If normalized > 0.001R Then
            Dim inset = 4
            Dim levelHeight = Math.Max(1, CInt(Math.Round((bounds.Height - inset * 2) * normalized, MidpointRounding.AwayFromZero)))
            Dim levelBounds As New Rectangle(bounds.X + inset, bounds.Bottom - inset - levelHeight, Math.Max(1, bounds.Width - inset * 2), levelHeight)
            Dim fillColor = If(dbfs > -9.0R, Color.FromArgb(232, 181, 105), Color.FromArgb(91, 190, 125))

            Using levelBrush As New SolidBrush(fillColor)
                graphics.FillRectangle(levelBrush, levelBounds)
            End Using
        End If

        Using borderPen As New Pen(Color.FromArgb(86, 97, 109), 2.0F)
            graphics.DrawRectangle(borderPen, bounds.X, bounds.Y, Math.Max(0, bounds.Width - 1), Math.Max(0, bounds.Height - 1))
        End Using
    End Sub

    Private Sub PumpReverseAudioFrame(frameDuration As TimeSpan, previewOnly As Boolean)
        Dim audioQueue = reverseAudio

        If audioQueue Is Nothing Then
            Return
        End If

        Dim audioFrame = audioQueue.ReadFrame(frameDuration)
        Dim audioByteCount = audioFrame.SampleFrames * ReverseAudioChannels * 4

        If audioFrame.HasAudio Then
            UpdateReverseAudioMeters(audioFrame.Pcm, audioFrame.SampleFrames)
        Else
            reverseAudioLeftDbfs = -90.0R
            reverseAudioRightDbfs = -90.0R
        End If

        reverseWaveAudioOutput?.Enqueue(audioFrame.Pcm, audioByteCount)
        WriteReverseDeckLinkAudio(audioFrame.Pcm, audioFrame.SampleFrames, previewOnly)
    End Sub

    Private Sub WriteReverseDeckLinkAudio(pcm As Byte(), sampleFrames As Integer, previewOnly As Boolean)
        If Not reverseDeckLinkAudioEnabled OrElse previewOnly OrElse sampleFrames <= 0 Then
            Return
        End If

        Dim audioByteCount = sampleFrames * ReverseAudioChannels * 4

        If reverseDeckLinkAudioOutput IsNot Nothing Then
            If Not reverseDeckLinkAudioOutput.Enqueue(pcm, audioByteCount, sampleFrames) Then
                reverseDeckLinkAudioEnabled = False
            End If
        End If
    End Sub

    Private Sub UpdateReverseAudioMeters(pcm As Byte(), sampleFrames As Integer)
        Dim bytesPerSampleFrame = ReverseAudioChannels * 4

        If pcm Is Nothing OrElse sampleFrames <= 0 OrElse pcm.Length < bytesPerSampleFrame Then
            reverseAudioLeftDbfs = -90.0R
            reverseAudioRightDbfs = -90.0R
            Return
        End If

        Dim usableSampleFrames = Math.Min(sampleFrames, pcm.Length \ bytesPerSampleFrame)
        Dim peakLeft = 0L
        Dim peakRight = 0L

        For sampleFrame = 0 To usableSampleFrames - 1
            Dim offset = sampleFrame * bytesPerSampleFrame
            Dim left = BinaryPrimitives.ReadInt32LittleEndian(pcm.AsSpan(offset, 4))
            Dim right = BinaryPrimitives.ReadInt32LittleEndian(pcm.AsSpan(offset + 4, 4))
            peakLeft = Math.Max(peakLeft, Math.Abs(CLng(left)))
            peakRight = Math.Max(peakRight, Math.Abs(CLng(right)))
        Next

        reverseAudioLeftDbfs = ToDbfs(peakLeft)
        reverseAudioRightDbfs = ToDbfs(peakRight)
    End Sub

    Private Shared Function ToDbfs(peak As Long) As Double
        If peak <= 0 Then
            Return -90.0R
        End If

        Dim normalized = Math.Min(1.0R, peak / CDbl(Integer.MaxValue))
        Return Math.Max(-90.0R, 20.0R * Math.Log10(normalized))
    End Function

    Private Shared Function GetReverseAudioMonitorGain(speed As Double) As Double
        If speed >= 10.0R Then
            Return 0.45R
        End If

        If speed >= 5.0R Then
            Return 0.65R
        End If

        Return 0.85R
    End Function

    Private Sub DisposeReverseAudio()
        reverseDeckLinkAudioEnabled = False
        reverseDeckLinkAudioOutput?.Dispose()
        reverseDeckLinkAudioOutput = Nothing
        reverseWaveAudioOutput?.Dispose()
        reverseWaveAudioOutput = Nothing
        reverseAudio?.Dispose()
        reverseAudio = Nothing
        reverseAudioLeftDbfs = -90.0R
        reverseAudioRightDbfs = -90.0R
    End Sub

    Private Sub SetReverseAudioStatus(message As String)
        If String.IsNullOrWhiteSpace(message) OrElse IsDisposed Then
            Return
        End If

        If InvokeRequired Then
            Try
                BeginInvoke(New Action(Of String)(AddressOf SetReverseAudioStatus), message)
            Catch
            End Try

            Return
        End If

        Dim warning = IsDeckLinkOutputWarning(message) OrElse
            message.IndexOf("unavailable", StringComparison.OrdinalIgnoreCase) >= 0 OrElse
            message.IndexOf("disabled", StringComparison.OrdinalIgnoreCase) >= 0 OrElse
            message.IndexOf("stopped", StringComparison.OrdinalIgnoreCase) >= 0

        If warning Then
            SetStatus(message, warning:=True)
        End If
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
            runner.Start(ffmpegPath, BuildPreviewArguments(filePath, fileHasAudio, startOffset, playbackSpeedMultiplier, loopCheckBox.Checked), AppContext.BaseDirectory)
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

    Private Async Function RenderSeekFrameAsync(filePath As String, startOffset As TimeSpan, updateDeckLink As Boolean, requestGeneration As Integer, cancellationToken As CancellationToken) As Task(Of Boolean)
        Dim ffmpegPath = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe")

        If Not File.Exists(ffmpegPath) Then
            SetStatus($"ffmpeg.exe not found in {AppContext.BaseDirectory}", warning:=True)
            Return False
        End If

        Dim deviceName = TryCast(outputDeviceComboBox.SelectedItem, String)
        Dim outputMode = TryCast(outputModeComboBox.SelectedItem, DeckLinkOutputMode)
        Dim useDeckLinkOutput = updateDeckLink AndAlso Not IsNoDeckLinkOutputDevice(deviceName) AndAlso outputMode IsNot Nothing

        Try
            If Not updateDeckLink AndAlso isScrubberDragging Then
                Await RenderSeekPreviewBurstAsync(ffmpegPath, filePath, startOffset, requestGeneration, cancellationToken)
                Return False
            End If

            Dim frame As SeekDecodedFrame

            If useDeckLinkOutput Then
                frame = Await Task.Run(
                    Function()
                        Return SeekFrameDecoder.DecodeFrame(ffmpegPath, filePath, outputMode.Width, outputMode.Height, 2, "uyvy422", outputMode.IsInterlaced, startOffset, cancellationToken)
                    End Function,
                    cancellationToken)
            Else
                frame = Await Task.Run(
                    Function()
                        Return SeekFrameDecoder.DecodeFrame(ffmpegPath, filePath, 900, 540, 3, "bgr24", False, startOffset, cancellationToken)
                    End Function,
                    cancellationToken)
            End If

            If requestGeneration <> scrubFrameRequestGeneration OrElse cancellationToken.IsCancellationRequested Then
                Return False
            End If

            reverseAudioLeftDbfs = -90.0R
            reverseAudioRightDbfs = -90.0R

            If String.Equals(frame.PixelFormat, "uyvy422", StringComparison.OrdinalIgnoreCase) Then
                DisplayReversePreviewUyvyFrame(frame.Data, frame.Width, frame.Height)
            Else
                DisplayReversePreviewFrame(frame.Data, frame.Width, frame.Height)
            End If

            If Not useDeckLinkOutput Then
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
                Await runner.PrepareCachedFrameOutputAsync(deviceName, outputMode.FormatCode, outputMode.Width, outputMode.Height, outputMode.FrameRate, enableAudio:=False, cancellationToken)
                cancellationToken.ThrowIfCancellationRequested()
                Await runner.DisplayCachedFrameAsync(deviceName, outputMode.FormatCode, outputMode.Width, outputMode.Height, outputMode.FrameRate, frame.Data, cancellationToken)

                If requestGeneration <> scrubFrameRequestGeneration OrElse cancellationToken.IsCancellationRequested Then
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
        Catch ex As OperationCanceledException
            Return False
        Catch ex As Exception
            If requestGeneration = scrubFrameRequestGeneration AndAlso Not cancellationToken.IsCancellationRequested Then
                SetStatus($"Unable to show scrub frame: {ex.Message}", warning:=True)
            End If

            Return False
        End Try
    End Function

    Private Async Function RenderSeekPreviewBurstAsync(ffmpegPath As String, filePath As String, startOffset As TimeSpan, requestGeneration As Integer, cancellationToken As CancellationToken) As Task
        Dim cache = EnsureScrubPreviewCache(ffmpegPath, filePath)
        Dim direction = GetScrubPreviewDirection(startOffset)
        Dim frames = Await Task.Run(
            Function()
                Return cache.GetPreviewRun(startOffset, direction, SeekPreviewBurstFrameCount, cancellationToken)
            End Function,
            cancellationToken)

        For Each frame In frames
            cancellationToken.ThrowIfCancellationRequested()

            If requestGeneration <> scrubFrameRequestGeneration Then
                Return
            End If

            DisplayScrubPreviewFrame(frame, requestGeneration, cancellationToken)

            If frame IsNot frames(frames.Count - 1) Then
                Await Task.Delay(SeekPreviewFrameDelayMs, cancellationToken)
            End If
        Next
    End Function

    Private Function EnsureScrubPreviewCache(ffmpegPath As String, filePath As String) As ScrubPreviewFrameCache
        If scrubPreviewCache IsNot Nothing AndAlso scrubPreviewCache.Matches(filePath, SeekPreviewProxyWidth, SeekPreviewProxyHeight, "bgr24") Then
            Return scrubPreviewCache
        End If

        scrubPreviewCache?.Dispose()
        scrubPreviewCache = New ScrubPreviewFrameCache(ffmpegPath, filePath, SeekPreviewProxyWidth, SeekPreviewProxyHeight, 3, "bgr24", False, GetPlaybackFrameDuration(), GetSelectedDuration(), Nothing)
        Return scrubPreviewCache
    End Function

    Private Function GetScrubPreviewDirection(startOffset As TimeSpan) As Integer
        Dim direction = 1

        If lastScrubPreviewOffset.HasValue AndAlso startOffset < lastScrubPreviewOffset.Value Then
            direction = -1
        End If

        lastScrubPreviewOffset = startOffset
        Return direction
    End Function

    Private Sub PostSeekPreviewFrame(frame As SeekDecodedFrame, requestGeneration As Integer, cancellationToken As CancellationToken)
        If frame Is Nothing OrElse IsDisposed OrElse Not IsHandleCreated OrElse cancellationToken.IsCancellationRequested Then
            Return
        End If

        If requestGeneration <> scrubFrameRequestGeneration Then
            Return
        End If

        If InvokeRequired Then
            Try
                Dim frameForUi = frame
                BeginInvoke(New MethodInvoker(Sub() DisplaySeekPreviewFrame(frameForUi, requestGeneration, cancellationToken)))
            Catch ex As ObjectDisposedException
            Catch ex As InvalidOperationException
            End Try

            Return
        End If

        DisplaySeekPreviewFrame(frame, requestGeneration, cancellationToken)
    End Sub

    Private Sub DisplaySeekPreviewFrame(frame As SeekDecodedFrame, requestGeneration As Integer, cancellationToken As CancellationToken)
        If frame Is Nothing OrElse IsDisposed OrElse cancellationToken.IsCancellationRequested Then
            Return
        End If

        If requestGeneration <> scrubFrameRequestGeneration Then
            Return
        End If

        reverseAudioLeftDbfs = -90.0R
        reverseAudioRightDbfs = -90.0R

        If String.Equals(frame.PixelFormat, "uyvy422", StringComparison.OrdinalIgnoreCase) Then
            DisplayReversePreviewUyvyFrame(frame.Data, frame.Width, frame.Height)
        Else
            DisplayReversePreviewFrame(frame.Data, frame.Width, frame.Height)
        End If
    End Sub

    Private Sub DisplayScrubPreviewFrame(frame As ScrubPreviewFrame, requestGeneration As Integer, cancellationToken As CancellationToken)
        If frame Is Nothing OrElse IsDisposed OrElse cancellationToken.IsCancellationRequested Then
            Return
        End If

        If requestGeneration <> scrubFrameRequestGeneration Then
            Return
        End If

        reverseAudioLeftDbfs = -90.0R
        reverseAudioRightDbfs = -90.0R

        If String.Equals(frame.PixelFormat, "uyvy422", StringComparison.OrdinalIgnoreCase) Then
            DisplayReversePreviewUyvyFrame(frame.Data, frame.Width, frame.Height)
        Else
            DisplayReversePreviewFrame(frame.Data, frame.Width, frame.Height)
        End If
    End Sub

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
            Await runner.StartPlaybackAsync(ffmpegPath, filePath, deviceName, outputMode.FormatCode, outputMode.Width, outputMode.Height, outputMode.FrameRate, outputMode.IsInterlaced, fileHasAudio, startOffset, playbackSpeedMultiplier, loopCheckBox.Checked)
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
            runner.Start(ffplayPath, BuildAudioMonitorArguments(filePath, startOffset, playbackSpeed, loopCheckBox.Checked), AppContext.BaseDirectory)
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

    Private Shared Function BuildAudioMonitorArguments(filePath As String, startOffset As TimeSpan, playbackSpeed As Double, isLooping As Boolean) As String
        Dim builder As New StringBuilder("-hide_banner -loglevel warning -nostats -nodisp -autoexit -volume 100 ")
        If isLooping AndAlso Not IsImageFile(filePath) Then
            builder.Append("-loop 0 ")
        End If
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

    Private Shared Function BuildPreviewArguments(filePath As String, hasAudioStream As Boolean, startOffset As TimeSpan, playbackSpeed As Double, isLooping As Boolean) As String
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
        Else
            If isLooping Then
                builder.Append("-stream_loop -1 ")
            End If
            If Math.Abs(normalizedSpeed - 1.0R) < 0.001R Then
                builder.Append("-re ")
            Else
                builder.Append("-readrate ").Append(speedNumber).Append(" ")
            End If
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
        UpdateFullscreenPreviewImage(frame)
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
        Dim speedControlsEnabled = Not isLoadingFiles AndAlso Not isStoppingPreview AndAlso Not isStoppingOutput

        For Each button In speedPresetButtons
            button.Enabled = speedControlsEnabled
        Next

        UpdateMarkControls()
    End Sub

    Private Sub UpdateMarkControls()
        Dim controlsEnabled = IsScrubberLoaded() AndAlso scrubberTrackBar.Enabled AndAlso
            Not isLoadingFiles AndAlso Not isStoppingPreview AndAlso Not isStoppingOutput AndAlso Not isSeekingPlayback

        For Each button In markButtons
            button.Enabled = controlsEnabled
        Next

        markInTextBox.Enabled = controlsEnabled
        markOutTextBox.Enabled = controlsEnabled
    End Sub

    Private Function IsPlaybackActive() As Boolean
        Return playbackPositionTimer.Enabled OrElse shuttlePlaybackActive OrElse previewRunner IsNot Nothing OrElse (outputRunner IsNot Nothing AndAlso Not outputRunnerIsScrubHold)
    End Function

    Private Sub UpdateSpeedControls()
        Dim speed = NormalizePlaybackSpeed(playbackSpeedMultiplier)

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
        markControlsPanel.BackColor = background
        previewPanel.BackColor = background
        browserSplit.BackColor = background

        folderLabel.ForeColor = foreground
        outputDeviceLabel.ForeColor = foreground
        outputModeLabel.ForeColor = foreground
        searchLabel.ForeColor = foreground
        loopCheckBox.ForeColor = foreground
        previewStateLabel.ForeColor = secondaryForeground
        selectedFileLabel.ForeColor = secondaryForeground
        statusLabel.ForeColor = secondaryForeground

        rootPathTextBox.BackColor = inputBackground
        rootPathTextBox.ForeColor = foreground
        outputDeviceComboBox.BackColor = inputBackground
        outputDeviceComboBox.ForeColor = foreground
        outputModeComboBox.BackColor = inputBackground
        outputModeComboBox.ForeColor = foreground
        searchTextBox.BackColor = inputBackground
        searchTextBox.ForeColor = foreground
        markInTextBox.BackColor = inputBackground
        markInTextBox.ForeColor = foreground
        markOutTextBox.BackColor = inputBackground
        markOutTextBox.ForeColor = foreground

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
        StyleButton(clearSearchButton, foreground)
        StyleButton(previewButton, foreground)
        StyleButton(stopPreviewButton, foreground)
        StyleButton(fullPreviewButton, foreground)
        For Each button In speedPresetButtons
            StyleButton(button, foreground)
        Next
        For Each button In markButtons
            StyleButton(button, foreground)
        Next
        ApplyContextMenuTheme(filesContextMenu, darkModeEnabledValue)
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

    Private Shared Sub ApplyContextMenuTheme(menu As ContextMenuStrip, dark As Boolean)
        menu.RenderMode = ToolStripRenderMode.Professional
        menu.Renderer = New AppMenuRenderer(dark)
        menu.BackColor = If(dark, Color.FromArgb(30, 35, 40), Color.FromArgb(246, 248, 250))
        menu.ForeColor = If(dark, Color.FromArgb(239, 244, 248), Color.FromArgb(24, 29, 34))
        menu.Font = New Font("Segoe UI", 9.0F, FontStyle.Regular, GraphicsUnit.Point)
        menu.ShowImageMargin = False
        menu.Padding = New Padding(2, 4, 2, 4)

        For Each item As ToolStripItem In menu.Items
            ApplyToolStripItemTheme(item, dark)
        Next
    End Sub

    Private Shared Sub ApplyToolStripItemTheme(item As ToolStripItem, dark As Boolean)
        Dim backColor = If(dark, Color.FromArgb(30, 35, 40), Color.FromArgb(246, 248, 250))
        Dim textColor = If(dark, Color.FromArgb(239, 244, 248), Color.FromArgb(24, 29, 34))
        Dim disabledTextColor = If(dark, Color.FromArgb(120, 130, 140), Color.FromArgb(160, 168, 176))

        item.BackColor = backColor
        item.ForeColor = If(item.Enabled, textColor, disabledTextColor)
        item.Font = New Font("Segoe UI", 9.0F, FontStyle.Regular, GraphicsUnit.Point)
        item.Margin = New Padding(0)
        item.Padding = New Padding(10, 3, 18, 3)

        Dim menuItem = TryCast(item, ToolStripMenuItem)
        If menuItem IsNot Nothing Then
            menuItem.DropDown.Renderer = New AppMenuRenderer(dark)
            menuItem.DropDown.BackColor = backColor
            menuItem.DropDown.ForeColor = textColor
            menuItem.DropDown.Padding = New Padding(2, 4, 2, 4)

            Dim dropDownMenu = TryCast(menuItem.DropDown, ToolStripDropDownMenu)
            If dropDownMenu IsNot Nothing Then
                dropDownMenu.ShowImageMargin = False
            End If

            For Each child As ToolStripItem In menuItem.DropDownItems
                ApplyToolStripItemTheme(child, dark)
            Next
        End If
    End Sub

    Private NotInheritable Class AppMenuRenderer
        Inherits ToolStripProfessionalRenderer

        Private ReadOnly _dark As Boolean

        Public Sub New(dark As Boolean)
            _dark = dark
        End Sub

        Protected Overrides Sub OnRenderToolStripBackground(e As ToolStripRenderEventArgs)
            Dim backColor = If(_dark, Color.FromArgb(30, 35, 40), Color.FromArgb(246, 248, 250))
            Using brush As New SolidBrush(backColor)
                e.Graphics.FillRectangle(brush, e.AffectedBounds)
            End Using
        End Sub

        Protected Overrides Sub OnRenderToolStripBorder(e As ToolStripRenderEventArgs)
            Dim borderColor = If(_dark, Color.FromArgb(64, 72, 82), Color.FromArgb(206, 214, 222))
            Using pen As New Pen(borderColor)
                Dim bounds = New Rectangle(Point.Empty, e.ToolStrip.Size)
                bounds.Width -= 1
                bounds.Height -= 1
                e.Graphics.DrawRectangle(pen, bounds)
            End Using
        End Sub

        Protected Overrides Sub OnRenderMenuItemBackground(e As ToolStripItemRenderEventArgs)
            Dim hoverColor = If(_dark, Color.FromArgb(32, 116, 190), Color.FromArgb(41, 128, 185))
            Dim backColor = If(_dark, Color.FromArgb(30, 35, 40), Color.FromArgb(246, 248, 250))
            Dim itemColor = If(e.Item.Selected AndAlso e.Item.Enabled, hoverColor, backColor)
            Using brush As New SolidBrush(itemColor)
                e.Graphics.FillRectangle(brush, New Rectangle(Point.Empty, e.Item.Size))
            End Using
        End Sub

        Protected Overrides Sub OnRenderItemText(e As ToolStripItemTextRenderEventArgs)
            Dim textColor = If(_dark, Color.FromArgb(239, 244, 248), Color.FromArgb(24, 29, 34))
            Dim disabledTextColor = If(_dark, Color.FromArgb(120, 130, 140), Color.FromArgb(160, 168, 176))
            Dim item = e.Item
            e.TextColor = If(item.Enabled, If(item.Selected, Color.White, textColor), disabledTextColor)
            MyBase.OnRenderItemText(e)
        End Sub

        Protected Overrides Sub OnRenderArrow(e As ToolStripArrowRenderEventArgs)
            Dim textColor = If(_dark, Color.FromArgb(239, 244, 248), Color.FromArgb(24, 29, 34))
            Dim disabledTextColor = If(_dark, Color.FromArgb(120, 130, 140), Color.FromArgb(160, 168, 176))
            Dim item = e.Item
            e.ArrowColor = If(item IsNot Nothing AndAlso item.Enabled, If(item.Selected, Color.White, textColor), disabledTextColor)
            MyBase.OnRenderArrow(e)
        End Sub

        Protected Overrides Sub OnRenderSeparator(e As ToolStripSeparatorRenderEventArgs)
            Dim borderColor = If(_dark, Color.FromArgb(64, 72, 82), Color.FromArgb(206, 214, 222))
            Using pen As New Pen(borderColor)
                Dim y = e.Item.Height \ 2
                e.Graphics.DrawLine(pen, 8, y, e.Item.Width - 8, y)
            End Using
        End Sub
    End Class

    Private Sub ClearPreviewImage()
        Dim previousImage = previewPictureBox.Image
        previewPictureBox.Image = Nothing

        If fullscreenPreviewForm IsNot Nothing AndAlso Not fullscreenPreviewForm.IsDisposed Then
            fullscreenPreviewForm.ClearPreviewImage()
        End If

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
            Dim scrubCache = scrubPreviewCache
            previewRunner = Nothing
            outputRunner = Nothing
            audioMonitorRunner = Nothing
            scrubPreviewCache = Nothing
            scrubPreviewTimer.Stop()
            playbackPositionTimer.Stop()
            growingDurationRefreshTimer.Stop()
            scrubPreviewTimer.Dispose()
            playbackPositionTimer.Dispose()
            growingDurationRefreshTimer.Dispose()

            If runner IsNot Nothing Then
                runner.Dispose()
            End If

            If deckLinkRunner IsNot Nothing Then
                deckLinkRunner.Dispose()
            End If

            If audioRunner IsNot Nothing Then
                audioRunner.Dispose()
            End If

            If scrubCache IsNot Nothing Then
                scrubCache.Dispose()
            End If

            CloseFullscreenPreview()
            ClearPreviewImage()
        End If

        MyBase.Dispose(disposing)
    End Sub
End Class
