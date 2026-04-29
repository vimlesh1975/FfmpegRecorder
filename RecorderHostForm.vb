Imports System.Diagnostics
Imports System.Drawing
Imports System.Globalization
Imports System.IO
Imports System.Runtime.InteropServices

Partial Public Class RecorderHostForm
    <StructLayout(LayoutKind.Sequential)>
    Private Structure FileTimeValue
        Public DwLowDateTime As UInteger
        Public DwHighDateTime As UInteger
    End Structure

    Private Structure SystemCpuSample
        Public IdleTicks As ULong
        Public KernelTicks As ULong
        Public UserTicks As ULong
    End Structure

    <DllImport("kernel32.dll", SetLastError:=True)>
    Private Shared Function GetSystemTimes(ByRef idleTime As FileTimeValue, ByRef kernelTime As FileTimeValue, ByRef userTime As FileTimeValue) As Boolean
    End Function

    Private ReadOnly systemCpuTimer As New Timer() With {.Interval = 1000}
    Private ReadOnly recordingDriveFreeSpaceTimer As New Timer() With {.Interval = 60000}
    Private ReadOnly recordingDirectoryPanel As New FlowLayoutPanel()
    Private ReadOnly recordingDirectoryLabel As New Label()
    Private ReadOnly recordingDirectoryTextBox As New TextBox()
    Private ReadOnly browseRecordingDirectoryButton As New Button()
    Private ReadOnly recordingDriveFreeSpaceLabel As New Label()
    Private isAdjustingCommonHeaderHeight As Boolean
    Private hasSystemCpuSample As Boolean
    Private lastSystemCpuSample As SystemCpuSample
    Private suppressSharedOperatorEvents As Boolean
    Private suppressRecordingDirectoryEvents As Boolean
    Private isDarkModeEnabled As Boolean = True

    Public Sub New()
        InitializeComponent()
        OrganizeCommonPanel()
        Text = $"{Text} {GetBuildTimestampSuffix()}"
        ApplyVisualTheme()

        AddHandler leftRecorderControl.CpuUsageChanged, AddressOf OnRecorderCpuUsageChanged
        AddHandler rightRecorderControl.CpuUsageChanged, AddressOf OnRecorderCpuUsageChanged
        AddHandler thirdRecorderControl.CpuUsageChanged, AddressOf OnRecorderCpuUsageChanged
        AddHandler fourthRecorderControl.CpuUsageChanged, AddressOf OnRecorderCpuUsageChanged
        AddHandler systemCpuTimer.Tick, AddressOf OnSystemCpuTimerTick
        AddHandler recordingDriveFreeSpaceTimer.Tick, AddressOf OnRecordingDriveFreeSpaceTimerTick
        AddHandler audioListenComboBox.SelectedIndexChanged, AddressOf OnAudioListenSelectionChanged
        AddHandler profileComboBox.SelectedIndexChanged, AddressOf OnSharedProfileChanged
        AddHandler intervalUpDown.ValueChanged, AddressOf OnSharedIntervalChanged
        AddHandler recordAllButton.Click, AddressOf OnRecordAllClicked
        AddHandler stopAllButton.Click, AddressOf OnStopAllClicked
        AddHandler openRecordingsButton.Click, AddressOf OnOpenRecordingsClicked
        AddHandler deleteAllButton.Click, AddressOf OnDeleteAllClicked
        AddHandler darkModeCheckBox.CheckedChanged, AddressOf OnDarkModeChanged
        AddHandler browseRecordingDirectoryButton.Click, AddressOf OnBrowseRecordingDirectoryClicked
        AddHandler recordingDirectoryTextBox.Leave, AddressOf OnRecordingDirectoryCommitted
        AddHandler recordingDirectoryTextBox.KeyDown, AddressOf OnRecordingDirectoryKeyDown
        AddHandler Load, AddressOf RecorderHostForm_Load
        AddHandler SizeChanged, AddressOf RecorderHostForm_SizeChanged

        profileComboBox.Items.Clear()
        profileComboBox.Items.AddRange(leftRecorderControl.AvailableProfileNames.ToArray())

        audioListenComboBox.Items.AddRange(New Object() {"Off", "CAM1", "CAM2", "CAM3", "CAM4"})
        audioListenComboBox.SelectedItem = "CAM1"
        InitializeRecordingDirectoryControls()
        RefreshRecordingDirectoryDisplay()

        ReadSystemCpuSample(lastSystemCpuSample)
        hasSystemCpuSample = True
        systemCpuTimer.Start()
        recordingDriveFreeSpaceTimer.Start()
        ApplyAudioListenSelection()
        UpdateCpuLabels()
    End Sub

    Private Sub OrganizeCommonPanel()
        commonPanel.SuspendLayout()
        commonPanel.Controls.Clear()
        commonPanel.ColumnStyles.Clear()
        commonPanel.RowStyles.Clear()
        commonPanel.ColumnCount = 2
        commonPanel.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100.0F))
        commonPanel.ColumnStyles.Add(New ColumnStyle(SizeType.AutoSize))
        commonPanel.RowCount = 2
        commonPanel.RowStyles.Add(New RowStyle(SizeType.Percent, 100.0F))
        commonPanel.RowStyles.Add(New RowStyle(SizeType.AutoSize))

        Dim leftSectionPanel As New FlowLayoutPanel() With {
            .AutoSize = False,
            .FlowDirection = FlowDirection.LeftToRight,
            .Dock = DockStyle.Fill,
            .Margin = New Padding(0),
            .Name = "commonLeftPanel",
            .Padding = New Padding(0),
            .WrapContents = True
        }

        leftSectionPanel.Controls.Add(BuildCommonSection("Setup", profileLabel, profileComboBox, intervalLabel, intervalUpDown))
        leftSectionPanel.Controls.Add(BuildCommonSection("Recording", recordAllButton, stopAllButton, openRecordingsButton, deleteAllButton))
        leftSectionPanel.Controls.Add(BuildCommonSection("Folder", recordingDirectoryPanel))
        leftSectionPanel.Controls.Add(BuildCommonSection("Audio", audioListenPanel))
        leftSectionPanel.Controls.Add(BuildCommonSection("View", darkModeCheckBox))

        Dim cpuSectionPanel = BuildCommonSection(
            "CPU",
            cam1CpuLabel,
            cam1CpuValueLabel,
            cam2CpuLabel,
            cam2CpuValueLabel,
            cam3CpuLabel,
            cam3CpuValueLabel,
            cam4CpuLabel,
            cam4CpuValueLabel)
        cpuSectionPanel.Name = "cameraCpuPanel"
        cpuSectionPanel.Margin = New Padding(0, 0, 0, 8)

        Dim rightSectionPanel As New FlowLayoutPanel() With {
            .AutoSize = True,
            .AutoSizeMode = AutoSizeMode.GrowAndShrink,
            .FlowDirection = FlowDirection.TopDown,
            .Margin = New Padding(16, 0, 0, 0),
            .Name = "commonRightPanel",
            .Padding = New Padding(0),
            .WrapContents = False
        }
        rightSectionPanel.Controls.Add(BuildPcCpuSection())
        rightSectionPanel.Controls.Add(BuildRecordingDriveSpaceSection())

        commonPanel.Controls.Add(leftSectionPanel, 0, 0)
        commonPanel.Controls.Add(rightSectionPanel, 1, 0)
        commonPanel.Controls.Add(cpuSectionPanel, 0, 1)
        commonPanel.SetRowSpan(rightSectionPanel, 2)

        commonPanel.ResumeLayout(True)
        UpdateCommonHeaderHeight()
    End Sub

    Private Sub InitializeRecordingDirectoryControls()
        recordingDirectoryPanel.AutoSize = True
        recordingDirectoryPanel.AutoSizeMode = AutoSizeMode.GrowAndShrink
        recordingDirectoryPanel.FlowDirection = FlowDirection.LeftToRight
        recordingDirectoryPanel.Margin = New Padding(0)
        recordingDirectoryPanel.Padding = New Padding(0)
        recordingDirectoryPanel.WrapContents = False

        recordingDirectoryLabel.AutoSize = True
        recordingDirectoryLabel.Font = New Font("Segoe UI", 9.0F, FontStyle.Regular, GraphicsUnit.Point, CByte(0))
        recordingDirectoryLabel.Margin = New Padding(0, 4, 6, 0)
        recordingDirectoryLabel.Text = "Recording Dir"

        recordingDirectoryTextBox.Font = New Font("Segoe UI", 9.0F, FontStyle.Regular, GraphicsUnit.Point, CByte(0))
        recordingDirectoryTextBox.Margin = New Padding(0)
        recordingDirectoryTextBox.Size = New Size(360, 23)

        browseRecordingDirectoryButton.Font = New Font("Segoe UI", 9.0F, FontStyle.Regular, GraphicsUnit.Point, CByte(0))
        browseRecordingDirectoryButton.Margin = New Padding(8, 0, 0, 0)
        browseRecordingDirectoryButton.Size = New Size(72, 24)
        browseRecordingDirectoryButton.Text = "Browse..."
        browseRecordingDirectoryButton.UseVisualStyleBackColor = True

        recordingDirectoryPanel.Controls.Add(recordingDirectoryLabel)
        recordingDirectoryPanel.Controls.Add(recordingDirectoryTextBox)
        recordingDirectoryPanel.Controls.Add(browseRecordingDirectoryButton)
    End Sub

    Private Function BuildCommonSection(title As String, ParamArray controls As Control()) As FlowLayoutPanel
        Dim sectionPanel As New FlowLayoutPanel() With {
            .AutoSize = True,
            .AutoSizeMode = AutoSizeMode.GrowAndShrink,
            .FlowDirection = FlowDirection.LeftToRight,
            .Margin = New Padding(0, 0, 12, 8),
            .Padding = New Padding(8, 4, 8, 4),
            .WrapContents = False
        }

        Dim titleLabel As New Label() With {
            .AutoSize = True,
            .Font = New Font("Segoe UI", 9.0F, FontStyle.Bold, GraphicsUnit.Point, CByte(0)),
            .Margin = New Padding(0, 4, 10, 0),
            .Text = title
        }

        sectionPanel.Controls.Add(titleLabel)

        For Each childControl In controls
            childControl.Margin = New Padding(0, Math.Max(0, childControl.Margin.Top), 8, 0)
            sectionPanel.Controls.Add(childControl)
        Next

        Return sectionPanel
    End Function

    Private Function BuildPcCpuSection() As FlowLayoutPanel
        Dim sectionPanel As New FlowLayoutPanel() With {
            .AutoSize = True,
            .AutoSizeMode = AutoSizeMode.GrowAndShrink,
            .FlowDirection = FlowDirection.LeftToRight,
            .Margin = New Padding(0, 0, 12, 8),
            .MinimumSize = New Size(340, 0),
            .Padding = New Padding(18, 12, 18, 12),
            .WrapContents = False,
            .Name = "pcCpuPanel"
        }

        totalCpuLabel.AutoSize = True
        totalCpuLabel.Font = New Font("Segoe UI", 10.0F, FontStyle.Bold, GraphicsUnit.Point, CByte(0))
        totalCpuLabel.Margin = New Padding(0, 11, 12, 0)
        totalCpuLabel.Text = "PC CPU"

        totalCpuValueLabel.AutoSize = False
        totalCpuValueLabel.Font = New Font("Consolas", 28.0F, FontStyle.Bold, GraphicsUnit.Point, CByte(0))
        totalCpuValueLabel.Margin = New Padding(0)
        totalCpuValueLabel.MinimumSize = New Size(210, 48)
        totalCpuValueLabel.Size = totalCpuValueLabel.MinimumSize
        totalCpuValueLabel.Text = "0.0%"
        totalCpuValueLabel.TextAlign = ContentAlignment.MiddleRight
        totalCpuValueLabel.ForeColor = GetCpuDisplayColor(0.0R)

        sectionPanel.Controls.Add(totalCpuLabel)
        sectionPanel.Controls.Add(totalCpuValueLabel)
        Return sectionPanel
    End Function

    Private Function BuildRecordingDriveSpaceSection() As FlowLayoutPanel
        Dim sectionPanel As New FlowLayoutPanel() With {
            .AutoSize = True,
            .AutoSizeMode = AutoSizeMode.GrowAndShrink,
            .FlowDirection = FlowDirection.TopDown,
            .Margin = New Padding(0, 0, 12, 8),
            .MinimumSize = New Size(340, 0),
            .Padding = New Padding(18, 0, 18, 12),
            .WrapContents = False,
            .Name = "driveFreeSpacePanel"
        }

        recordingDriveFreeSpaceLabel.AutoSize = False
        recordingDriveFreeSpaceLabel.Font = New Font("Segoe UI", 9.0F, FontStyle.Regular, GraphicsUnit.Point, CByte(0))
        recordingDriveFreeSpaceLabel.Margin = New Padding(0)
        recordingDriveFreeSpaceLabel.MinimumSize = New Size(280, 20)
        recordingDriveFreeSpaceLabel.Size = recordingDriveFreeSpaceLabel.MinimumSize
        recordingDriveFreeSpaceLabel.Text = "Free: -- MB"
        recordingDriveFreeSpaceLabel.TextAlign = ContentAlignment.MiddleRight

        sectionPanel.Controls.Add(recordingDriveFreeSpaceLabel)
        Return sectionPanel
    End Function

    Private Shared Function GetBuildTimestampSuffix() As String
        Dim executablePath = Application.ExecutablePath
        Dim executableNameTimestamp = GetTimestampSuffixFromExecutableName(executablePath)

        If Not String.IsNullOrWhiteSpace(executableNameTimestamp) Then
            Return executableNameTimestamp
        End If

        If Not String.IsNullOrWhiteSpace(executablePath) AndAlso File.Exists(executablePath) Then
            Return File.GetLastWriteTime(executablePath).ToString("ddMMyyyy_HHmmss")
        End If

        Return DateTime.Now.ToString("ddMMyyyy_HHmmss")
    End Function

    Private Shared Function GetTimestampSuffixFromExecutableName(executablePath As String) As String
        If String.IsNullOrWhiteSpace(executablePath) Then
            Return Nothing
        End If

        Dim executableName = Path.GetFileNameWithoutExtension(executablePath)
        Const timestampedExecutablePrefix As String = "FfmpegRecorder_"

        If Not executableName.StartsWith(timestampedExecutablePrefix, StringComparison.OrdinalIgnoreCase) Then
            Return Nothing
        End If

        Dim timestampText = executableName.Substring(timestampedExecutablePrefix.Length)
        Dim parsedTimestamp As DateTime

        If DateTime.TryParseExact(timestampText, "yyyyMMdd_HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, parsedTimestamp) Then
            Return timestampText
        End If

        Return Nothing
    End Function

    Private Sub ApplyVisualTheme()
        Dim appBackground = If(isDarkModeEnabled, Color.FromArgb(28, 31, 36), Color.FromArgb(232, 236, 238))
        Dim commonBackground = If(isDarkModeEnabled, Color.FromArgb(43, 45, 50), Color.FromArgb(236, 229, 214))
        Dim commonForeground = If(isDarkModeEnabled, Color.FromArgb(236, 239, 242), Color.FromArgb(36, 53, 69))
        Dim cam1Background = If(isDarkModeEnabled, Color.FromArgb(34, 43, 53), Color.FromArgb(232, 240, 248))
        Dim cam2Background = If(isDarkModeEnabled, Color.FromArgb(51, 44, 35), Color.FromArgb(246, 238, 224))
        Dim cam3Background = If(isDarkModeEnabled, Color.FromArgb(35, 48, 39), Color.FromArgb(232, 242, 233))
        Dim cam4Background = If(isDarkModeEnabled, Color.FromArgb(51, 38, 43), Color.FromArgb(245, 232, 235))

        BackColor = appBackground
        mainLayout.BackColor = appBackground
        contentLayout.BackColor = appBackground
        cameraGrid.BackColor = appBackground

        commonGroupBox.BackColor = commonBackground
        commonGroupBox.ForeColor = commonForeground
        commonPanel.BackColor = commonBackground
        ApplyCommonControlTheme(commonPanel, commonBackground, commonForeground)
        Dim pcCpuPanel = FindPcCpuPanel()
        If pcCpuPanel IsNot Nothing Then
            pcCpuPanel.BackColor = If(isDarkModeEnabled, Color.FromArgb(54, 59, 66), Color.FromArgb(250, 244, 234))
        End If
        totalCpuValueLabel.ForeColor = GetCpuDisplayColor(ParseCpuText(totalCpuValueLabel.Text))

        StyleCameraSection(cam1GroupBox, leftRecorderControl, cam1Background)
        StyleCameraSection(cam2GroupBox, rightRecorderControl, cam2Background)
        StyleCameraSection(cam3GroupBox, thirdRecorderControl, cam3Background)
        StyleCameraSection(cam4GroupBox, fourthRecorderControl, cam4Background)
        StyleStreamSection(commonBackground)
    End Sub

    Private Sub StyleCameraSection(groupBox As GroupBox, recorderControl As RecorderControl, baseColor As Color)
        Dim foreground = If(isDarkModeEnabled, Color.FromArgb(236, 239, 242), Color.FromArgb(52, 60, 68))

        groupBox.BackColor = baseColor
        groupBox.ForeColor = foreground
        recorderControl.BackColor = If(isDarkModeEnabled, LightenColor(baseColor, 8), LightenColor(baseColor, 10))
        recorderControl.DarkModeEnabled = isDarkModeEnabled
    End Sub

    Private Sub StyleStreamSection(baseColor As Color)
        Dim foreground = If(isDarkModeEnabled, Color.FromArgb(236, 239, 242), Color.FromArgb(52, 60, 68))

        streamGroupBox.BackColor = baseColor
        streamGroupBox.ForeColor = foreground
        streamRecorderControl.BackColor = If(isDarkModeEnabled, LightenColor(baseColor, 8), LightenColor(baseColor, 10))
        streamRecorderControl.DarkModeEnabled = isDarkModeEnabled
    End Sub

    Private Sub ApplyCommonControlTheme(parentControl As Control, background As Color, foreground As Color)
        For Each childControl As Control In parentControl.Controls
            If TypeOf childControl Is Label OrElse
               TypeOf childControl Is FlowLayoutPanel OrElse
               TypeOf childControl Is TableLayoutPanel Then
                childControl.BackColor = background
                childControl.ForeColor = foreground
            ElseIf TypeOf childControl Is CheckBox Then
                childControl.BackColor = background
                childControl.ForeColor = foreground
            ElseIf TypeOf childControl Is Button Then
                Dim button = DirectCast(childControl, Button)
                button.UseVisualStyleBackColor = Not isDarkModeEnabled
                button.BackColor = If(isDarkModeEnabled, Color.FromArgb(62, 67, 74), SystemColors.Control)
                button.ForeColor = foreground
                button.FlatStyle = If(isDarkModeEnabled, FlatStyle.Flat, FlatStyle.Standard)
                If isDarkModeEnabled Then
                    button.FlatAppearance.BorderColor = Color.FromArgb(90, 96, 104)
                End If
            ElseIf TypeOf childControl Is ComboBox OrElse TypeOf childControl Is NumericUpDown Then
                childControl.BackColor = If(isDarkModeEnabled, Color.FromArgb(37, 40, 45), SystemColors.Window)
                childControl.ForeColor = If(isDarkModeEnabled, Color.FromArgb(245, 247, 250), SystemColors.WindowText)
            ElseIf TypeOf childControl Is TextBox Then
                childControl.BackColor = If(isDarkModeEnabled, Color.FromArgb(37, 40, 45), SystemColors.Window)
                childControl.ForeColor = If(isDarkModeEnabled, Color.FromArgb(245, 247, 250), SystemColors.WindowText)
            End If

            If childControl.HasChildren Then
                ApplyCommonControlTheme(childControl, background, foreground)
            End If
        Next
    End Sub

    Private Function LightenColor(color As Color, amount As Integer) As Color
        Return Color.FromArgb(
            Math.Min(255, color.R + amount),
            Math.Min(255, color.G + amount),
            Math.Min(255, color.B + amount))
    End Function

    Private Iterator Function GetRecorderControls() As IEnumerable(Of RecorderControl)
        Yield leftRecorderControl
        Yield rightRecorderControl
        Yield thirdRecorderControl
        Yield fourthRecorderControl
    End Function

    Private Sub RecorderHostForm_Load(sender As Object, e As EventArgs)
        SyncSharedOperatorControlsFromRecorder(leftRecorderControl)
        UpdateCommonHeaderHeight()
    End Sub

    Private Sub RecorderHostForm_SizeChanged(sender As Object, e As EventArgs)
        UpdateCommonHeaderHeight()
    End Sub

    Private Sub OnSystemCpuTimerTick(sender As Object, e As EventArgs)
        UpdateSystemCpuLabel()
    End Sub

    Private Sub OnRecordingDriveFreeSpaceTimerTick(sender As Object, e As EventArgs)
        UpdateRecordingDriveFreeSpaceLabel()
    End Sub

    Private Sub OnRecorderCpuUsageChanged(sender As Object, e As RecorderControl.CpuUsageChangedEventArgs)
        If InvokeRequired Then
            BeginInvoke(New Action(Of Object, RecorderControl.CpuUsageChangedEventArgs)(AddressOf OnRecorderCpuUsageChanged), sender, e)
            Return
        End If

        UpdateCpuLabels()
    End Sub

    Private Sub OnAudioListenSelectionChanged(sender As Object, e As EventArgs)
        ApplyAudioListenSelection()
    End Sub

    Private Sub OnSharedProfileChanged(sender As Object, e As EventArgs)
        If suppressSharedOperatorEvents Then
            Return
        End If

        Dim selectedProfileName = TryCast(profileComboBox.SelectedItem, String)

        If String.IsNullOrWhiteSpace(selectedProfileName) Then
            Return
        End If

        For Each recorderControl In GetRecorderControls()
            recorderControl.SelectedProfileName = selectedProfileName
        Next
    End Sub

    Private Sub OnSharedIntervalChanged(sender As Object, e As EventArgs)
        If suppressSharedOperatorEvents Then
            Return
        End If

        Dim intervalSeconds = Decimal.ToInt32(intervalUpDown.Value)

        For Each recorderControl In GetRecorderControls()
            recorderControl.ClipIntervalSeconds = intervalSeconds
        Next
    End Sub

    Private Sub OnRecordAllClicked(sender As Object, e As EventArgs)
        For Each recorderControl In GetRecorderControls()
            If recorderControl.IncludeInRecordAll Then
                recorderControl.StartRecordingRequested()
            End If
        Next
    End Sub

    Private Sub OnStopAllClicked(sender As Object, e As EventArgs)
        For Each recorderControl In GetRecorderControls()
            recorderControl.StopRecordingRequested()
        Next
    End Sub

    Private Sub OnOpenRecordingsClicked(sender As Object, e As EventArgs)
        Dim outputFolderPath = RecordingDirectorySettings.GetRecordingDirectory()
        Directory.CreateDirectory(outputFolderPath)
        Process.Start(New ProcessStartInfo(outputFolderPath) With {.UseShellExecute = True})
    End Sub

    Private Sub OnDeleteAllClicked(sender As Object, e As EventArgs)
        Dim outputFolderPath = RecordingDirectorySettings.GetRecordingDirectory()

        If GetRecorderControls().Any(Function(recorderControl) recorderControl.IsRecording) OrElse streamRecorderControl.IsRecording Then
            MessageBox.Show(Me, "Stop all recordings before deleting files.", "Delete All", MessageBoxButtons.OK, MessageBoxIcon.Warning)
            Return
        End If

        If Not Directory.Exists(outputFolderPath) Then
            MessageBox.Show(Me, "There are no recordings to delete.", "Delete All", MessageBoxButtons.OK, MessageBoxIcon.Information)
            Return
        End If

        Dim filePaths = Directory.GetFiles(outputFolderPath)

        If filePaths.Length = 0 Then
            MessageBox.Show(Me, "There are no recordings to delete.", "Delete All", MessageBoxButtons.OK, MessageBoxIcon.Information)
            Return
        End If

        Dim confirmation = MessageBox.Show(
            Me,
            $"Delete all files in {outputFolderPath}?",
            "Delete All Recordings",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2)

        If confirmation <> DialogResult.Yes Then
            Return
        End If

        Dim deletedCount = 0

        For Each filePath In filePaths
            File.Delete(filePath)
            deletedCount += 1
        Next

        MessageBox.Show(Me, $"Deleted {deletedCount} recording file(s).", "Delete All", MessageBoxButtons.OK, MessageBoxIcon.Information)
    End Sub

    Private Sub OnDarkModeChanged(sender As Object, e As EventArgs)
        isDarkModeEnabled = darkModeCheckBox.Checked
        ApplyVisualTheme()
    End Sub

    Private Sub OnBrowseRecordingDirectoryClicked(sender As Object, e As EventArgs)
        Using folderDialog As New FolderBrowserDialog()
            folderDialog.Description = "Choose the recording directory"
            folderDialog.SelectedPath = RecordingDirectorySettings.GetRecordingDirectory()
            folderDialog.ShowNewFolderButton = True

            If folderDialog.ShowDialog(Me) <> DialogResult.OK Then
                Return
            End If

            recordingDirectoryTextBox.Text = folderDialog.SelectedPath
            CommitRecordingDirectoryChange()
        End Using
    End Sub

    Private Sub OnRecordingDirectoryCommitted(sender As Object, e As EventArgs)
        CommitRecordingDirectoryChange()
    End Sub

    Private Sub OnRecordingDirectoryKeyDown(sender As Object, e As KeyEventArgs)
        If e.KeyCode <> Keys.Enter Then
            Return
        End If

        e.Handled = True
        e.SuppressKeyPress = True
        CommitRecordingDirectoryChange()
    End Sub

    Private Sub CommitRecordingDirectoryChange()
        If suppressRecordingDirectoryEvents Then
            Return
        End If

        Try
            Dim savedPath = RecordingDirectorySettings.SaveRecordingDirectory(recordingDirectoryTextBox.Text)
            RefreshRecordingDirectoryDisplay(savedPath)
        Catch ex As Exception
            RefreshRecordingDirectoryDisplay()
            MessageBox.Show(Me, $"Unable to use that recording directory.{Environment.NewLine}{ex.Message}", "Recording Directory", MessageBoxButtons.OK, MessageBoxIcon.Warning)
        End Try
    End Sub

    Private Sub RefreshRecordingDirectoryDisplay(Optional directoryPath As String = Nothing)
        suppressRecordingDirectoryEvents = True

        Try
            Dim selectedDirectoryPath = If(String.IsNullOrWhiteSpace(directoryPath), RecordingDirectorySettings.GetRecordingDirectory(), directoryPath)
            recordingDirectoryTextBox.Text = selectedDirectoryPath
            UpdateRecordingDriveFreeSpaceLabel(selectedDirectoryPath)
        Finally
            suppressRecordingDirectoryEvents = False
        End Try
    End Sub

    Private Sub UpdateRecordingDriveFreeSpaceLabel(Optional directoryPath As String = Nothing)
        Dim selectedDirectoryPath = If(String.IsNullOrWhiteSpace(directoryPath), recordingDirectoryTextBox.Text, directoryPath)
        recordingDriveFreeSpaceLabel.Text = GetRecordingDriveFreeSpaceText(selectedDirectoryPath)
    End Sub

    Private Function GetRecordingDriveFreeSpaceText(directoryPath As String) As String
        Try
            Dim normalizedPath = RecordingDirectorySettings.NormalizeRecordingDirectory(directoryPath)
            Dim driveRoot = Path.GetPathRoot(normalizedPath)

            If String.IsNullOrWhiteSpace(driveRoot) Then
                Return "Free: unavailable"
            End If

            Dim drive = New DriveInfo(driveRoot)

            If Not drive.IsReady Then
                Return $"Free: unavailable on {drive.Name.TrimEnd("\"c)}"
            End If

            Dim freeSpaceInMb = CLng(Math.Floor(drive.AvailableFreeSpace / (1024.0R * 1024.0R)))
            Return $"Free: {freeSpaceInMb:N0} MB on {drive.Name.TrimEnd("\"c)}"
        Catch
            Return "Free: unavailable"
        End Try
    End Function

    Private Sub ApplyAudioListenSelection()
        Dim selectedCameraName = If(TryCast(audioListenComboBox.SelectedItem, String), "Off")

        leftRecorderControl.SpeakerMonitorEnabled = String.Equals(selectedCameraName, "CAM1", StringComparison.OrdinalIgnoreCase)
        rightRecorderControl.SpeakerMonitorEnabled = String.Equals(selectedCameraName, "CAM2", StringComparison.OrdinalIgnoreCase)
        thirdRecorderControl.SpeakerMonitorEnabled = String.Equals(selectedCameraName, "CAM3", StringComparison.OrdinalIgnoreCase)
        fourthRecorderControl.SpeakerMonitorEnabled = String.Equals(selectedCameraName, "CAM4", StringComparison.OrdinalIgnoreCase)
    End Sub

    Private Sub SyncSharedOperatorControlsFromRecorder(sourceRecorder As RecorderControl)
        suppressSharedOperatorEvents = True

        Try
            profileComboBox.SelectedItem = sourceRecorder.SelectedProfileName
            intervalUpDown.Value = Math.Max(intervalUpDown.Minimum, Math.Min(intervalUpDown.Maximum, sourceRecorder.ClipIntervalSeconds))
        Finally
            suppressSharedOperatorEvents = False
        End Try

        OnSharedProfileChanged(Me, EventArgs.Empty)
        OnSharedIntervalChanged(Me, EventArgs.Empty)
    End Sub

    Private Sub UpdateCpuLabels()
        cam1CpuValueLabel.Text = $"{leftRecorderControl.CurrentCpuUsagePercent:0.0}%"
        cam2CpuValueLabel.Text = $"{rightRecorderControl.CurrentCpuUsagePercent:0.0}%"
        cam3CpuValueLabel.Text = $"{thirdRecorderControl.CurrentCpuUsagePercent:0.0}%"
        cam4CpuValueLabel.Text = $"{fourthRecorderControl.CurrentCpuUsagePercent:0.0}%"
    End Sub

    Private Sub UpdateSystemCpuLabel()
        Dim currentSample As SystemCpuSample

        If Not ReadSystemCpuSample(currentSample) Then
            Return
        End If

        If hasSystemCpuSample Then
            Dim totalTicks = (currentSample.KernelTicks - lastSystemCpuSample.KernelTicks) + (currentSample.UserTicks - lastSystemCpuSample.UserTicks)
            Dim idleTicks = currentSample.IdleTicks - lastSystemCpuSample.IdleTicks

            If totalTicks > 0 Then
                Dim busyTicks = Math.Max(0UL, totalTicks - idleTicks)
                Dim totalCpuPercent = (busyTicks * 100.0R / totalTicks)
                totalCpuValueLabel.Text = $"{totalCpuPercent:0.0}%"
                totalCpuValueLabel.ForeColor = GetCpuDisplayColor(totalCpuPercent)
            End If
        End If

        lastSystemCpuSample = currentSample
        hasSystemCpuSample = True
    End Sub

    Private Function FindPcCpuPanel() As FlowLayoutPanel
        Return FindNamedFlowLayoutPanel(commonPanel, "pcCpuPanel")
    End Function

    Private Sub UpdateCommonHeaderHeight()
        If isAdjustingCommonHeaderHeight OrElse commonPanel Is Nothing OrElse commonGroupBox Is Nothing Then
            Return
        End If

        Dim leftSectionPanel = FindNamedFlowLayoutPanel(commonPanel, "commonLeftPanel")
        Dim rightSectionPanel = FindNamedFlowLayoutPanel(commonPanel, "commonRightPanel")
        Dim cameraCpuPanel = FindNamedFlowLayoutPanel(commonPanel, "cameraCpuPanel")

        If leftSectionPanel Is Nothing OrElse rightSectionPanel Is Nothing OrElse cameraCpuPanel Is Nothing OrElse commonPanel.RowStyles.Count < 2 Then
            Return
        End If

        isAdjustingCommonHeaderHeight = True

        Try
            Dim totalContentWidth = Math.Max(0, commonPanel.ClientSize.Width - commonPanel.Padding.Horizontal)
            Dim rightPreferredWidth = rightSectionPanel.GetPreferredSize(Size.Empty).Width + rightSectionPanel.Margin.Horizontal
            Dim leftAvailableWidth = Math.Max(240, totalContentWidth - rightPreferredWidth)

            Dim leftPreferredHeight = leftSectionPanel.GetPreferredSize(New Size(leftAvailableWidth, 0)).Height + leftSectionPanel.Margin.Vertical
            Dim rightPreferredHeight = rightSectionPanel.GetPreferredSize(Size.Empty).Height + rightSectionPanel.Margin.Vertical
            Dim cpuPreferredHeight = cameraCpuPanel.GetPreferredSize(New Size(leftAvailableWidth, 0)).Height + cameraCpuPanel.Margin.Vertical

            Dim row0Height = leftPreferredHeight
            Dim row1Height = cpuPreferredHeight
            Dim combinedLeftHeight = row0Height + row1Height

            If rightPreferredHeight > combinedLeftHeight Then
                row1Height += (rightPreferredHeight - combinedLeftHeight)
            End If

            commonPanel.RowStyles(0).SizeType = SizeType.Absolute
            commonPanel.RowStyles(0).Height = row0Height
            commonPanel.RowStyles(1).SizeType = SizeType.Absolute
            commonPanel.RowStyles(1).Height = row1Height

            Dim desiredPanelHeight = commonPanel.Padding.Vertical + row0Height + row1Height

            desiredPanelHeight = Math.Max(desiredPanelHeight, 96)

            commonPanel.PerformLayout()

            Dim desiredGroupHeight = commonPanel.Top + desiredPanelHeight + commonGroupBox.Padding.Bottom

            If commonPanel.Height <> desiredPanelHeight Then
                commonPanel.Height = desiredPanelHeight
            End If

            If commonGroupBox.Height <> desiredGroupHeight Then
                commonGroupBox.Height = desiredGroupHeight
            End If
        Finally
            isAdjustingCommonHeaderHeight = False
        End Try
    End Sub

    Private Function FindNamedFlowLayoutPanel(parent As Control, panelName As String) As FlowLayoutPanel
        For Each childControl As Control In parent.Controls
            If TypeOf childControl Is FlowLayoutPanel AndAlso String.Equals(childControl.Name, panelName, StringComparison.OrdinalIgnoreCase) Then
                Return DirectCast(childControl, FlowLayoutPanel)
            End If

            If childControl.HasChildren Then
                Dim nestedPanel = FindNamedFlowLayoutPanel(childControl, panelName)

                If nestedPanel IsNot Nothing Then
                    Return nestedPanel
                End If
            End If
        Next

        Return Nothing
    End Function

    Private Function GetCpuDisplayColor(cpuPercent As Double) As Color
        If cpuPercent >= 85.0R Then
            Return Color.FromArgb(220, 53, 69)
        End If

        If cpuPercent >= 60.0R Then
            Return Color.FromArgb(245, 159, 0)
        End If

        Return Color.FromArgb(47, 158, 68)
    End Function

    Private Function ParseCpuText(cpuText As String) As Double
        Dim normalizedText = If(cpuText, String.Empty).Replace("%", String.Empty).Trim()
        Dim cpuPercent As Double

        If Double.TryParse(normalizedText, cpuPercent) Then
            Return cpuPercent
        End If

        Return 0.0R
    End Function

    Private Function ReadSystemCpuSample(ByRef sample As SystemCpuSample) As Boolean
        Dim idleTime As FileTimeValue
        Dim kernelTime As FileTimeValue
        Dim userTime As FileTimeValue

        If Not GetSystemTimes(idleTime, kernelTime, userTime) Then
            Return False
        End If

        sample = New SystemCpuSample With {
            .IdleTicks = ConvertFileTimeToUInt64(idleTime),
            .KernelTicks = ConvertFileTimeToUInt64(kernelTime),
            .UserTicks = ConvertFileTimeToUInt64(userTime)
        }

        Return True
    End Function

    Private Function ConvertFileTimeToUInt64(value As FileTimeValue) As ULong
        Return (CULng(value.DwHighDateTime) << 32) Or value.DwLowDateTime
    End Function

    Private Sub RecorderHostForm_FormClosed(sender As Object, e As FormClosedEventArgs) Handles Me.FormClosed
        systemCpuTimer.Stop()
        recordingDriveFreeSpaceTimer.Stop()
        systemCpuTimer.Dispose()
        recordingDriveFreeSpaceTimer.Dispose()
    End Sub
End Class
