Imports System.ComponentModel
Imports System.Diagnostics
Imports System.IO
Imports System.Net
Imports System.Net.Sockets
Imports System.Text

Partial Public Class Form1
    Private NotInheritable Class OperatorSettings
        Public Property DeviceName As String
        Public Property ProfileName As String
        Public Property IntervalSeconds As Integer
    End Class

    Private NotInheritable Class RecordingProfileDefinition
        Public Sub New(displayName As String, containerExtension As String, outputOptions As String)
            Me.DisplayName = displayName
            Me.ContainerExtension = containerExtension
            Me.OutputOptions = outputOptions
        End Sub

        Public ReadOnly Property DisplayName As String
        Public ReadOnly Property ContainerExtension As String
        Public ReadOnly Property OutputOptions As String

        Public ReadOnly Property SummaryText As String
            Get
                Return $"{DisplayName} {ContainerExtension.TrimStart("."c).ToUpperInvariant()}"
            End Get
        End Property

        Public Overrides Function ToString() As String
            Return DisplayName
        End Function
    End Class

    Private Const PreviewWidth As Integer = 360
    Private Const PreviewHeight As Integer = 202
    Private Const PreviewMeterWidth As Integer = 20
    Private Const PreviewCompositeWidth As Integer = PreviewWidth + (PreviewMeterWidth * 2)
    Private Const PreviewFrameRate As Integer = 10
    Private Const LogHeight As Integer = 56

    Private ReadOnly xdcamHd422Profile As New RecordingProfileDefinition(
        "XDCAM HD422",
        ".mxf",
        "-c:v mpeg2video -pix_fmt yuv422p -b:v 50000k -minrate 50000k -maxrate 50000k -bufsize 17825792 -rc_init_occupancy 17825792 -g 12 -bf 2 -flags +ildct+ilme -top 1 -qmin 1 -qmax 12 -dc 10 -intra_vlc 1 -color_primaries bt709 -color_trc bt709 -colorspace bt709 -c:a pcm_s16le -ar 48000 -ac 2"
    )
    Private ReadOnly proRes422HqProfile As New RecordingProfileDefinition(
        "ProRes 422 HQ",
        ".mov",
        "-c:v prores_ks -profile:v 3 -pix_fmt yuv422p10le -vendor apl0 -bits_per_mb 2400 -c:a pcm_s16le -ar 48000"
    )

    Private ReadOnly previewPictureBox As New PictureBox()
    Private ReadOnly previewStateLabel As New Label()
    Private ReadOnly statusValueLabel As New Label()
    Private ReadOnly deviceComboBox As New ComboBox()
    Private ReadOnly deviceValueLabel As New Label()
    Private ReadOnly intervalUpDown As New NumericUpDown()
    Private ReadOnly profileComboBox As New ComboBox()
    Private ReadOnly recordButton As New Button()
    Private ReadOnly stopButton As New Button()
    Private ReadOnly openOutputFolderButton As New Button()
    Private ReadOnly logTextBox As New TextBox()
    Private ReadOnly recordingPreviewRetryTimer As New Timer() With {.Interval = 250}
    Private ReadOnly audioMonitorRetryTimer As New Timer() With {.Interval = 500}

    Private WithEvents captureRunner As FfmpegProcessRunner
    Private WithEvents previewRunner As PreviewFrameReader
    Private WithEvents recordingPreviewReader As NetworkPreviewReader
    Private WithEvents audioMonitorRunner As FfmpegProcessRunner
    Private audioMonitorPort As Integer
    Private recordingPreviewPort As Integer
    Private suppressDeviceSelectionChanged As Boolean
    Private suppressSettingsSave As Boolean
    Private savedDeviceName As String = "DeckLink SDI 4K"

    Public Sub New()
        InitializeComponent()
        InitializeOperatorUi()
        InitializeDeckLinkSelector()
        LoadOperatorSettings()
        UpdateStaticInfo()
        UpdateUiState(False)
        AddHandler recordingPreviewRetryTimer.Tick, AddressOf OnRecordingPreviewRetryTick
        AddHandler audioMonitorRetryTimer.Tick, AddressOf OnAudioMonitorRetryTick
        AddHandler Shown, AddressOf FormShown
    End Sub

    Private Sub InitializeOperatorUi()
        SuspendLayout()

        Text = "DeckLink Recorder"
        ClientSize = New Size(470, 392)
        FormBorderStyle = FormBorderStyle.FixedSingle
        MaximizeBox = False
        StartPosition = FormStartPosition.CenterScreen

        Dim rootLayout As New TableLayoutPanel() With {
            .Dock = DockStyle.Fill,
            .ColumnCount = 1,
            .Padding = New Padding(8),
            .RowCount = 3
        }
        rootLayout.RowStyles.Add(New RowStyle(SizeType.AutoSize))
        rootLayout.RowStyles.Add(New RowStyle(SizeType.Absolute, PreviewHeight))
        rootLayout.RowStyles.Add(New RowStyle(SizeType.Absolute, LogHeight))

        rootLayout.Controls.Add(BuildActionPanel(), 0, 0)
        rootLayout.Controls.Add(BuildPreviewGroup(), 0, 1)
        rootLayout.Controls.Add(BuildLogGroup(), 0, 2)

        Controls.Add(rootLayout)
        ResumeLayout(True)
    End Sub

    Private Function BuildActionPanel() As Control
        Dim panel As New TableLayoutPanel() With {
            .Dock = DockStyle.Top,
            .ColumnCount = 1,
            .RowCount = 4,
            .AutoSize = True,
            .Margin = New Padding(0, 0, 0, 8)
        }
        panel.RowStyles.Add(New RowStyle(SizeType.AutoSize))
        panel.RowStyles.Add(New RowStyle(SizeType.AutoSize))
        panel.RowStyles.Add(New RowStyle(SizeType.AutoSize))
        panel.RowStyles.Add(New RowStyle(SizeType.AutoSize))
        panel.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100.0F))

        Dim headerRow As New TableLayoutPanel() With {
            .AutoSize = True,
            .ColumnCount = 6,
            .RowCount = 1,
            .Dock = DockStyle.Fill,
            .Margin = New Padding(0)
        }
        headerRow.ColumnStyles.Add(New ColumnStyle(SizeType.AutoSize))
        headerRow.ColumnStyles.Add(New ColumnStyle(SizeType.AutoSize))
        headerRow.ColumnStyles.Add(New ColumnStyle(SizeType.AutoSize))
        headerRow.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100.0F))
        headerRow.ColumnStyles.Add(New ColumnStyle(SizeType.AutoSize))
        headerRow.ColumnStyles.Add(New ColumnStyle(SizeType.AutoSize))
        headerRow.RowStyles.Add(New RowStyle(SizeType.AutoSize))

        Dim statusLabel As New Label() With {
            .AutoSize = True,
            .Text = "Status:",
            .Anchor = AnchorStyles.Left,
            .Margin = New Padding(0, 6, 6, 6)
        }

        statusValueLabel.AutoSize = True
        statusValueLabel.Text = "Idle"
        statusValueLabel.ForeColor = Color.DarkGreen
        statusValueLabel.Anchor = AnchorStyles.Left
        statusValueLabel.Margin = New Padding(0, 6, 12, 6)

        Dim intervalLabel As New Label() With {
            .AutoSize = True,
            .Text = "Interval (s)",
            .Anchor = AnchorStyles.Left,
            .Margin = New Padding(12, 6, 6, 6)
        }

        intervalUpDown.Minimum = 1
        intervalUpDown.Maximum = 3600
        intervalUpDown.Value = 10
        intervalUpDown.Width = 58
        intervalUpDown.Anchor = AnchorStyles.Left
        intervalUpDown.Margin = New Padding(0, 3, 0, 3)
        AddHandler intervalUpDown.ValueChanged, AddressOf OnIntervalValueChanged

        Dim profileLabel As New Label() With {
            .AutoSize = True,
            .Text = "Profile",
            .Anchor = AnchorStyles.Left,
            .Margin = New Padding(0, 6, 6, 6)
        }

        profileComboBox.DropDownStyle = ComboBoxStyle.DropDownList
        profileComboBox.Width = 145
        profileComboBox.Anchor = AnchorStyles.Left
        profileComboBox.Margin = New Padding(0, 3, 0, 3)
        profileComboBox.Items.AddRange(New Object() {xdcamHd422Profile, proRes422HqProfile})
        profileComboBox.SelectedItem = xdcamHd422Profile
        AddHandler profileComboBox.SelectedIndexChanged, AddressOf OnProfileChanged

        Dim deviceRow As New TableLayoutPanel() With {
            .AutoSize = True,
            .ColumnCount = 2,
            .RowCount = 1,
            .Dock = DockStyle.Fill,
            .Margin = New Padding(0, 4, 0, 0)
        }
        deviceRow.ColumnStyles.Add(New ColumnStyle(SizeType.AutoSize))
        deviceRow.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100.0F))
        deviceRow.RowStyles.Add(New RowStyle(SizeType.AutoSize))

        Dim deviceLabel As New Label() With {
            .AutoSize = True,
            .Text = "DeckLink",
            .Anchor = AnchorStyles.Left,
            .Margin = New Padding(0, 6, 6, 6)
        }

        deviceComboBox.DropDownStyle = ComboBoxStyle.DropDownList
        deviceComboBox.Dock = DockStyle.Fill
        deviceComboBox.Margin = New Padding(0, 3, 0, 3)
        AddHandler deviceComboBox.SelectedIndexChanged, AddressOf OnDeviceChanged

        Dim buttonRow As New TableLayoutPanel() With {
            .AutoSize = True,
            .ColumnCount = 3,
            .RowCount = 1,
            .Anchor = AnchorStyles.Left,
            .Margin = New Padding(0, 4, 0, 0)
        }
        buttonRow.ColumnStyles.Add(New ColumnStyle(SizeType.AutoSize))
        buttonRow.ColumnStyles.Add(New ColumnStyle(SizeType.AutoSize))
        buttonRow.ColumnStyles.Add(New ColumnStyle(SizeType.AutoSize))
        buttonRow.RowStyles.Add(New RowStyle(SizeType.AutoSize))

        recordButton.Text = "Record"
        recordButton.AutoSize = False
        recordButton.Size = New Size(76, 28)
        recordButton.Font = New Font(recordButton.Font, FontStyle.Bold)
        recordButton.Margin = New Padding(0, 0, 4, 0)

        stopButton.Text = "Stop"
        stopButton.AutoSize = False
        stopButton.Size = New Size(64, 28)
        stopButton.Margin = New Padding(0, 0, 4, 0)

        openOutputFolderButton.Text = "Open Recordings"
        openOutputFolderButton.AutoSize = False
        openOutputFolderButton.Size = New Size(112, 28)
        openOutputFolderButton.Margin = New Padding(0)

        deviceValueLabel.AutoSize = True
        deviceValueLabel.ForeColor = Color.DimGray
        deviceValueLabel.MaximumSize = New Size(420, 0)
        deviceValueLabel.Margin = New Padding(0, 8, 0, 0)

        AddHandler recordButton.Click, AddressOf StartRecording
        AddHandler stopButton.Click, AddressOf StopRecording
        AddHandler openOutputFolderButton.Click, AddressOf OpenOutputFolder

        headerRow.Controls.Add(statusLabel, 0, 0)
        headerRow.Controls.Add(statusValueLabel, 1, 0)
        headerRow.Controls.Add(profileLabel, 2, 0)
        headerRow.Controls.Add(profileComboBox, 3, 0)
        headerRow.Controls.Add(intervalLabel, 4, 0)
        headerRow.Controls.Add(intervalUpDown, 5, 0)

        deviceRow.Controls.Add(deviceLabel, 0, 0)
        deviceRow.Controls.Add(deviceComboBox, 1, 0)

        buttonRow.Controls.Add(recordButton, 0, 0)
        buttonRow.Controls.Add(stopButton, 1, 0)
        buttonRow.Controls.Add(openOutputFolderButton, 2, 0)

        panel.Controls.Add(headerRow, 0, 0)
        panel.Controls.Add(deviceRow, 0, 1)
        panel.Controls.Add(buttonRow, 0, 2)
        panel.Controls.Add(deviceValueLabel, 0, 3)

        Return panel
    End Function

    Private Function BuildPreviewGroup() As Control
        Dim previewPanel As New Panel() With {
            .Dock = DockStyle.Fill,
            .Padding = New Padding(0),
            .Margin = New Padding(0)
        }

        Dim layout As New TableLayoutPanel() With {
            .Dock = DockStyle.Fill,
            .ColumnCount = 1,
            .RowCount = 1,
            .Padding = New Padding(0),
            .Margin = New Padding(0)
        }
        layout.RowStyles.Add(New RowStyle(SizeType.Percent, 100.0F))

        previewStateLabel.AutoSize = True
        previewStateLabel.Text = "Preview starting..."
        previewStateLabel.ForeColor = Color.DarkOrange
        previewStateLabel.Margin = New Padding(0)
        previewStateLabel.Visible = False

        previewPictureBox.Size = New Size(PreviewCompositeWidth, PreviewHeight)
        previewPictureBox.MinimumSize = previewPictureBox.Size
        previewPictureBox.MaximumSize = previewPictureBox.Size
        previewPictureBox.Anchor = AnchorStyles.None
        previewPictureBox.Margin = New Padding(0)
        previewPictureBox.BackColor = Color.Black
        previewPictureBox.SizeMode = PictureBoxSizeMode.Zoom
        previewPictureBox.BorderStyle = BorderStyle.FixedSingle

        layout.Controls.Add(previewPictureBox, 0, 0)

        previewPanel.Controls.Add(layout)
        Return previewPanel
    End Function

    Private Function BuildLogGroup() As Control
        Dim logPanel As New Panel() With {
            .Dock = DockStyle.Fill,
            .Padding = New Padding(0),
            .Margin = New Padding(0)
        }

        logTextBox.Dock = DockStyle.Fill
        logTextBox.Multiline = True
        logTextBox.ReadOnly = True
        logTextBox.ScrollBars = ScrollBars.Vertical
        logTextBox.Font = New Font("Consolas", 8.5F)
        logTextBox.BorderStyle = BorderStyle.FixedSingle

        logPanel.Controls.Add(logTextBox)
        Return logPanel
    End Function

    Private Sub FormShown(sender As Object, e As EventArgs)
        LoadDeckLinkDevices()
        StartIdlePreview()
    End Sub

    Private Sub UpdateStaticInfo()
        Dim options = CreateDefaultOptions()

        deviceValueLabel.Text = $"{options.DeviceName} | 1080i50 | {options.ClipDurationSeconds} s {GetSelectedRecordingProfile().SummaryText} | L/R dBFS"
    End Sub

    Private Function CreateDefaultOptions() As RecorderOptions
        Dim selectedProfile = GetSelectedRecordingProfile()

        Return New RecorderOptions With {
            .FfmpegPath = ResolveFfmpegPath(),
            .DeviceName = GetSelectedDeviceName(),
            .FormatCode = "Hi50",
            .AudioInput = "embedded",
            .Channels = 2,
            .OutputFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "FFmpegRecorder"),
            .FilePrefix = "clip",
            .ClipDurationSeconds = GetSelectedClipDurationSeconds(),
            .ContainerExtension = selectedProfile.ContainerExtension,
            .OutputOptions = selectedProfile.OutputOptions
        }
    End Function

    Private Function GetSelectedRecordingProfile() As RecordingProfileDefinition
        Return If(TryCast(profileComboBox.SelectedItem, RecordingProfileDefinition), xdcamHd422Profile)
    End Function

    Private Function GetSettingsFilePath() As String
        Dim settingsDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FfmpegRecorder")
        Return Path.Combine(settingsDirectory, "settings.txt")
    End Function

    Private Sub LoadOperatorSettings()
        Dim settingsFilePath = GetSettingsFilePath()

        If Not File.Exists(settingsFilePath) Then
            Return
        End If

        Dim loadedSettings As New OperatorSettings With {
            .DeviceName = "DeckLink SDI 4K",
            .ProfileName = xdcamHd422Profile.DisplayName,
            .IntervalSeconds = 10
        }

        For Each rawLine In File.ReadAllLines(settingsFilePath)
            If String.IsNullOrWhiteSpace(rawLine) Then
                Continue For
            End If

            Dim separatorIndex = rawLine.IndexOf("="c)

            If separatorIndex <= 0 Then
                Continue For
            End If

            Dim key = rawLine.Substring(0, separatorIndex).Trim()
            Dim value = rawLine.Substring(separatorIndex + 1).Trim()

            Select Case key
                Case "device"
                    If Not String.IsNullOrWhiteSpace(value) Then
                        loadedSettings.DeviceName = value
                    End If
                Case "profile"
                    If Not String.IsNullOrWhiteSpace(value) Then
                        loadedSettings.ProfileName = value
                    End If
                Case "interval"
                    Dim parsedInterval As Integer

                    If Integer.TryParse(value, parsedInterval) Then
                        loadedSettings.IntervalSeconds = parsedInterval
                    End If
            End Select
        Next

        ApplyOperatorSettings(loadedSettings)
    End Sub

    Private Sub ApplyOperatorSettings(settings As OperatorSettings)
        suppressSettingsSave = True

        Try
            savedDeviceName = If(String.IsNullOrWhiteSpace(settings.DeviceName), "DeckLink SDI 4K", settings.DeviceName)

            Dim clampedInterval = Math.Max(CInt(intervalUpDown.Minimum), Math.Min(CInt(intervalUpDown.Maximum), settings.IntervalSeconds))
            intervalUpDown.Value = clampedInterval

            Dim selectedProfile As RecordingProfileDefinition = Nothing

            For Each item As Object In profileComboBox.Items
                Dim profile = TryCast(item, RecordingProfileDefinition)

                If profile IsNot Nothing AndAlso String.Equals(profile.DisplayName, settings.ProfileName, StringComparison.OrdinalIgnoreCase) Then
                    selectedProfile = profile
                    Exit For
                End If
            Next

            profileComboBox.SelectedItem = If(selectedProfile, xdcamHd422Profile)
        Finally
            suppressSettingsSave = False
        End Try
    End Sub

    Private Sub SaveOperatorSettings()
        If suppressSettingsSave Then
            Return
        End If

        Dim settingsFilePath = GetSettingsFilePath()
        Dim settingsDirectory = Path.GetDirectoryName(settingsFilePath)

        If Not String.IsNullOrWhiteSpace(settingsDirectory) Then
            Directory.CreateDirectory(settingsDirectory)
        End If

        savedDeviceName = GetSelectedDeviceName()

        Dim lines = {
            $"device={savedDeviceName}",
            $"profile={GetSelectedRecordingProfile().DisplayName}",
            $"interval={GetSelectedClipDurationSeconds()}"
        }

        File.WriteAllLines(settingsFilePath, lines)
    End Sub

    Private Sub InitializeDeckLinkSelector()
        suppressDeviceSelectionChanged = True
        deviceComboBox.Items.Clear()
        deviceComboBox.Items.Add(savedDeviceName)
        deviceComboBox.SelectedIndex = 0
        suppressDeviceSelectionChanged = False
    End Sub

    Private Sub LoadDeckLinkDevices()
        Dim ffmpegPath = ResolveFfmpegPath()
        Dim deviceNames = GetDeckLinkDeviceNames(ffmpegPath)

        If deviceNames.Count = 0 Then
            Return
        End If

        Dim preferredDeviceName = "DeckLink SDI 4K"
        Dim selectedDeviceName = savedDeviceName

        suppressDeviceSelectionChanged = True
        deviceComboBox.BeginUpdate()

        Try
            deviceComboBox.Items.Clear()

            For Each deviceName In deviceNames
                deviceComboBox.Items.Add(deviceName)
            Next

            Dim targetDeviceName = selectedDeviceName

            If String.IsNullOrWhiteSpace(targetDeviceName) OrElse deviceComboBox.Items.IndexOf(targetDeviceName) < 0 Then
                targetDeviceName = If(deviceComboBox.Items.IndexOf(preferredDeviceName) >= 0, preferredDeviceName, deviceComboBox.Items(0).ToString())
            End If

            deviceComboBox.SelectedItem = targetDeviceName
        Finally
            deviceComboBox.EndUpdate()
            suppressDeviceSelectionChanged = False
        End Try

        UpdateStaticInfo()
    End Sub

    Private Function GetDeckLinkDeviceNames(ffmpegPath As String) As List(Of String)
        Dim deviceNames As New List(Of String)()

        If Not File.Exists(ffmpegPath) Then
            Return deviceNames
        End If

        Dim startInfo As New ProcessStartInfo() With {
            .FileName = ffmpegPath,
            .Arguments = "-hide_banner -sources decklink",
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

                Dim alreadyAdded = False

                For Each existingDeviceName In deviceNames
                    If String.Equals(existingDeviceName, deviceName, StringComparison.OrdinalIgnoreCase) Then
                        alreadyAdded = True
                        Exit For
                    End If
                Next

                If Not String.IsNullOrWhiteSpace(deviceName) AndAlso Not alreadyAdded Then
                    deviceNames.Add(deviceName)
                End If
            Next
        End Using

        Return deviceNames
    End Function

    Private Function GetSelectedDeviceName() As String
        Dim selectedDevice = TryCast(deviceComboBox.SelectedItem, String)

        If Not String.IsNullOrWhiteSpace(selectedDevice) Then
            Return selectedDevice
        End If

        Return If(String.IsNullOrWhiteSpace(savedDeviceName), "DeckLink SDI 4K", savedDeviceName)
    End Function

    Private Function GetSelectedClipDurationSeconds() As Integer
        If intervalUpDown Is Nothing Then
            Return 10
        End If

        Return Decimal.ToInt32(intervalUpDown.Value)
    End Function

    Private Sub OnIntervalValueChanged(sender As Object, e As EventArgs)
        UpdateStaticInfo()
        SaveOperatorSettings()
    End Sub

    Private Sub OnProfileChanged(sender As Object, e As EventArgs)
        UpdateStaticInfo()
        SaveOperatorSettings()
    End Sub

    Private Sub OnDeviceChanged(sender As Object, e As EventArgs)
        If suppressDeviceSelectionChanged Then
            Return
        End If

        savedDeviceName = GetSelectedDeviceName()
        UpdateStaticInfo()
        SaveOperatorSettings()

        If captureRunner IsNot Nothing Then
            Return
        End If

        TearDownAudioMonitor(fast:=True)
        StopIdlePreview("Switching device...", fast:=True)
        StartIdlePreview()
    End Sub

    Private Function ResolveFfmpegPath() As String
        Return Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe")
    End Function

    Private Function ResolveFfplayPath(ffmpegPath As String) As String
        If Not String.IsNullOrWhiteSpace(ffmpegPath) AndAlso (ffmpegPath.Contains(Path.DirectorySeparatorChar) OrElse ffmpegPath.Contains(Path.AltDirectorySeparatorChar)) Then
            Dim ffmpegDirectory = Path.GetDirectoryName(ffmpegPath)

            If Not String.IsNullOrWhiteSpace(ffmpegDirectory) Then
                Dim siblingPath = Path.Combine(ffmpegDirectory, "ffplay.exe")

                If File.Exists(siblingPath) Then
                    Return siblingPath
                End If
            End If
        End If

        Return Nothing
    End Function

    Private Sub StartIdlePreview()
        If captureRunner IsNot Nothing OrElse previewRunner IsNot Nothing OrElse recordingPreviewReader IsNot Nothing Then
            Return
        End If

        Dim options = CreateDefaultOptions()
        Dim hasAudioMonitor = Not String.IsNullOrWhiteSpace(ResolveFfplayPath(options.FfmpegPath))

        If hasAudioMonitor AndAlso audioMonitorPort <= 0 Then
            audioMonitorPort = GetAvailableTcpPort()
        End If

        previewStateLabel.Text = "Starting preview..."
        previewStateLabel.ForeColor = Color.DarkOrange

        Try
            previewRunner = New PreviewFrameReader()
            previewRunner.Start(options.FfmpegPath, options.BuildPreviewWithAudioMonitorArguments(If(hasAudioMonitor, audioMonitorPort, 0), PreviewWidth, PreviewFrameRate), AppContext.BaseDirectory)

            If hasAudioMonitor Then
                If Not EnsureAudioMonitor(options) Then
                    ScheduleAudioMonitorRetry()
                End If
            End If

            AppendLog("Idle preview started.")
        Catch ex As Exception
            previewRunner = Nothing
            previewStateLabel.Text = "Preview unavailable"
            previewStateLabel.ForeColor = Color.DarkOrange
            AppendLog($"Preview failed: {GetFriendlyProcessError(ex)}")
        End Try
    End Sub

    Private Sub StopIdlePreview(statusText As String, Optional fast As Boolean = False)
        If previewRunner Is Nothing Then
            previewStateLabel.Text = statusText
            previewStateLabel.ForeColor = Color.DarkOrange
            Return
        End If

        Dim runner = previewRunner
        previewRunner = Nothing

        If fast Then
            runner.Dispose()
        Else
            runner.Stop()
            runner.Dispose()
        End If

        previewStateLabel.Text = statusText
        previewStateLabel.ForeColor = Color.DarkOrange
    End Sub

    Private Function GetAvailableTcpPort() As Integer
        Using listener As New TcpListener(IPAddress.Loopback, 0)
            listener.Start()
            Return DirectCast(listener.LocalEndpoint, IPEndPoint).Port
        End Using
    End Function

    Private Function GetAvailableUdpPort() As Integer
        Using udpClient As New UdpClient(New IPEndPoint(IPAddress.Loopback, 0))
            Return DirectCast(udpClient.Client.LocalEndPoint, IPEndPoint).Port
        End Using
    End Function

    Private Function EnsureAudioMonitor(options As RecorderOptions, Optional logFailure As Boolean = True) As Boolean
        Dim ffplayPath = ResolveFfplayPath(options.FfmpegPath)

        If String.IsNullOrWhiteSpace(ffplayPath) Then
            If logFailure Then
                AppendLog($"ffplay.exe was not found in {AppContext.BaseDirectory}. Live speaker monitoring is unavailable.")
            End If
            Return False
        End If

        If audioMonitorPort <= 0 Then
            audioMonitorPort = GetAvailableTcpPort()
        End If

        If audioMonitorRunner IsNot Nothing Then
            Return True
        End If

        Try
            audioMonitorRunner = New FfmpegProcessRunner()
            audioMonitorRunner.Start(ffplayPath, BuildAudioMonitorArguments(audioMonitorPort, options.Channels), AppContext.BaseDirectory)
            StopAudioMonitorRetry()
            AppendLog("Audio monitor started.")
            Return True
        Catch ex As Exception
            audioMonitorRunner = Nothing
            If logFailure Then
                AppendLog($"Audio monitor failed: {GetFriendlyProcessError(ex)}")
            End If
            Return False
        End Try
    End Function

    Private Function BuildAudioMonitorArguments(audioMonitorPort As Integer, channels As Integer) As String
        Dim channelLayout = GetAudioChannelLayout(channels)
        Return $"-hide_banner -loglevel warning -nostats -nodisp -fflags nobuffer -flags low_delay -probesize 32 -analyzeduration 0 -sync ext -volume 100 -f s16le -sample_rate 48000 -ch_layout {channelLayout} -i {Quote($"tcp://127.0.0.1:{audioMonitorPort}")}"
    End Function

    Private Function GetAudioChannelLayout(channels As Integer) As String
        If channels <= 1 Then
            Return "mono"
        End If

        Return "stereo"
    End Function

    Private Shared Function Quote(value As String) As String
        Dim safeValue = If(value, String.Empty).Replace("""", String.Empty)
        Return $"""{safeValue}"""
    End Function

    Private Sub StartRecording(sender As Object, e As EventArgs)
        If captureRunner IsNot Nothing Then
            Return
        End If

        Dim options = CreateDefaultOptions()
        Dim previewPort = GetAvailableTcpPort()
        Dim hasAudioMonitor = Not String.IsNullOrWhiteSpace(ResolveFfplayPath(options.FfmpegPath))

        If hasAudioMonitor Then
            TearDownAudioMonitor(fast:=True)

            If audioMonitorPort <= 0 Then
                audioMonitorPort = GetAvailableTcpPort()
            End If
        End If

        Directory.CreateDirectory(options.OutputFolder)
        StopIdlePreview("Switching to recording preview...", fast:=True)

        Dim sessionStamp = DateTime.Now.ToString("yyyyMMdd_HHmmss")
        Dim outputPattern = options.BuildOutputPattern(sessionStamp)
        Dim arguments = options.BuildRecordingWithPreviewArguments(outputPattern, previewPort, If(hasAudioMonitor, audioMonitorPort, 0), PreviewWidth, PreviewFrameRate)

        logTextBox.Clear()
        AppendLog($"Clip pattern: {outputPattern}")

        Try
            captureRunner = New FfmpegProcessRunner()
            captureRunner.Start(options.FfmpegPath, arguments, options.OutputFolder)

            If hasAudioMonitor Then
                If Not EnsureAudioMonitor(options) Then
                    ScheduleAudioMonitorRetry()
                End If
            End If

            Dim previewConnected = TryStartRecordingPreview(previewPort, 250)

            If Not previewConnected Then
                ScheduleRecordingPreviewRetry(previewPort)
            End If

            UpdateUiState(True)
            statusValueLabel.Text = "Recording"
            statusValueLabel.ForeColor = Color.Firebrick
            previewStateLabel.Text = If(previewConnected, If(audioMonitorRunner IsNot Nothing, "Live preview and speaker monitoring active while recording.", "Live preview active while recording."), "Recording started. Preview is reconnecting...")
            previewStateLabel.ForeColor = Color.DarkGreen
        Catch ex As Exception
            AppendLog($"Failed to start recording: {GetFriendlyProcessError(ex)}")
            TearDownRecordingSession()
            UpdateUiState(False)
            statusValueLabel.Text = "Idle"
            statusValueLabel.ForeColor = Color.DarkGreen
            MessageBox.Show(Me, GetFriendlyProcessError(ex), "Unable To Start Recording", MessageBoxButtons.OK, MessageBoxIcon.Error)
            StartIdlePreview()
        End Try
    End Sub

    Private Sub StopRecording(sender As Object, e As EventArgs)
        If captureRunner Is Nothing Then
            Return
        End If

        stopButton.Enabled = False
        AppendLog("Stopping recording...")
        captureRunner.Stop()
    End Sub

    Private Sub OpenOutputFolder(sender As Object, e As EventArgs)
        Dim folderPath = CreateDefaultOptions().OutputFolder
        Directory.CreateDirectory(folderPath)
        Process.Start(New ProcessStartInfo(folderPath) With {.UseShellExecute = True})
    End Sub

    Private Sub UpdateUiState(isRecording As Boolean)
        recordButton.Enabled = Not isRecording
        stopButton.Enabled = isRecording
        intervalUpDown.Enabled = Not isRecording
        profileComboBox.Enabled = Not isRecording
        deviceComboBox.Enabled = Not isRecording
    End Sub

    Private Sub AppendLog(message As String)
        If InvokeRequired Then
            BeginInvoke(New Action(Of String)(AddressOf AppendLog), message)
            Return
        End If

        logTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}")
    End Sub

    Private Function GetFriendlyProcessError(ex As Exception) As String
        If TypeOf ex Is Win32Exception AndAlso ex.Message.Contains("cannot find the file", StringComparison.OrdinalIgnoreCase) Then
            Return $"ffmpeg.exe or ffplay.exe was not found in {AppContext.BaseDirectory}. Copy the local binaries there."
        End If

        Return ex.Message
    End Function

    Private Sub TearDownRecordingPreview()
        If recordingPreviewReader Is Nothing Then
            Return
        End If

        Dim reader = recordingPreviewReader
        recordingPreviewReader = Nothing
        reader.Stop()
        reader.Dispose()
    End Sub

    Private Function TryStartRecordingPreview(port As Integer, Optional connectTimeoutMs As Integer = 250) As Boolean
        If recordingPreviewReader IsNot Nothing Then
            Return True
        End If

        Try
            recordingPreviewReader = New NetworkPreviewReader()
            recordingPreviewReader.Start("127.0.0.1", port, connectTimeoutMs)
            recordingPreviewPort = 0
            recordingPreviewRetryTimer.Stop()
            Return True
        Catch ex As Exception
            If recordingPreviewReader IsNot Nothing Then
                recordingPreviewReader.Dispose()
                recordingPreviewReader = Nothing
            End If

            AppendLog($"Recording preview connection delayed: {ex.Message}")
            Return False
        End Try
    End Function

    Private Sub ScheduleRecordingPreviewRetry(port As Integer)
        recordingPreviewPort = port

        If Not recordingPreviewRetryTimer.Enabled Then
            recordingPreviewRetryTimer.Start()
        End If
    End Sub

    Private Sub StopRecordingPreviewRetry()
        recordingPreviewPort = 0
        recordingPreviewRetryTimer.Stop()
    End Sub

    Private Sub OnRecordingPreviewRetryTick(sender As Object, e As EventArgs)
        If captureRunner Is Nothing Then
            StopRecordingPreviewRetry()
            Return
        End If

        If recordingPreviewReader IsNot Nothing OrElse recordingPreviewPort <= 0 Then
            StopRecordingPreviewRetry()
            Return
        End If

        If TryStartRecordingPreview(recordingPreviewPort, 250) Then
            AppendLog("Recording preview connected after retry.")
            previewStateLabel.Text = If(audioMonitorRunner IsNot Nothing, "Live preview and speaker monitoring active while recording.", "Live preview active while recording.")
            previewStateLabel.ForeColor = Color.DarkGreen
        End If
    End Sub

    Private Function ShouldRetryAudioMonitor() As Boolean
        Return audioMonitorPort > 0 AndAlso (previewRunner IsNot Nothing OrElse captureRunner IsNot Nothing)
    End Function

    Private Sub ScheduleAudioMonitorRetry()
        If Not ShouldRetryAudioMonitor() Then
            StopAudioMonitorRetry()
            Return
        End If

        If Not audioMonitorRetryTimer.Enabled Then
            audioMonitorRetryTimer.Start()
        End If
    End Sub

    Private Sub StopAudioMonitorRetry()
        audioMonitorRetryTimer.Stop()
    End Sub

    Private Sub OnAudioMonitorRetryTick(sender As Object, e As EventArgs)
        If audioMonitorRunner IsNot Nothing Then
            StopAudioMonitorRetry()
            Return
        End If

        If Not ShouldRetryAudioMonitor() Then
            StopAudioMonitorRetry()
            Return
        End If

        If EnsureAudioMonitor(CreateDefaultOptions(), logFailure:=False) Then
            AppendLog("Audio monitor reconnected.")
            previewStateLabel.Text = GetActivePreviewStateText()
            previewStateLabel.ForeColor = Color.DarkGreen
        End If
    End Sub

    Private Sub TearDownAudioMonitor(Optional fast As Boolean = False)
        StopAudioMonitorRetry()

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

    Private Sub TearDownCaptureRunner()
        If captureRunner Is Nothing Then
            Return
        End If

        Dim runner = captureRunner
        captureRunner = Nothing
        runner.Dispose()
    End Sub

    Private Sub TearDownRecordingSession()
        StopRecordingPreviewRetry()
        TearDownRecordingPreview()
        TearDownCaptureRunner()
    End Sub

    Private Sub previewRunner_FrameReady(frame As Bitmap) Handles previewRunner.FrameReady
        ShowPreviewFrame(frame, GetActivePreviewStateText(), Color.DarkGreen)
    End Sub

    Private Sub recordingPreviewReader_FrameReady(frame As Bitmap) Handles recordingPreviewReader.FrameReady
        ShowPreviewFrame(frame, GetActivePreviewStateText(), Color.DarkGreen)
    End Sub

    Private Function GetActivePreviewStateText() As String
        If captureRunner IsNot Nothing Then
            Return If(audioMonitorRunner IsNot Nothing, "Live preview and speaker monitoring active while recording.", "Live preview active while recording. Audio monitor reconnecting...")
        End If

        Return If(audioMonitorRunner IsNot Nothing, "Live preview and speaker monitoring active.", "Live preview active. Audio monitor reconnecting...")
    End Function

    Private Sub ShowPreviewFrame(frame As Bitmap, stateText As String, stateColor As Color)
        If InvokeRequired Then
            BeginInvoke(New Action(Of Bitmap, String, Color)(AddressOf ShowPreviewFrame), frame, stateText, stateColor)
            Return
        End If

        Dim previousImage = previewPictureBox.Image
        previewPictureBox.Image = frame
        previewStateLabel.Text = stateText
        previewStateLabel.ForeColor = stateColor

        If previousImage IsNot Nothing Then
            previousImage.Dispose()
        End If
    End Sub

    Private Sub previewRunner_LogReceived(message As String) Handles previewRunner.LogReceived
        AppendLog(message)
    End Sub

    Private Sub recordingPreviewReader_LogReceived(message As String) Handles recordingPreviewReader.LogReceived
        AppendLog(message)
    End Sub

    Private Sub previewRunner_Exited(exitCode As Integer) Handles previewRunner.Exited
        If InvokeRequired Then
            BeginInvoke(New Action(Of Integer)(AddressOf previewRunner_Exited), exitCode)
            Return
        End If

        If previewRunner IsNot Nothing Then
            previewRunner.Dispose()
            previewRunner = Nothing
        End If

        If captureRunner Is Nothing AndAlso recordingPreviewReader Is Nothing Then
            previewStateLabel.Text = If(exitCode = 0, "Preview stopped.", $"Preview stopped (Exit {exitCode}).")
            previewStateLabel.ForeColor = Color.DarkOrange
            AppendLog($"Preview exited with code {exitCode}.")
        End If
    End Sub

    Private Sub recordingPreviewReader_Exited() Handles recordingPreviewReader.Exited
        If InvokeRequired Then
            BeginInvoke(New Action(AddressOf recordingPreviewReader_Exited))
            Return
        End If

        If captureRunner IsNot Nothing Then
            previewStateLabel.Text = "Recording is running. Preview is reconnecting..."
            previewStateLabel.ForeColor = Color.DarkOrange
            AppendLog("Recording preview disconnected.")
            If recordingPreviewPort > 0 Then
                ScheduleRecordingPreviewRetry(recordingPreviewPort)
            End If
        End If

        If recordingPreviewReader IsNot Nothing Then
            recordingPreviewReader.Dispose()
            recordingPreviewReader = Nothing
        End If
    End Sub

    Private Sub captureRunner_LogReceived(message As String) Handles captureRunner.LogReceived
        AppendLog(message)
    End Sub

    Private Sub audioMonitorRunner_LogReceived(message As String) Handles audioMonitorRunner.LogReceived
        AppendLog($"Audio monitor: {message}")
    End Sub

    Private Sub audioMonitorRunner_Exited(exitCode As Integer) Handles audioMonitorRunner.Exited
        If InvokeRequired Then
            BeginInvoke(New Action(Of Integer)(AddressOf audioMonitorRunner_Exited), exitCode)
            Return
        End If

        If audioMonitorRunner IsNot Nothing Then
            audioMonitorRunner.Dispose()
            audioMonitorRunner = Nothing
        End If

        previewStateLabel.Text = If(captureRunner IsNot Nothing, "Live preview active while recording. Audio monitor reconnecting...", "Live preview active. Audio monitor reconnecting...")
        previewStateLabel.ForeColor = Color.DarkOrange
        AppendLog($"Audio monitor exited with code {exitCode}.")

        ScheduleAudioMonitorRetry()
    End Sub

    Private Sub captureRunner_Exited(exitCode As Integer) Handles captureRunner.Exited
        If InvokeRequired Then
            BeginInvoke(New Action(Of Integer)(AddressOf captureRunner_Exited), exitCode)
            Return
        End If

        StopRecordingPreviewRetry()
        TearDownRecordingPreview()
        TearDownAudioMonitor()

        statusValueLabel.Text = If(exitCode = 0, "Idle", $"Stopped (Exit {exitCode})")
        statusValueLabel.ForeColor = If(exitCode = 0, Color.DarkGreen, Color.DarkOrange)
        AppendLog($"Recording exited with code {exitCode}.")

        TearDownCaptureRunner()
        UpdateUiState(False)
        StartIdlePreview()
    End Sub

    Protected Overrides Sub OnFormClosing(e As FormClosingEventArgs)
        SaveOperatorSettings()
        StopRecordingPreviewRetry()
        TearDownRecordingPreview()
        TearDownAudioMonitor()

        If previewRunner IsNot Nothing Then
            Dim runner = previewRunner
            previewRunner = Nothing
            runner.Stop()
            runner.Dispose()
        End If

        If captureRunner IsNot Nothing Then
            captureRunner.Stop()
            TearDownCaptureRunner()
        End If

        If previewPictureBox.Image IsNot Nothing Then
            previewPictureBox.Image.Dispose()
            previewPictureBox.Image = Nothing
        End If

        MyBase.OnFormClosing(e)
    End Sub
End Class
