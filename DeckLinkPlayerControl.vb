Imports System.Diagnostics
Imports System.Drawing
Imports System.IO
Imports System.Collections.Generic
Imports System.ComponentModel
Imports System.Globalization
Imports System.Linq
Imports System.Text
Imports System.Threading.Tasks

Public Class DeckLinkPlayerControl
    Inherits UserControl

    Private Const LoadingNodeText As String = "Loading..."
    Private Const NoDeckLinkOutputText As String = "(No DeckLink output)"
    Private Const DefaultDeckLinkOutputDeviceName As String = "DeckLink SDI 4K"
    Private Const DeckLinkSdkOutputHelperFileName As String = "DeckLinkOutputHelper.exe"
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
    Private ReadOnly previewPictureBox As New PictureBox()
    Private ReadOnly previewStateLabel As New Label()
    Private ReadOnly statusLabel As New Label()

    Private WithEvents previewRunner As PreviewFrameReader
    Private WithEvents outputRunner As FfmpegProcessRunner
    Private WithEvents audioMonitorRunner As FfmpegProcessRunner
    Private darkModeEnabledValue As Boolean = True
    Private rootDirectoryPath As String
    Private selectedFilePath As String
    Private isLoadingFiles As Boolean
    Private isStoppingPreview As Boolean
    Private isStoppingOutput As Boolean
    Private speakerMonitorEnabledValue As Boolean
    Private durationProbeGeneration As Integer
    Private hasAppliedInitialBrowserSplit As Boolean
    Private outputRunnerUsesSdkHelper As Boolean
    Private lastDeckLinkOutputMessage As String

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
        AddHandler outputDeviceComboBox.SelectedIndexChanged, AddressOf OnOutputSelectionChanged
        AddHandler outputModeComboBox.SelectedIndexChanged, AddressOf OnOutputSelectionChanged
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
                StartAudioMonitor(selectedFilePath)
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
        rootLayout.RowStyles.Add(New RowStyle(SizeType.Absolute, 282.0F))
        rootLayout.RowStyles.Add(New RowStyle(SizeType.Absolute, 452.0F))
        rootLayout.RowStyles.Add(New RowStyle(SizeType.Absolute, 24.0F))
        rootLayout.Size = New Size(760, 850)

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
        browserSplit.Size = New Size(744, 310)
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
        previewPanel.RowCount = 2
        previewPanel.RowStyles.Add(New RowStyle(SizeType.Absolute, 36.0F))
        previewPanel.RowStyles.Add(New RowStyle(SizeType.Percent, 100.0F))

        previewToolbarPanel.Dock = DockStyle.Fill
        previewToolbarPanel.FlowDirection = FlowDirection.LeftToRight
        previewToolbarPanel.Margin = New Padding(0, 0, 0, 6)
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
        selectedFileLabel.Size = New Size(520, 20)
        selectedFileLabel.Text = "Select a file in the grid, then Play or double-click."
        selectedFileLabel.TextAlign = ContentAlignment.MiddleLeft

        previewToolbarPanel.Controls.Add(previewButton)
        previewToolbarPanel.Controls.Add(stopPreviewButton)
        previewToolbarPanel.Controls.Add(selectedFileLabel)

        previewPictureBox.BackColor = Color.Black
        previewPictureBox.BorderStyle = BorderStyle.FixedSingle
        previewPictureBox.Dock = DockStyle.Fill
        previewPictureBox.Margin = New Padding(0)
        previewPictureBox.SizeMode = PictureBoxSizeMode.Zoom

        previewStateLabel.AutoSize = False
        previewStateLabel.BackColor = Color.FromArgb(160, 0, 0, 0)
        previewStateLabel.Dock = DockStyle.Top
        previewStateLabel.ForeColor = Color.White
        previewStateLabel.Height = 24
        previewStateLabel.Text = "Preview stopped"
        previewStateLabel.TextAlign = ContentAlignment.MiddleCenter

        Dim previewSurface As New Panel() With {
            .Dock = DockStyle.Fill,
            .Margin = New Padding(0)
        }
        previewSurface.Controls.Add(previewPictureBox)
        previewSurface.Controls.Add(previewStateLabel)

        previewPanel.Controls.Add(previewToolbarPanel, 0, 0)
        previewPanel.Controls.Add(previewSurface, 0, 1)

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

        If deviceNames.Count = 0 Then
            outputDeviceComboBox.Items.Add(NoDeckLinkOutputText)
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
        SetStatus($"DeckLink output ready: {targetDevice}.")
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

        UpdatePreviewButtons()
    End Sub

    Private Async Sub OnFilesGridCellDoubleClick(sender As Object, e As DataGridViewCellEventArgs)
        If e.RowIndex < 0 Then
            Return
        End If

        Await StartSelectedPlaybackAsync()
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
        Await StopPlaybackAsync(clearImage:=False)
    End Sub

    Private Sub OnOutputSelectionChanged(sender As Object, e As EventArgs)
        SaveDeckLinkOutputSelection()
        UpdatePreviewButtons()
    End Sub

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

        If String.IsNullOrWhiteSpace(deviceName) OrElse
           String.Equals(deviceName, NoDeckLinkOutputText, StringComparison.OrdinalIgnoreCase) OrElse
           outputMode Is Nothing Then
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

            Dim durationText = Await Task.Run(Function() GetDurationText(ffprobePath, filePath))

            If probeGeneration <> durationProbeGeneration OrElse IsDisposed Then
                Return
            End If

            UpdateDurationCell(filePath, durationText)
        Next
    End Sub

    Private Sub UpdateDurationCell(filePath As String, durationText As String)
        If InvokeRequired Then
            BeginInvoke(New Action(Of String, String)(AddressOf UpdateDurationCell), filePath, durationText)
            Return
        End If

        For Each row As DataGridViewRow In filesGridView.Rows
            If row.Tag IsNot Nothing AndAlso String.Equals(TryCast(row.Tag, String), filePath, StringComparison.OrdinalIgnoreCase) Then
                row.Cells("Duration").Value = durationText
                Exit For
            End If
        Next
    End Sub

    Private Shared Function GetDurationText(ffprobePath As String, filePath As String) As String
        If IsImageFile(filePath) Then
            Return "Still"
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
                    Return "--"
                End If

                Dim output = process.StandardOutput.ReadToEnd().Trim()

                If Not process.WaitForExit(3000) Then
                    process.Kill(True)
                    Return "--"
                End If

                Dim seconds As Double

                If process.ExitCode <> 0 OrElse Not Double.TryParse(output, NumberStyles.Float, CultureInfo.InvariantCulture, seconds) OrElse seconds < 0 Then
                    Return "--"
                End If

                Return FormatDuration(TimeSpan.FromSeconds(seconds))
            End Using
        Catch
            Return "--"
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

    Private Async Function StartSelectedPlaybackAsync() As Task
        Dim filePath = GetSelectedFilePath()

        If String.IsNullOrWhiteSpace(filePath) Then
            SetStatus("Select a file from the grid first.", warning:=True)
            Return
        End If

        Await StopPlaybackAsync(clearImage:=True)
        Await StartPreviewAsync(filePath)
        Await StartOutputAsync(filePath)
        StartAudioMonitor(filePath)
    End Function

    Private Async Function StartPreviewAsync(filePath As String) As Task
        Await StopPreviewAsync(clearImage:=True)

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
            runner.Start(ffmpegPath, BuildPreviewArguments(filePath, fileHasAudio), AppContext.BaseDirectory)
            UpdatePreviewButtons()
        Catch ex As Exception
            previewRunner = Nothing
            previewStateLabel.Text = "Preview unavailable"
            previewStateLabel.Visible = True
            SetStatus($"Unable to start preview: {ex.Message}", warning:=True)
            UpdatePreviewButtons()
        End Try
    End Function

    Private Async Function StopPreviewAsync(Optional clearImage As Boolean = False) As Task
        Dim runner = previewRunner

        If runner Is Nothing OrElse isStoppingPreview Then
            If clearImage Then
                ClearPreviewImage()
            End If

            Return
        End If

        isStoppingPreview = True
        previewRunner = Nothing
        previewStateLabel.Text = "Stopping preview..."
        previewStateLabel.Visible = True
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

            previewStateLabel.Text = "Preview stopped"
            UpdatePreviewButtons()
        End Try
    End Function

    Private Async Function StartOutputAsync(filePath As String) As Task
        If outputRunner IsNot Nothing Then
            SetStatus("DeckLink output is already running.", warning:=True)
            Return
        End If

        Dim ffmpegPath = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe")

        If Not File.Exists(ffmpegPath) Then
            SetStatus($"ffmpeg.exe not found in {AppContext.BaseDirectory}", warning:=True)
            Return
        End If

        Dim deviceName = TryCast(outputDeviceComboBox.SelectedItem, String)

        If String.IsNullOrWhiteSpace(deviceName) OrElse String.Equals(deviceName, NoDeckLinkOutputText, StringComparison.OrdinalIgnoreCase) Then
            SetStatus("Choose a DeckLink output device first.", warning:=True)
            Return
        End If

        Dim outputMode = TryCast(outputModeComboBox.SelectedItem, DeckLinkOutputMode)

        If outputMode Is Nothing Then
            SetStatus("Choose a DeckLink output mode first.", warning:=True)
            Return
        End If

        Try
            Dim fileHasAudio = Await Task.Run(Function() ProbeHasAudioStream(filePath))
            Dim runner As New FfmpegProcessRunner()
            Dim helperPath = Path.Combine(AppContext.BaseDirectory, DeckLinkSdkOutputHelperFileName)
            Dim useSdkHelper = File.Exists(helperPath)
            Dim executablePath = If(useSdkHelper, helperPath, ffmpegPath)
            Dim arguments = If(useSdkHelper,
                BuildSdkDeckLinkOutputArguments(filePath, ffmpegPath, deviceName, outputMode, fileHasAudio),
                BuildDeckLinkOutputArguments(filePath, deviceName, outputMode, fileHasAudio))

            outputRunner = runner
            outputRunnerUsesSdkHelper = useSdkHelper
            lastDeckLinkOutputMessage = String.Empty
            SetStatus($"Starting SDI: {Path.GetFileName(filePath)} -> {deviceName} {outputMode.DisplayName}")
            runner.Start(executablePath, arguments, AppContext.BaseDirectory)
            SetStatus($"Playing SDI via {If(useSdkHelper, "DeckLink SDK", "FFmpeg")}: {Path.GetFileName(filePath)} -> {deviceName} {outputMode.DisplayName}")
        Catch ex As Exception
            outputRunner = Nothing
            outputRunnerUsesSdkHelper = False
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
        Dim forceStop = outputRunnerUsesSdkHelper
        outputRunnerUsesSdkHelper = False
        SetStatus("Stopping DeckLink output...")
        UpdatePreviewButtons()

        Try
            If forceStop Then
                Await Task.Run(Sub() runner.Dispose())
            Else
                Await Task.Run(Sub() runner.Stop())
                runner.Dispose()
            End If
            SetStatus("DeckLink output stopped.")
        Catch ex As Exception
            SetStatus($"DeckLink output stop failed: {ex.Message}", warning:=True)
        Finally
            isStoppingOutput = False
            UpdatePreviewButtons()
        End Try
    End Function

    Private Async Function StopPlaybackAsync(Optional clearImage As Boolean = False) As Task
        TearDownAudioMonitor(fast:=True)
        Await StopOutputAsync()
        Await StopPreviewAsync(clearImage)
    End Function

    Private Sub StartAudioMonitor(filePath As String)
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
            runner.Start(ffplayPath, BuildAudioMonitorArguments(filePath), AppContext.BaseDirectory)
            SetStatus($"Player audio listen active: {Path.GetFileName(filePath)}")
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

    Private Shared Function BuildAudioMonitorArguments(filePath As String) As String
        Return $"-hide_banner -loglevel warning -nostats -nodisp -autoexit -loop 0 -volume 100 -i {Quote(filePath)}"
    End Function

    Private Shared Function BuildSdkDeckLinkOutputArguments(filePath As String, ffmpegPath As String, deviceName As String, outputMode As DeckLinkOutputMode, hasAudioStream As Boolean) As String
        Dim videoFilter = $"scale={outputMode.Width}:{outputMode.Height}:force_original_aspect_ratio=decrease,pad={outputMode.Width}:{outputMode.Height}:(ow-iw)/2:(oh-ih)/2,setsar=1,fps={outputMode.FrameRate},setpts=N/({outputMode.FrameRate}*TB)"
        Dim builder As New StringBuilder()

        builder.Append("play ")
        builder.Append("--ffmpeg-path ").Append(Quote(ffmpegPath)).Append(" ")
        builder.Append("--input ").Append(Quote(filePath)).Append(" ")
        builder.Append("--device ").Append(Quote(deviceName)).Append(" ")
        builder.Append("--format-code ").Append(Quote(outputMode.FormatCode)).Append(" ")
        builder.Append("--video-size ").Append(Quote($"{outputMode.Width}x{outputMode.Height}")).Append(" ")
        builder.Append("--frame-rate ").Append(Quote(outputMode.FrameRate)).Append(" ")
        builder.Append("--pixel-format uyvy422 ")
        builder.Append("--audio-channels 2 ")
        builder.Append("--preroll 0.5 ")
        builder.Append("--video-filter ").Append(Quote(videoFilter)).Append(" ")
        builder.Append("--loop ")

        If Not hasAudioStream Then
            builder.Append("--no-audio ")
        End If

        Return builder.ToString().TrimEnd()
    End Function

    Private Shared Function BuildDeckLinkOutputArguments(filePath As String, deviceName As String, outputMode As DeckLinkOutputMode, hasAudioStream As Boolean) As String
        Dim fieldFilter = If(outputMode.IsInterlaced, ",setfield=tff", String.Empty)
        Dim filterGraph = $"scale={outputMode.Width}:{outputMode.Height}:force_original_aspect_ratio=decrease,pad={outputMode.Width}:{outputMode.Height}:(ow-iw)/2:(oh-ih)/2,setsar=1,fps={outputMode.FrameRate},setpts=N/({outputMode.FrameRate}*TB){fieldFilter},format=uyvy422"
        Dim builder As New StringBuilder()

        builder.Append("-hide_banner -loglevel info ")

        If IsImageFile(filePath) Then
            builder.Append("-loop 1 -framerate ").Append(outputMode.FrameRate).Append(" ")
        Else
            builder.Append("-stream_loop -1 -readrate 1 -readrate_initial_burst 2 -readrate_catchup 1.5 ")
        End If

        builder.Append("-i ").Append(Quote(filePath)).Append(" ")
        builder.Append("-map 0:v:0 ")

        If hasAudioStream Then
            builder.Append("-map 0:a:0 ")
        End If

        builder.Append("-vf ").Append(Quote(filterGraph)).Append(" ")
        builder.Append("-s:v ").Append(outputMode.Width).Append("x").Append(outputMode.Height).Append(" ")
        builder.Append("-r:v ").Append(outputMode.FrameRate).Append(" ")
        builder.Append("-fps_mode cfr ")
        builder.Append("-pix_fmt uyvy422 ")

        If outputMode.IsInterlaced Then
            builder.Append("-top 1 -field_order tt ")
        End If

        If hasAudioStream Then
            builder.Append("-af ").Append(Quote("aresample=48000:async=1:first_pts=0,aformat=sample_fmts=s16:channel_layouts=stereo,asetpts=N/SR/TB")).Append(" ")
            builder.Append("-ac 2 ")
            builder.Append("-ar 48000 ")
            builder.Append("-c:a pcm_s16le ")
        Else
            builder.Append("-an ")
        End If

        builder.Append("-f decklink -format_code ").Append(Quote(outputMode.FormatCode)).Append(" -preroll 2 ")
        builder.Append(Quote(deviceName))

        Return builder.ToString()
    End Function

    Private Shared Function BuildPreviewArguments(filePath As String, hasAudioStream As Boolean) As String
        Dim previewWidth = 900
        Dim previewHeight = 540
        Dim meterChannelWidth = 96
        Dim meterOutputWidth = 30
        Dim audioInputLabel = If(hasAudioStream, "[0:a]", "[1:a]")
        Dim rightMeterPan = "mono|c0=c1"
        Dim filterGraph = $"{audioInputLabel}aresample=48000,aformat=sample_fmts=s16:channel_layouts=stereo,apad,asetpts=N/SR/TB,asplit=2[left_meter_src][right_meter_src];" &
            $"[0:v]scale={previewWidth}:{previewHeight}:force_original_aspect_ratio=decrease,pad={previewWidth}:{previewHeight}:(ow-iw)/2:(oh-ih)/2,fps=25,setpts=N/(25*TB),format=yuv420p[video];" &
            $"[left_meter_src]pan=mono|c0=c0,showvolume=r=25:w={meterChannelWidth}:h={previewHeight}:f=0.92:b=2:t=0:v=1:dm=1:o=v:ds=log:p=0.18:m=r[left_bar_src];" &
            $"[left_bar_src]scale={meterOutputWidth}:{previewHeight},format=yuv420p[left_bar];" &
            $"[right_meter_src]pan={rightMeterPan},showvolume=r=25:w={meterChannelWidth}:h={previewHeight}:f=0.92:b=2:t=0:v=1:dm=1:o=v:ds=log:p=0.18:m=r[right_bar_src];" &
            $"[right_bar_src]scale={meterOutputWidth}:{previewHeight},format=yuv420p[right_bar];" &
            "[left_bar][video][right_bar]hstack=inputs=3:shortest=1[out]"
        Dim builder As New StringBuilder()

        builder.Append("-hide_banner -loglevel warning ")

        If IsImageFile(filePath) Then
            builder.Append("-loop 1 -framerate 25 ")
        Else
            builder.Append("-stream_loop -1 -re ")
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

        Dim previousImage = previewPictureBox.Image
        previewPictureBox.Image = frame
        previewStateLabel.Visible = False

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
        previewStateLabel.Text = If(exitCode = 0, "Preview stopped", $"Preview stopped (Exit {exitCode})")
        previewStateLabel.Visible = True
        SetStatus(previewStateLabel.Text, warning:=exitCode <> 0)
        UpdatePreviewButtons()
    End Sub

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
        outputRunnerUsesSdkHelper = False
        Dim finalMessage = If(exitCode = 0,
            "DeckLink output stopped.",
            If(String.IsNullOrWhiteSpace(lastDeckLinkOutputMessage),
                $"DeckLink output stopped (Exit {exitCode}).",
                $"DeckLink output failed: {lastDeckLinkOutputMessage}"))

        lastDeckLinkOutputMessage = String.Empty
        SetStatus(finalMessage, warning:=exitCode <> 0)
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
        Dim hasOutputDevice = outputDeviceComboBox.SelectedItem IsNot Nothing AndAlso
            Not String.Equals(TryCast(outputDeviceComboBox.SelectedItem, String), NoDeckLinkOutputText, StringComparison.OrdinalIgnoreCase)

        previewButton.Enabled = hasSelectedFile AndAlso hasOutputDevice AndAlso Not isLoadingFiles AndAlso Not isPreviewRunning AndAlso Not isOutputRunning AndAlso Not isStoppingPreview AndAlso Not isStoppingOutput
        stopPreviewButton.Enabled = (isPreviewRunning OrElse isOutputRunning) AndAlso Not isStoppingPreview AndAlso Not isStoppingOutput
        outputDeviceComboBox.Enabled = Not isOutputRunning AndAlso Not isStoppingOutput
        outputModeComboBox.Enabled = Not isOutputRunning AndAlso Not isStoppingOutput
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
        previewPanel.BackColor = background
        browserSplit.BackColor = background

        folderLabel.ForeColor = foreground
        outputDeviceLabel.ForeColor = foreground
        outputModeLabel.ForeColor = foreground
        selectedFileLabel.ForeColor = secondaryForeground
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

    Protected Overrides Sub Dispose(disposing As Boolean)
        If disposing Then
            Dim runner = previewRunner
            Dim deckLinkRunner = outputRunner
            Dim audioRunner = audioMonitorRunner
            previewRunner = Nothing
            outputRunner = Nothing
            audioMonitorRunner = Nothing

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
