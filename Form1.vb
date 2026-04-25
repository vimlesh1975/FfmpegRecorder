Imports System.ComponentModel
Imports System.Diagnostics
Imports System.IO
Imports System.Net
Imports System.Net.Sockets
Imports System.Text

Partial Public Class RecorderControl
    Private NotInheritable Class OperatorSettings
        Public Property DeviceName As String
        Public Property ProfileName As String
        Public Property IntervalSeconds As Integer
    End Class

    Private NotInheritable Class CpuSample
        Public Property TimestampUtc As DateTime
        Public Property ProcessorTime As TimeSpan
    End Class

    Public NotInheritable Class CpuUsageChangedEventArgs
        Inherits EventArgs

        Public Sub New(cpuUsagePercent As Double)
            Me.CpuUsagePercent = cpuUsagePercent
        End Sub

        Public ReadOnly Property CpuUsagePercent As Double
    End Class

    Private NotInheritable Class RecordingProfileDefinition
        Public Sub New(displayName As String, containerExtension As String, outputOptions As String, Optional videoFilter As String = Nothing)
            Me.DisplayName = displayName
            Me.ContainerExtension = containerExtension
            Me.OutputOptions = outputOptions
            Me.VideoFilter = videoFilter
        End Sub

        Public ReadOnly Property DisplayName As String
        Public ReadOnly Property ContainerExtension As String
        Public ReadOnly Property OutputOptions As String
        Public ReadOnly Property VideoFilter As String

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
    Private Shared ReadOnly deviceReservationSync As New Object()
    Private Shared ReadOnly reservedDevices As New Dictionary(Of String, WeakReference(Of RecorderControl))(StringComparer.OrdinalIgnoreCase)

    Private ReadOnly xdcamHd422Profile As New RecordingProfileDefinition(
        "XDCAM HD422",
        ".mxf",
        "-c:v mpeg2video -pix_fmt yuv422p -b:v 50000k -minrate 50000k -maxrate 50000k -bufsize 17825792 -rc_init_occupancy 17825792 -g 12 -bf 2 -flags +ildct+ilme -top 1 -qmin 1 -qmax 12 -dc 10 -intra_vlc 1 -color_primaries bt709 -color_trc bt709 -colorspace bt709 -c:a pcm_s16le -ar 48000 -ac 2"
    )
    Private ReadOnly mp4HighResProfile As New RecordingProfileDefinition(
        "MP4 High Quality",
        ".mp4",
        "-c:v libx264 -preset medium -crf 18 -pix_fmt yuv420p -profile:v high -movflags +faststart -c:a aac -b:a 192k -ar 48000 -ac 2",
        "bwdif=mode=send_frame:parity=auto:deint=all,scale=1920:1080:flags=lanczos,fps=25"
    )
    Private ReadOnly mp4LowResProfile As New RecordingProfileDefinition(
        "MP4 Low Bitrate",
        ".mp4",
        "-c:v libx264 -preset veryfast -crf 24 -pix_fmt yuv420p -profile:v high -movflags +faststart -c:a aac -b:a 128k -ar 48000 -ac 2",
        "bwdif=mode=send_frame:parity=auto:deint=all,scale=1920:1080:flags=lanczos,fps=25"
    )
    Private ReadOnly proResProxyProfile As New RecordingProfileDefinition(
        "ProRes Proxy (Small)",
        ".mov",
        "-c:v prores_ks -profile:v 0 -pix_fmt yuv422p10le -vendor apl0 -bits_per_mb 400 -c:a pcm_s16le -ar 48000"
    )
    Private ReadOnly proResLtProfile As New RecordingProfileDefinition(
        "ProRes LT (Light)",
        ".mov",
        "-c:v prores_ks -profile:v 1 -pix_fmt yuv422p10le -vendor apl0 -bits_per_mb 1000 -c:a pcm_s16le -ar 48000"
    )
    Private ReadOnly proRes422Profile As New RecordingProfileDefinition(
        "ProRes 422 (Medium)",
        ".mov",
        "-c:v prores_ks -profile:v 2 -pix_fmt yuv422p10le -vendor apl0 -bits_per_mb 1600 -c:a pcm_s16le -ar 48000"
    )
    Private ReadOnly proRes422HqProfile As New RecordingProfileDefinition(
        "ProRes 422 HQ (High)",
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
    Private ReadOnly cpuUsageTimer As New Timer() With {.Interval = 1000}

    Private WithEvents captureRunner As FfmpegProcessRunner
    Private WithEvents previewRunner As PreviewFrameReader
    Private WithEvents recordingPreviewReader As NetworkPreviewReader
    Private WithEvents audioMonitorRunner As FfmpegProcessRunner
    Private audioMonitorPort As Integer
    Private recordingPreviewPort As Integer
    Private suppressDeviceSelectionChanged As Boolean
    Private suppressSettingsSave As Boolean
    Private cameraNameValue As String = "CAM1"
    Private settingsKeyValue As String
    Private savedDeviceName As String = "DeckLink SDI 4K"
    Private reservedDeviceName As String
    Private speakerMonitorEnabledValue As Boolean
    Private currentCpuUsagePercentValue As Double
    Private lastCpuSamples As New Dictionary(Of Integer, CpuSample)()
    Private hasLoadedOnce As Boolean
    Private hasDisposedResources As Boolean

    Public Event CpuUsageChanged As EventHandler(Of CpuUsageChangedEventArgs)

    <Browsable(True), DesignerSerializationVisibility(DesignerSerializationVisibility.Visible), DefaultValue("CAM1")>
    Public Property CameraName As String
        Get
            Return cameraNameValue
        End Get
        Set(value As String)
            cameraNameValue = If(String.IsNullOrWhiteSpace(value), "CAM1", value.Trim())
            UpdateStaticInfo()
        End Set
    End Property

    <Browsable(True), DesignerSerializationVisibility(DesignerSerializationVisibility.Visible), DefaultValue("")>
    Public Property SettingsKey As String
        Get
            Return settingsKeyValue
        End Get
        Set(value As String)
            settingsKeyValue = If(value, String.Empty).Trim()
        End Set
    End Property

    Public ReadOnly Property CurrentCpuUsagePercent As Double
        Get
            Return currentCpuUsagePercentValue
        End Get
    End Property

    <Browsable(False), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)>
    Public ReadOnly Property IsRecording As Boolean
        Get
            Return captureRunner IsNot Nothing
        End Get
    End Property

    <Browsable(False), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)>
    Public ReadOnly Property OutputFolderPath As String
        Get
            Return CreateDefaultOptions().OutputFolder
        End Get
    End Property

    <Browsable(False), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)>
    Public ReadOnly Property AvailableProfileNames As IReadOnlyList(Of String)
        Get
            Return profileComboBox.Items.Cast(Of Object)().
                Select(Function(item) item.ToString()).
                ToArray()
        End Get
    End Property

    <Browsable(False), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)>
    Public Property SelectedProfileName As String
        Get
            Return GetSelectedRecordingProfile().DisplayName
        End Get
        Set(value As String)
            If String.IsNullOrWhiteSpace(value) Then
                Return
            End If

            Dim matchingProfile = profileComboBox.Items.
                Cast(Of Object)().
                Select(Function(item) TryCast(item, RecordingProfileDefinition)).
                FirstOrDefault(Function(profile) profile IsNot Nothing AndAlso String.Equals(profile.DisplayName, value, StringComparison.OrdinalIgnoreCase))

            If matchingProfile Is Nothing Then
                Return
            End If

            profileComboBox.SelectedItem = matchingProfile
        End Set
    End Property

    <Browsable(False), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)>
    Public Property ClipIntervalSeconds As Integer
        Get
            Return GetSelectedClipDurationSeconds()
        End Get
        Set(value As Integer)
            Dim clampedValue = Math.Max(CInt(intervalUpDown.Minimum), Math.Min(CInt(intervalUpDown.Maximum), value))
            intervalUpDown.Value = clampedValue
        End Set
    End Property

    <Browsable(False), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)>
    Public Property SpeakerMonitorEnabled As Boolean
        Get
            Return speakerMonitorEnabledValue
        End Get
        Set(value As Boolean)
            If speakerMonitorEnabledValue = value Then
                Return
            End If

            speakerMonitorEnabledValue = value

            If Not hasLoadedOnce Then
                Return
            End If

            StopAudioMonitorRetry()

            If Not speakerMonitorEnabledValue Then
                TearDownAudioMonitor(fast:=True)
            End If

            If captureRunner Is Nothing Then
                StopIdlePreview("Updating audio listen...", fast:=True)
                StartIdlePreview()
                Return
            End If

            If speakerMonitorEnabledValue Then
                AppendLog("Audio listen selection will apply to the next recording or preview restart.")
            End If
        End Set
    End Property

    Public Sub StartRecordingRequested()
        StartRecording(Me, EventArgs.Empty)
    End Sub

    Public Sub StopRecordingRequested()
        StopRecording(Me, EventArgs.Empty)
    End Sub

    Public Sub New()
        InitializeComponent()
        InitializeOperatorUi()
        InitializeDeckLinkSelector()
        ApplyThemeBackColors()
        UpdateStaticInfo()
        UpdateUiState(False)
        AddHandler recordingPreviewRetryTimer.Tick, AddressOf OnRecordingPreviewRetryTick
        AddHandler audioMonitorRetryTimer.Tick, AddressOf OnAudioMonitorRetryTick
        AddHandler cpuUsageTimer.Tick, AddressOf OnCpuUsageTimerTick
        AddHandler Load, AddressOf RecorderControl_Load
    End Sub

    Protected Overrides Sub OnBackColorChanged(e As EventArgs)
        MyBase.OnBackColorChanged(e)
        ApplyThemeBackColors()
    End Sub

    Private Sub InitializeOperatorUi()
        SuspendLayout()

        AutoScaleMode = AutoScaleMode.Font
        Margin = New Padding(0)
        Size = New Size(470, 392)
        MinimumSize = Size

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

        Dim profileLabel As New Label() With {
            .AutoSize = True,
            .Text = "Profile",
            .Anchor = AnchorStyles.Left,
            .Margin = New Padding(0, 6, 6, 6)
        }

        profileComboBox.DropDownStyle = ComboBoxStyle.DropDownList
        profileComboBox.Width = 136
        profileComboBox.DropDownWidth = 160
        profileComboBox.Anchor = AnchorStyles.Left
        profileComboBox.Margin = New Padding(0, 3, 0, 3)
        profileComboBox.Items.AddRange(New Object() {
            xdcamHd422Profile,
            mp4HighResProfile,
            mp4LowResProfile,
            proResProxyProfile,
            proResLtProfile,
            proRes422Profile,
            proRes422HqProfile
        })
        profileComboBox.SelectedItem = xdcamHd422Profile
        AddHandler profileComboBox.SelectedIndexChanged, AddressOf OnProfileChanged

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
            .ColumnCount = 2,
            .RowCount = 1,
            .Anchor = AnchorStyles.Left,
            .Margin = New Padding(0, 4, 0, 0)
        }
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

        deviceValueLabel.AutoSize = True
        deviceValueLabel.ForeColor = Color.DimGray
        deviceValueLabel.MaximumSize = New Size(420, 0)
        deviceValueLabel.Margin = New Padding(0, 8, 0, 0)

        AddHandler recordButton.Click, AddressOf StartRecording
        AddHandler stopButton.Click, AddressOf StopRecording

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

    Private Sub ApplyThemeBackColors()
        If Controls.Count = 0 Then
            Return
        End If

        ApplyThemeBackColorsRecursive(Me)
    End Sub

    Private Sub ApplyThemeBackColorsRecursive(parentControl As Control)
        For Each childControl As Control In parentControl.Controls
            If TypeOf childControl Is TableLayoutPanel OrElse TypeOf childControl Is Panel OrElse TypeOf childControl Is Label Then
                childControl.BackColor = BackColor
            End If

            If childControl.HasChildren Then
                ApplyThemeBackColorsRecursive(childControl)
            End If
        Next
    End Sub

    Private Sub RecorderControl_Load(sender As Object, e As EventArgs)
        If hasLoadedOnce Then
            Return
        End If

        hasLoadedOnce = True

        If Not File.Exists(GetSettingsFilePath()) Then
            savedDeviceName = GetPreferredDefaultDeviceName()
        End If

        LoadOperatorSettings()
        InitializeDeckLinkSelector()
        LoadDeckLinkDevices()
        EnsureExclusiveDeviceSelection(saveIfSelectionChanged:=True)
        UpdateStaticInfo()
        StartIdlePreview()
        cpuUsageTimer.Start()
    End Sub

    Private Sub UpdateStaticInfo()
        Dim options = CreateDefaultOptions()

        deviceValueLabel.Text = $"{GetRecordingPrefix()} | {options.DeviceName} | 1080i50 | {options.ClipDurationSeconds} s {GetSelectedRecordingProfile().SummaryText} | L/R dBFS"
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
            .FilePrefix = GetRecordingPrefix(),
            .ClipDurationSeconds = GetSelectedClipDurationSeconds(),
            .ContainerExtension = selectedProfile.ContainerExtension,
            .VideoFilter = selectedProfile.VideoFilter,
            .OutputOptions = selectedProfile.OutputOptions
        }
    End Function

    Private Function GetSelectedRecordingProfile() As RecordingProfileDefinition
        Return If(TryCast(profileComboBox.SelectedItem, RecordingProfileDefinition), xdcamHd422Profile)
    End Function

    Private Function GetPreferredDefaultDeviceName() As String
        Select Case GetRecordingPrefix().ToUpperInvariant()
            Case "CAM2"
                Return "DeckLink Duo (1)"
            Case "CAM3"
                Return "DeckLink Duo (2)"
            Case "CAM4"
                Return "DeckLink Duo (3)"
            Case Else
                Return "DeckLink SDI 4K"
        End Select
    End Function

    Private Function GetRecordingPrefix() As String
        Return SanitizeFileToken(CameraName, "CAM1")
    End Function

    Private Function GetSettingsStorageKey() As String
        Return SanitizeFileToken(If(String.IsNullOrWhiteSpace(SettingsKey), CameraName, SettingsKey), "CAM1")
    End Function

    Private Shared Function SanitizeFileToken(value As String, fallbackValue As String) As String
        Dim safeValue = If(String.IsNullOrWhiteSpace(value), fallbackValue, value.Trim())

        For Each invalidCharacter In Path.GetInvalidFileNameChars()
            safeValue = safeValue.Replace(invalidCharacter, "_"c)
        Next

        Return If(String.IsNullOrWhiteSpace(safeValue), fallbackValue, safeValue)
    End Function

    Private Function GetSettingsFilePath() As String
        Dim settingsDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FfmpegRecorder")
        Return Path.Combine(settingsDirectory, $"settings-{GetSettingsStorageKey()}.txt")
    End Function

    Private Sub LoadOperatorSettings()
        Dim settingsFilePath = GetSettingsFilePath()

        If Not File.Exists(settingsFilePath) Then
            Return
        End If

        Dim loadedSettings As New OperatorSettings With {
            .DeviceName = GetPreferredDefaultDeviceName(),
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
            savedDeviceName = If(String.IsNullOrWhiteSpace(settings.DeviceName), GetPreferredDefaultDeviceName(), settings.DeviceName)

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

        Dim preferredDeviceName = GetPreferredDefaultDeviceName()
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

    Private Function EnsureExclusiveDeviceSelection(Optional saveIfSelectionChanged As Boolean = False) As Boolean
        Dim selectedDeviceName = GetSelectedDeviceName()

        If TryReserveSelectedDevice(selectedDeviceName) Then
            savedDeviceName = selectedDeviceName
            Return True
        End If

        Dim replacementDeviceName = FindAvailableDeviceName(selectedDeviceName)

        If String.IsNullOrWhiteSpace(replacementDeviceName) Then
            AppendLog($"No free DeckLink input is available for {GetRecordingPrefix()}.")
            Return False
        End If

        suppressDeviceSelectionChanged = True

        Try
            deviceComboBox.SelectedItem = replacementDeviceName
        Finally
            suppressDeviceSelectionChanged = False
        End Try

        savedDeviceName = replacementDeviceName
        TryReserveSelectedDevice(replacementDeviceName)
        UpdateStaticInfo()

        If saveIfSelectionChanged Then
            SaveOperatorSettings()
        End If

        AppendLog($"{selectedDeviceName} is already assigned to another camera panel. Switched to {replacementDeviceName}.")
        Return True
    End Function

    Private Function FindAvailableDeviceName(conflictingDeviceName As String) As String
        Dim candidateDeviceNames As New List(Of String)()

        AddCandidateDeviceName(candidateDeviceNames, GetPreferredDefaultDeviceName())
        AddCandidateDeviceName(candidateDeviceNames, savedDeviceName)

        For Each item As Object In deviceComboBox.Items
            AddCandidateDeviceName(candidateDeviceNames, TryCast(item, String))
        Next

        For Each candidateDeviceName In candidateDeviceNames
            If String.Equals(candidateDeviceName, conflictingDeviceName, StringComparison.OrdinalIgnoreCase) Then
                Continue For
            End If

            If IsDeviceAvailable(candidateDeviceName) Then
                Return candidateDeviceName
            End If
        Next

        Return Nothing
    End Function

    Private Sub AddCandidateDeviceName(candidateDeviceNames As IList(Of String), candidateDeviceName As String)
        If String.IsNullOrWhiteSpace(candidateDeviceName) Then
            Return
        End If

        For Each existingDeviceName In candidateDeviceNames
            If String.Equals(existingDeviceName, candidateDeviceName, StringComparison.OrdinalIgnoreCase) Then
                Return
            End If
        Next

        candidateDeviceNames.Add(candidateDeviceName)
    End Sub

    Private Function TryReserveSelectedDevice(deviceName As String) As Boolean
        If String.IsNullOrWhiteSpace(deviceName) Then
            Return False
        End If

        SyncLock deviceReservationSync
            CleanupReservedDevices()

            Dim existingReservation As WeakReference(Of RecorderControl) = Nothing

            If reservedDevices.TryGetValue(deviceName, existingReservation) Then
                Dim owner As RecorderControl = Nothing

                If existingReservation.TryGetTarget(owner) AndAlso owner IsNot Nothing AndAlso Not Object.ReferenceEquals(owner, Me) Then
                    Return False
                End If
            End If

            ReleaseReservedDeviceInternal()
            reservedDevices(deviceName) = New WeakReference(Of RecorderControl)(Me)
            reservedDeviceName = deviceName
            Return True
        End SyncLock
    End Function

    Private Function IsDeviceAvailable(deviceName As String) As Boolean
        If String.IsNullOrWhiteSpace(deviceName) Then
            Return False
        End If

        SyncLock deviceReservationSync
            CleanupReservedDevices()

            Dim existingReservation As WeakReference(Of RecorderControl) = Nothing

            If reservedDevices.TryGetValue(deviceName, existingReservation) Then
                Dim owner As RecorderControl = Nothing

                Return Not existingReservation.TryGetTarget(owner) OrElse owner Is Nothing OrElse Object.ReferenceEquals(owner, Me)
            End If

            Return True
        End SyncLock
    End Function

    Private Shared Sub CleanupReservedDevices()
        Dim deviceNamesToRemove As New List(Of String)()

        For Each entry In reservedDevices
            Dim owner As RecorderControl = Nothing

            If Not entry.Value.TryGetTarget(owner) OrElse owner Is Nothing Then
                deviceNamesToRemove.Add(entry.Key)
            End If
        Next

        For Each deviceNameToRemove In deviceNamesToRemove
            reservedDevices.Remove(deviceNameToRemove)
        Next
    End Sub

    Private Sub ReleaseReservedDevice()
        SyncLock deviceReservationSync
            ReleaseReservedDeviceInternal()
        End SyncLock
    End Sub

    Private Sub ReleaseReservedDeviceInternal()
        If String.IsNullOrWhiteSpace(reservedDeviceName) Then
            Return
        End If

        Dim existingReservation As WeakReference(Of RecorderControl) = Nothing

        If reservedDevices.TryGetValue(reservedDeviceName, existingReservation) Then
            Dim owner As RecorderControl = Nothing

            If Not existingReservation.TryGetTarget(owner) OrElse owner Is Nothing OrElse Object.ReferenceEquals(owner, Me) Then
                reservedDevices.Remove(reservedDeviceName)
            End If
        End If

        reservedDeviceName = Nothing
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

        Return If(String.IsNullOrWhiteSpace(savedDeviceName), GetPreferredDefaultDeviceName(), savedDeviceName)
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

        If Not EnsureExclusiveDeviceSelection(saveIfSelectionChanged:=True) Then
            UpdateStaticInfo()
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
        Dim hasAudioMonitor = speakerMonitorEnabledValue AndAlso Not String.IsNullOrWhiteSpace(ResolveFfplayPath(options.FfmpegPath))

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
        Dim hasAudioMonitor = speakerMonitorEnabledValue AndAlso Not String.IsNullOrWhiteSpace(ResolveFfplayPath(options.FfmpegPath))

        If hasAudioMonitor Then
            TearDownAudioMonitor(fast:=True)

            If audioMonitorPort <= 0 Then
                audioMonitorPort = GetAvailableTcpPort()
            End If
        End If

        Directory.CreateDirectory(options.OutputFolder)
        StopIdlePreview("Switching to recording preview...", fast:=True)

        Dim outputPattern = options.BuildOutputPattern()
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

    Private Sub OnCpuUsageTimerTick(sender As Object, e As EventArgs)
        UpdateCpuUsageDisplay()
    End Sub

    Private Sub UpdateCpuUsageDisplay()
        Dim activeProcessIds = GetActiveProcessIds()
        Dim activeSamples As New Dictionary(Of Integer, CpuSample)()
        Dim totalCpuPercent = 0.0

        For Each processId In activeProcessIds
            Try
                Using runningProcess As Process = Process.GetProcessById(processId)
                    If runningProcess.HasExited Then
                        Continue For
                    End If

                    Dim currentSample As New CpuSample With {
                        .TimestampUtc = DateTime.UtcNow,
                        .ProcessorTime = runningProcess.TotalProcessorTime
                    }

                    activeSamples(processId) = currentSample

                    Dim previousSample As CpuSample = Nothing

                    If lastCpuSamples.TryGetValue(processId, previousSample) Then
                        Dim elapsedWallClock = (currentSample.TimestampUtc - previousSample.TimestampUtc).TotalMilliseconds
                        Dim elapsedCpu = (currentSample.ProcessorTime - previousSample.ProcessorTime).TotalMilliseconds

                        If elapsedWallClock > 0 Then
                            totalCpuPercent += Math.Max(0.0, (elapsedCpu / (elapsedWallClock * Environment.ProcessorCount)) * 100.0)
                        End If
                    End If
                End Using
            Catch
            End Try
        Next

        lastCpuSamples = activeSamples
        currentCpuUsagePercentValue = totalCpuPercent
        RaiseEvent CpuUsageChanged(Me, New CpuUsageChangedEventArgs(currentCpuUsagePercentValue))
    End Sub

    Private Function GetActiveProcessIds() As IEnumerable(Of Integer)
        Dim processIds As New HashSet(Of Integer)()

        AddActiveProcessId(processIds, captureRunner?.GetProcessId())
        AddActiveProcessId(processIds, previewRunner?.GetProcessId())
        AddActiveProcessId(processIds, audioMonitorRunner?.GetProcessId())

        Return processIds
    End Function

    Private Sub AddActiveProcessId(processIds As ISet(Of Integer), processId As Integer?)
        If processId.HasValue AndAlso processId.Value > 0 Then
            processIds.Add(processId.Value)
        End If
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
        Return speakerMonitorEnabledValue AndAlso audioMonitorPort > 0 AndAlso (previewRunner IsNot Nothing OrElse captureRunner IsNot Nothing)
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

    Private Sub DisposeRecorderResources()
        If hasDisposedResources Then
            Return
        End If

        hasDisposedResources = True
        cpuUsageTimer.Stop()
        SaveOperatorSettings()
        ReleaseReservedDevice()
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
    End Sub
End Class
