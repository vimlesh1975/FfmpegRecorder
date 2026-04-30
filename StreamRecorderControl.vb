Imports System.ComponentModel
Imports System.Diagnostics
Imports System.Globalization
Imports System.IO
Imports System.Text
Imports System.Threading.Tasks

Public Class StreamRecorderControl
    Inherits UserControl

    Private Const PreviewAudioDelayMilliseconds As Integer = 700

    Private NotInheritable Class StreamRecordingProfile
        Public Sub New(displayName As String, containerExtension As String, outputOptions As String, Optional useFfmbcFinalize As Boolean = False)
            Me.DisplayName = displayName
            Me.ContainerExtension = containerExtension
            Me.OutputOptions = outputOptions
            Me.UseFfmbcFinalize = useFfmbcFinalize
        End Sub

        Public ReadOnly Property DisplayName As String
        Public ReadOnly Property ContainerExtension As String
        Public ReadOnly Property OutputOptions As String
        Public ReadOnly Property UseFfmbcFinalize As Boolean

        Public Overrides Function ToString() As String
            Return DisplayName
        End Function
    End Class

    Private NotInheritable Class StreamAudioSourceInfo
        Public Sub New(inputIndex As Integer, streamIndex As Integer, channels As Integer)
            Me.InputIndex = inputIndex
            Me.StreamIndex = streamIndex
            Me.Channels = channels
        End Sub

        Public ReadOnly Property InputIndex As Integer
        Public ReadOnly Property StreamIndex As Integer
        Public ReadOnly Property Channels As Integer
    End Class

    Private NotInheritable Class PendingFfmbcFinalizeSession
        Public Sub New(tempOutputFolder As String, finalOutputFolder As String)
            Me.TempOutputFolder = tempOutputFolder
            Me.FinalOutputFolder = finalOutputFolder
        End Sub

        Public ReadOnly Property TempOutputFolder As String
        Public ReadOnly Property FinalOutputFolder As String
    End Class

    Private ReadOnly statusStrip As New Panel()
    Private ReadOnly statusValueLabel As New Label()
    Private ReadOnly urlTextBox As New TextBox()
    Private ReadOnly profileComboBox As New ComboBox()
    Private ReadOnly intervalUpDown As New NumericUpDown()
    Private ReadOnly previewButton As New Button()
    Private ReadOnly stopPreviewButton As New Button()
    Private ReadOnly recordButton As New Button()
    Private ReadOnly stopButton As New Button()
    Private ReadOnly elapsedLabel As New Label()
    Private ReadOnly previewPictureBox As New PictureBox()
    Private ReadOnly previewStateLabel As New Label()
    Private ReadOnly logTextBox As New TextBox()
    Private ReadOnly elapsedTimer As New Timer() With {.Interval = 1000}
    Private ReadOnly ffmbcFinalizeTimer As New Timer() With {.Interval = 1000}

    Private WithEvents streamRunner As FfmpegProcessRunner
    Private WithEvents previewRunner As PreviewFrameReader
    Private WithEvents previewAudioRunner As FfmpegProcessRunner
    Private recordingStartedAtUtc As DateTime?
    Private darkModeEnabledValue As Boolean
    Private suppressSettingsSave As Boolean
    Private isFinalizingRecordingValue As Boolean
    Private isStartingRecordingValue As Boolean
    Private isStoppingRecordingValue As Boolean
    Private isStartingPreviewValue As Boolean
    Private isStoppingPreviewValue As Boolean
    Private currentRecordingUsesDirectFfmbcValue As Boolean
    Private currentRecordingUsesFfmbcFallbackValue As Boolean
    Private continueDirectFfmbcRecordingValue As Boolean
    Private currentDirectFfmbcInputUrls As IReadOnlyList(Of String)
    Private currentDirectFfmbcProfile As StreamRecordingProfile
    Private currentDirectFfmbcAudioSource As StreamAudioSourceInfo
    Private currentDirectFfmbcSilenceFilePath As String
    Private currentRecordingTempOutputFolder As String
    Private currentRecordingFinalOutputFolder As String
    Private ReadOnly ffmbcFinalizeSync As New Object()
    Private ReadOnly ffmbcProcessedTempFiles As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
    Private ReadOnly ffmbcProcessingTempFiles As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)
    Private ReadOnly pendingFfmbcFinalizeSessions As New List(Of PendingFfmbcFinalizeSession)()
    Private ffmbcBackgroundFinalizeTask As Task(Of String)

    Public Sub New()
        InitializeOperatorUi()
        LoadStreamSettings()
        ApplyThemeColors()
        UpdateUiState(False)

        AddHandler recordButton.Click, AddressOf StartRecording
        AddHandler stopButton.Click, AddressOf StopRecording
        AddHandler previewButton.Click, AddressOf StartPreview
        AddHandler stopPreviewButton.Click, AddressOf StopPreview
        AddHandler elapsedTimer.Tick, AddressOf OnElapsedTimerTick
        AddHandler ffmbcFinalizeTimer.Tick, AddressOf OnFfmbcFinalizeTimerTick
        AddHandler urlTextBox.TextChanged, AddressOf OnSettingsChanged
        AddHandler profileComboBox.SelectedIndexChanged, AddressOf OnSettingsChanged
        AddHandler intervalUpDown.ValueChanged, AddressOf OnSettingsChanged
    End Sub

    <Browsable(False), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)>
    Public Property DarkModeEnabled As Boolean
        Get
            Return darkModeEnabledValue
        End Get
        Set(value As Boolean)
            If darkModeEnabledValue = value Then
                Return
            End If

            darkModeEnabledValue = value
            ApplyThemeColors()
        End Set
    End Property

    Public ReadOnly Property IsRecording As Boolean
        Get
            Return streamRunner IsNot Nothing
        End Get
    End Property

    Private ReadOnly Property OutputFolderPath As String
        Get
            Return RecordingDirectorySettings.GetRecordingDirectory()
        End Get
    End Property

    Private ReadOnly Property SettingsFilePath As String
        Get
            Return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FfmpegRecorder", "StreamRecorder.settings")
        End Get
    End Property

    Private Sub InitializeOperatorUi()
        SuspendLayout()

        AutoScaleMode = AutoScaleMode.Font
        Margin = New Padding(0)
        MinimumSize = New Size(320, 0)

        Dim rootLayout As New TableLayoutPanel() With {
            .Dock = DockStyle.Fill,
            .ColumnCount = 2,
            .RowCount = 5,
            .Padding = New Padding(8)
        }
        rootLayout.ColumnStyles.Add(New ColumnStyle(SizeType.Absolute, 6))
        rootLayout.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100.0F))
        rootLayout.RowStyles.Add(New RowStyle(SizeType.AutoSize))
        rootLayout.RowStyles.Add(New RowStyle(SizeType.AutoSize))
        rootLayout.RowStyles.Add(New RowStyle(SizeType.AutoSize))
        rootLayout.RowStyles.Add(New RowStyle(SizeType.Absolute, 190))
        rootLayout.RowStyles.Add(New RowStyle(SizeType.Percent, 100.0F))

        statusStrip.Dock = DockStyle.Fill
        statusStrip.Margin = New Padding(0, 0, 8, 0)
        rootLayout.Controls.Add(statusStrip, 0, 0)
        rootLayout.SetRowSpan(statusStrip, 5)

        Dim headerRow As New TableLayoutPanel() With {
            .AutoSize = True,
            .ColumnCount = 2,
            .Dock = DockStyle.Fill,
            .Margin = New Padding(0, 0, 0, 8),
            .RowCount = 1
        }
        headerRow.ColumnStyles.Add(New ColumnStyle(SizeType.AutoSize))
        headerRow.ColumnStyles.Add(New ColumnStyle(SizeType.Percent, 100.0F))

        Dim statusLabel As New Label() With {
            .AutoSize = True,
            .Text = "Status:",
            .Margin = New Padding(0, 6, 6, 0)
        }

        statusValueLabel.AutoSize = True
        statusValueLabel.Text = "Idle"
        statusValueLabel.Margin = New Padding(0, 6, 0, 0)

        headerRow.Controls.Add(statusLabel, 0, 0)
        headerRow.Controls.Add(statusValueLabel, 1, 0)

        Dim sourcePanel As New TableLayoutPanel() With {
            .AutoSize = True,
            .ColumnCount = 1,
            .Dock = DockStyle.Fill,
            .Margin = New Padding(0, 0, 0, 8),
            .RowCount = 4
        }
        sourcePanel.RowStyles.Add(New RowStyle(SizeType.AutoSize))
        sourcePanel.RowStyles.Add(New RowStyle(SizeType.AutoSize))
        sourcePanel.RowStyles.Add(New RowStyle(SizeType.AutoSize))
        sourcePanel.RowStyles.Add(New RowStyle(SizeType.AutoSize))

        Dim sourceLabel As New Label() With {
            .AutoSize = True,
            .Text = "URL / File",
            .Margin = New Padding(0, 0, 0, 4)
        }

        urlTextBox.Dock = DockStyle.Top
        urlTextBox.Margin = New Padding(0)
        urlTextBox.Text = "https://youtu.be/tRCU_5Ngws8"

        Dim profileLabel As New Label() With {
            .AutoSize = True,
            .Text = "Profile",
            .Margin = New Padding(0, 8, 0, 4)
        }

        profileComboBox.DropDownStyle = ComboBoxStyle.DropDownList
        profileComboBox.Dock = DockStyle.Top
        profileComboBox.MinimumSize = New Size(260, 0)
        profileComboBox.DropDownWidth = 280
        profileComboBox.Margin = New Padding(0)
        profileComboBox.Items.AddRange(New Object() {
            New StreamRecordingProfile("XDCAM HD422", ".mxf", "-c:v mpeg2video -pix_fmt yuv422p -b:v 50000k -minrate 50000k -maxrate 50000k -bufsize 17825792 -rc_init_occupancy 17825792 -g 12 -bf 2 -flags +ildct+ilme -top 1 -qmin 1 -qmax 12 -dc 10 -intra_vlc 1 -color_primaries bt709 -color_trc bt709 -colorspace bt709 -c:a pcm_s16le -ar 48000 -ac 2"),
            New StreamRecordingProfile("XDCAM Sony Compatible", ".mxf", "-c:v mpeg2video -pix_fmt yuv422p -b:v 50000k -minrate 50000k -maxrate 50000k -bufsize 17825792 -rc_init_occupancy 17825792 -g 12 -bf 2 -flags +ildct+ilme -top 1 -qmin 1 -qmax 12 -dc 10 -intra_vlc 1 -color_primaries bt709 -color_trc bt709 -colorspace bt709 -c:a pcm_s24le -ar 48000", useFfmbcFinalize:=True),
            New StreamRecordingProfile("MP4 High Quality", ".mp4", "-c:v libx264 -preset medium -crf 18 -pix_fmt yuv420p -profile:v high -movflags +faststart -c:a aac -b:a 192k -ar 48000 -ac 2"),
            New StreamRecordingProfile("MP4 Low Bitrate", ".mp4", "-c:v libx264 -preset veryfast -crf 24 -pix_fmt yuv420p -profile:v high -movflags +faststart -c:a aac -b:a 128k -ar 48000 -ac 2"),
            New StreamRecordingProfile("ProRes Proxy (Small)", ".mov", "-c:v prores_ks -profile:v 0 -pix_fmt yuv422p10le -vendor apl0 -bits_per_mb 400 -c:a pcm_s16le -ar 48000"),
            New StreamRecordingProfile("ProRes LT (Light)", ".mov", "-c:v prores_ks -profile:v 1 -pix_fmt yuv422p10le -vendor apl0 -bits_per_mb 1000 -c:a pcm_s16le -ar 48000"),
            New StreamRecordingProfile("ProRes 422 (Medium)", ".mov", "-c:v prores_ks -profile:v 2 -pix_fmt yuv422p10le -vendor apl0 -bits_per_mb 1600 -c:a pcm_s16le -ar 48000"),
            New StreamRecordingProfile("ProRes 422 HQ (High)", ".mov", "-c:v prores_ks -profile:v 3 -pix_fmt yuv422p10le -vendor apl0 -bits_per_mb 2400 -c:a pcm_s16le -ar 48000")
        })
        profileComboBox.SelectedIndex = 1

        sourcePanel.Controls.Add(sourceLabel, 0, 0)
        sourcePanel.Controls.Add(urlTextBox, 0, 1)
        sourcePanel.Controls.Add(profileLabel, 0, 2)
        sourcePanel.Controls.Add(profileComboBox, 0, 3)

        Dim actionRow As New FlowLayoutPanel() With {
            .AutoSize = True,
            .Dock = DockStyle.Fill,
            .FlowDirection = FlowDirection.LeftToRight,
            .Margin = New Padding(0, 0, 0, 8),
            .WrapContents = True
        }

        Dim intervalLabel As New Label() With {
            .AutoSize = True,
            .Text = "Interval (s)",
            .Margin = New Padding(0, 6, 6, 0)
        }

        intervalUpDown.Minimum = 1
        intervalUpDown.Maximum = 3600
        intervalUpDown.Value = 10
        intervalUpDown.Width = 64
        intervalUpDown.Margin = New Padding(0, 3, 12, 0)

        previewButton.Text = "Preview"
        previewButton.Size = New Size(72, 28)
        previewButton.Margin = New Padding(0, 0, 6, 0)

        stopPreviewButton.Text = "Stop Preview"
        stopPreviewButton.Size = New Size(96, 28)
        stopPreviewButton.Margin = New Padding(0, 0, 6, 0)

        recordButton.Text = "Record"
        recordButton.Font = New Font(recordButton.Font, FontStyle.Bold)
        recordButton.Size = New Size(76, 28)
        recordButton.Margin = New Padding(0, 0, 6, 0)

        stopButton.Text = "Stop"
        stopButton.Size = New Size(64, 28)
        stopButton.Margin = New Padding(0, 0, 6, 0)

        elapsedLabel.AutoSize = True
        elapsedLabel.Font = New Font("Segoe UI", 9.0F, FontStyle.Bold)
        elapsedLabel.Text = "REC 00:00:00"
        elapsedLabel.Visible = False
        elapsedLabel.Margin = New Padding(0, 6, 0, 0)

        actionRow.Controls.Add(intervalLabel)
        actionRow.Controls.Add(intervalUpDown)
        actionRow.Controls.Add(previewButton)
        actionRow.Controls.Add(stopPreviewButton)
        actionRow.Controls.Add(recordButton)
        actionRow.Controls.Add(stopButton)
        actionRow.Controls.Add(elapsedLabel)

        Dim previewPanel As New Panel() With {
            .Dock = DockStyle.Fill,
            .Margin = New Padding(0, 0, 0, 8)
        }

        previewPictureBox.Dock = DockStyle.Fill
        previewPictureBox.BackColor = Color.Black
        previewPictureBox.BorderStyle = BorderStyle.FixedSingle
        previewPictureBox.SizeMode = PictureBoxSizeMode.Zoom

        previewStateLabel.AutoSize = True
        previewStateLabel.Text = "Preview stopped"
        previewStateLabel.Visible = False

        previewPanel.Controls.Add(previewPictureBox)
        previewPanel.Controls.Add(previewStateLabel)

        logTextBox.Dock = DockStyle.Fill
        logTextBox.Multiline = True
        logTextBox.ReadOnly = True
        logTextBox.ScrollBars = ScrollBars.Vertical
        logTextBox.Font = New Font("Consolas", 8.5F)
        logTextBox.BorderStyle = BorderStyle.FixedSingle
        logTextBox.Margin = New Padding(0)

        rootLayout.Controls.Add(headerRow, 1, 0)
        rootLayout.Controls.Add(sourcePanel, 1, 1)
        rootLayout.Controls.Add(actionRow, 1, 2)
        rootLayout.Controls.Add(previewPanel, 1, 3)
        rootLayout.Controls.Add(logTextBox, 1, 4)

        Controls.Add(rootLayout)
        ResumeLayout(True)
    End Sub

    Protected Overrides Sub OnBackColorChanged(e As EventArgs)
        MyBase.OnBackColorChanged(e)
        ApplyThemeColors()
    End Sub

    Private Sub ApplyThemeColors()
        Dim textColor = If(darkModeEnabledValue, Color.FromArgb(232, 236, 240), SystemColors.ControlText)
        Dim inputBackground = If(darkModeEnabledValue, Color.FromArgb(36, 39, 44), SystemColors.Window)
        Dim inputForeground = If(darkModeEnabledValue, Color.FromArgb(245, 247, 250), SystemColors.WindowText)
        Dim logBackground = If(darkModeEnabledValue, Color.FromArgb(20, 22, 25), SystemColors.Window)

        ApplyThemeColorsRecursive(Me, textColor, inputBackground, inputForeground, logBackground)
        elapsedLabel.ForeColor = Color.FromArgb(230, 73, 73)
        UpdateStatusAccent()
    End Sub

    Private Sub ApplyThemeColorsRecursive(parentControl As Control, textColor As Color, inputBackground As Color, inputForeground As Color, logBackground As Color)
        For Each childControl As Control In parentControl.Controls
            If TypeOf childControl Is TableLayoutPanel OrElse TypeOf childControl Is FlowLayoutPanel OrElse TypeOf childControl Is Panel OrElse TypeOf childControl Is Label Then
                childControl.BackColor = BackColor
            End If

            If TypeOf childControl Is Label AndAlso childControl IsNot statusValueLabel Then
                childControl.ForeColor = textColor
            ElseIf TypeOf childControl Is TextBox Then
                childControl.BackColor = If(childControl Is logTextBox, logBackground, inputBackground)
                childControl.ForeColor = inputForeground
            ElseIf TypeOf childControl Is NumericUpDown OrElse TypeOf childControl Is ComboBox Then
                childControl.BackColor = inputBackground
                childControl.ForeColor = inputForeground
            ElseIf TypeOf childControl Is Button Then
                Dim button = DirectCast(childControl, Button)
                button.UseVisualStyleBackColor = Not darkModeEnabledValue
                button.BackColor = If(darkModeEnabledValue, Color.FromArgb(61, 66, 73), SystemColors.Control)
                button.ForeColor = If(darkModeEnabledValue, Color.FromArgb(245, 247, 250), SystemColors.ControlText)
                button.FlatStyle = If(darkModeEnabledValue, FlatStyle.Flat, FlatStyle.Standard)
                If darkModeEnabledValue Then
                    button.FlatAppearance.BorderColor = Color.FromArgb(90, 96, 104)
                End If
            End If

            If childControl.HasChildren Then
                ApplyThemeColorsRecursive(childControl, textColor, inputBackground, inputForeground, logBackground)
            End If
        Next
    End Sub

    Private Async Sub StartRecording(sender As Object, e As EventArgs)
        If streamRunner IsNot Nothing OrElse isStartingRecordingValue OrElse isStoppingRecordingValue OrElse isStartingPreviewValue OrElse isStoppingPreviewValue Then
            Return
        End If

        Dim sourceValue = urlTextBox.Text.Trim()
        Dim selectedProfile = GetSelectedProfile()
        Dim silenceDurationSeconds = Math.Max(1, Decimal.ToInt32(intervalUpDown.Value) + 1)

        If String.IsNullOrWhiteSpace(sourceValue) Then
            AppendLog("Enter a URL or file path before recording.")
            Return
        End If

        isStartingRecordingValue = True
        statusValueLabel.Text = "Starting"
        statusValueLabel.ForeColor = Color.DarkOrange
        UpdateUiState(False)
        UpdateStatusAccent()
        Directory.CreateDirectory(OutputFolderPath)
        logTextBox.Clear()

        Try
            Dim inputUrls = Await Task.Run(Function() ResolveInputUrls(sourceValue))

            If IsDisposed Then
                Return
            End If

            AppendLog("Stream recording reads the source as fast as it is available. On-demand media can finish faster than real time and stop automatically at end of input; live sources stay near live speed.")

            If selectedProfile.UseFfmbcFinalize Then
                Dim ffmbcPath = ResolveFfmbcPath()
                Dim ffprobePath = ResolveFfprobePath()
                Dim ffmpegPath = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe")
                Dim canUseDirectFfmbcMode = CanUseDirectFfmbcInput(inputUrls)

                If String.IsNullOrWhiteSpace(ffmbcPath) OrElse Not File.Exists(ffmbcPath) Then
                    AppendLog($"ffmbc.exe was not found in {AppContext.BaseDirectory}. Copy the local FFmbc build there before using XDCAM Sony Compatible.")
                    Return
                End If

                If String.IsNullOrWhiteSpace(ffprobePath) OrElse Not File.Exists(ffprobePath) Then
                    AppendLog($"ffprobe.exe was not found in {AppContext.BaseDirectory}. Copy the local FFprobe build there before using XDCAM Sony Compatible.")
                    Return
                End If

                Dim useDirectFfmbcMode = canUseDirectFfmbcMode AndAlso Await Task.Run(Function() ShouldUseDirectFfmbcSegmentMode(inputUrls, ffprobePath))

                If IsDisposed Then
                    Return
                End If

                If useDirectFfmbcMode Then
                    currentRecordingUsesDirectFfmbcValue = True
                    currentRecordingUsesFfmbcFallbackValue = False
                    continueDirectFfmbcRecordingValue = True
                    currentDirectFfmbcInputUrls = inputUrls.ToArray()
                    currentDirectFfmbcProfile = selectedProfile
                    currentDirectFfmbcAudioSource = Await Task.Run(Function() ProbePrimaryAudioSource(currentDirectFfmbcInputUrls, ffprobePath))

                    If IsDisposed Then
                        Return
                    End If

                    currentDirectFfmbcSilenceFilePath = Await Task.Run(Function() EnsureSilenceWavFile(silenceDurationSeconds))

                    If IsDisposed Then
                        Return
                    End If

                    currentRecordingTempOutputFolder = Nothing
                    currentRecordingFinalOutputFolder = Nothing
                    AppendLog("XDCAM Sony Compatible is using direct FFmbc segment recording.")

                    streamRunner = New FfmpegProcessRunner()
                    streamRunner.Start(ffmbcPath, BuildDirectFfmbcRecordingArguments(), OutputFolderPath)
                Else
                    If Not File.Exists(ffmpegPath) Then
                        AppendLog($"ffmpeg.exe was not found in {AppContext.BaseDirectory}.")
                        Return
                    End If

                    If canUseDirectFfmbcMode Then
                        AppendLog("Source appears to be on-demand media. Using FFmpeg ingest so recording stops automatically when the input finishes.")
                    End If

                    currentRecordingUsesDirectFfmbcValue = False
                    currentRecordingUsesFfmbcFallbackValue = True
                    continueDirectFfmbcRecordingValue = False
                    currentDirectFfmbcInputUrls = Nothing
                    currentDirectFfmbcProfile = Nothing
                    currentDirectFfmbcAudioSource = Nothing
                    currentDirectFfmbcSilenceFilePath = Nothing
                    currentRecordingFinalOutputFolder = OutputFolderPath
                    currentRecordingTempOutputFolder = CreateFfmbcTempOutputFolder(OutputFolderPath)
                    Dim tempOutputPattern = Path.Combine(currentRecordingTempOutputFolder, $"Stream_%d%m%Y_%H%M%S{selectedProfile.ContainerExtension}")
                    Dim fallbackAudioSource = Await Task.Run(Function() ProbePrimaryAudioSource(inputUrls, ffprobePath))

                    If IsDisposed Then
                        Return
                    End If

                    Dim fallbackSilenceFilePath = Await Task.Run(Function() EnsureSilenceWavFile(silenceDurationSeconds))

                    If IsDisposed Then
                        Return
                    End If

                    Dim arguments = BuildSonyCompatibleTempRecordingArguments(inputUrls, tempOutputPattern, selectedProfile, fallbackAudioSource, fallbackSilenceFilePath)
                    AppendLog("XDCAM Sony Compatible is using FFmpeg ingest with FFmbc finalization for this source.")

                    streamRunner = New FfmpegProcessRunner()
                    streamRunner.Start(ffmpegPath, arguments, currentRecordingTempOutputFolder)
                End If
            Else
                Dim ffmpegPath = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe")

                If Not File.Exists(ffmpegPath) Then
                    AppendLog($"ffmpeg.exe was not found in {AppContext.BaseDirectory}.")
                    Return
                End If

                currentRecordingUsesDirectFfmbcValue = False
                currentRecordingUsesFfmbcFallbackValue = False
                continueDirectFfmbcRecordingValue = False
                currentDirectFfmbcInputUrls = Nothing
                currentDirectFfmbcProfile = Nothing
                currentDirectFfmbcAudioSource = Nothing
                currentDirectFfmbcSilenceFilePath = Nothing
                currentRecordingTempOutputFolder = Nothing
                currentRecordingFinalOutputFolder = Nothing

                Dim outputPattern = Path.Combine(OutputFolderPath, $"Stream_%d%m%Y_%H%M%S{selectedProfile.ContainerExtension}")
                Dim arguments = BuildRecordingArguments(inputUrls, outputPattern)

                streamRunner = New FfmpegProcessRunner()
                streamRunner.Start(ffmpegPath, arguments, OutputFolderPath)
            End If

            recordingStartedAtUtc = DateTime.UtcNow
            elapsedTimer.Start()
            If currentRecordingUsesFfmbcFallbackValue Then
                StartFfmbcFinalizeTimer()
            Else
                StopFfmbcFinalizeTimer()
            End If
            statusValueLabel.Text = "Recording"
            statusValueLabel.ForeColor = Color.Firebrick
            UpdateElapsedDisplay()
        Catch ex As Exception
            AppendLog($"Failed to start stream recording: {ex.Message}")
            isFinalizingRecordingValue = False
            currentRecordingUsesDirectFfmbcValue = False
            currentRecordingUsesFfmbcFallbackValue = False
            continueDirectFfmbcRecordingValue = False
            currentDirectFfmbcInputUrls = Nothing
            currentDirectFfmbcProfile = Nothing
            currentDirectFfmbcAudioSource = Nothing
            currentDirectFfmbcSilenceFilePath = Nothing
            currentRecordingTempOutputFolder = Nothing
            currentRecordingFinalOutputFolder = Nothing
            StopFfmbcFinalizeTimer()
            TearDownRunner()
            recordingStartedAtUtc = Nothing
            elapsedTimer.Stop()
            statusValueLabel.Text = "Idle"
            statusValueLabel.ForeColor = Color.DarkGreen
        Finally
            isStartingRecordingValue = False

            If streamRunner Is Nothing AndAlso String.Equals(statusValueLabel.Text, "Starting", StringComparison.OrdinalIgnoreCase) Then
                statusValueLabel.Text = "Idle"
                statusValueLabel.ForeColor = Color.DarkGreen
            End If

            UpdateUiState(streamRunner IsNot Nothing)
            UpdateStatusAccent()
        End Try
    End Sub

    Private Function BuildRecordingArguments(inputUrls As IReadOnlyList(Of String), outputPattern As String) As String
        Dim selectedProfile = GetSelectedProfile()

        Dim builder As New StringBuilder()
        builder.Append("-hide_banner -y ")

        For Each inputUrl In inputUrls
            AppendInputArgument(builder, inputUrl)
        Next

        builder.Append("-map 0:v:0 ")

        For inputIndex = 0 To inputUrls.Count - 1
            builder.Append("-map ").Append(inputIndex).Append(":a:0? ")
        Next

        builder.Append("-vf ").Append(Quote("scale=1920:1080:force_original_aspect_ratio=decrease,pad=1920:1080:(ow-iw)/2:(oh-ih)/2,fps=25")).Append(" ")
        builder.Append(selectedProfile.OutputOptions.Trim()).Append(" ")
        builder.Append("-r 25 ")
        builder.Append("-force_key_frames ").Append(Quote($"expr:gte(t,n_forced*{Decimal.ToInt32(intervalUpDown.Value)})")).Append(" ")
        builder.Append("-f segment ")
        builder.Append("-segment_time ").Append(Decimal.ToInt32(intervalUpDown.Value)).Append(" ")
        builder.Append("-reset_timestamps 1 ")
        builder.Append("-segment_start_number 0 ")
        builder.Append("-segment_format ").Append(Quote(GetSegmentFormat(selectedProfile))).Append(" ")
        builder.Append("-strftime 1 ")
        builder.Append(Quote(outputPattern))
        Return builder.ToString()
    End Function

    Private Function BuildSonyCompatibleTempRecordingArguments(inputUrls As IReadOnlyList(Of String), outputPattern As String, selectedProfile As StreamRecordingProfile, audioSource As StreamAudioSourceInfo, silenceFilePath As String) As String
        Dim builder As New StringBuilder()
        builder.Append("-hide_banner -y -fflags +genpts ")

        For Each inputUrl In inputUrls
            AppendInputArgument(builder, inputUrl)
        Next

        Dim silenceInputIndex = inputUrls.Count
        builder.Append("-stream_loop -1 -i ").Append(Quote(silenceFilePath)).Append(" ")
        builder.Append("-filter_complex ").Append(Quote(BuildSonyCompatibleFallbackAudioFilterGraph(audioSource, silenceInputIndex))).Append(" ")
        builder.Append("-map 0:v:0 ")
        builder.Append("-map ").Append(Quote("[rec_a1]")).Append(" ")
        builder.Append("-map ").Append(Quote("[rec_a2]")).Append(" ")
        builder.Append("-map ").Append(silenceInputIndex).Append(":a:0 ")
        builder.Append("-map ").Append(silenceInputIndex).Append(":a:0 ")
        builder.Append("-map ").Append(silenceInputIndex).Append(":a:0 ")
        builder.Append("-map ").Append(silenceInputIndex).Append(":a:0 ")
        builder.Append("-map ").Append(silenceInputIndex).Append(":a:0 ")
        builder.Append("-map ").Append(silenceInputIndex).Append(":a:0 ")
        builder.Append("-vf ").Append(Quote("scale=1920:1080:force_original_aspect_ratio=decrease,pad=1920:1080:(ow-iw)/2:(oh-ih)/2,fps=25")).Append(" ")
        builder.Append(selectedProfile.OutputOptions.Trim()).Append(" ")
        builder.Append("-r 25 ")
        builder.Append("-force_key_frames ").Append(Quote($"expr:gte(t,n_forced*{Decimal.ToInt32(intervalUpDown.Value)})")).Append(" ")
        builder.Append("-f segment ")
        builder.Append("-segment_time ").Append(Decimal.ToInt32(intervalUpDown.Value)).Append(" ")
        builder.Append("-reset_timestamps 1 ")
        builder.Append("-segment_start_number 0 ")
        builder.Append("-segment_format ").Append(Quote(GetSegmentFormat(selectedProfile))).Append(" ")
        builder.Append("-strftime 1 ")
        builder.Append(Quote(outputPattern))
        Return builder.ToString()
    End Function

    Private Function BuildSonyCompatibleFallbackAudioFilterGraph(audioSource As StreamAudioSourceInfo, silenceInputIndex As Integer) As String
        If audioSource Is Nothing Then
            Return $"[{silenceInputIndex}:a:0]anull[rec_a1];[{silenceInputIndex}:a:0]anull[rec_a2]"
        End If

        Dim audioInputLabel = $"[{audioSource.InputIndex}:a:{audioSource.StreamIndex}]"

        If audioSource.Channels >= 2 Then
            Return $"{audioInputLabel}channelsplit=channel_layout=stereo[rec_a1][rec_a2]"
        End If

        Return $"{audioInputLabel}pan=mono|c0=c0[rec_a1];[{silenceInputIndex}:a:0]anull[rec_a2]"
    End Function

    Private Function BuildDirectFfmbcRecordingArguments() As String
        Dim builder As New StringBuilder()
        builder.Append("-y ")

        For Each inputUrl In currentDirectFfmbcInputUrls
            AppendInputArgument(builder, inputUrl)
        Next

        Dim silenceInputIndex = currentDirectFfmbcInputUrls.Count
        builder.Append("-i ").Append(Quote(currentDirectFfmbcSilenceFilePath)).Append(" ")
        builder.Append("-t ").Append(Decimal.ToInt32(intervalUpDown.Value)).Append(" ")
        builder.Append("-an ")
        builder.Append("-vf ").Append(Quote("scale=1920:1080:force_original_aspect_ratio=decrease,pad=1920:1080:(ow-iw)/2:(oh-ih)/2,fps=25")).Append(" ")
        builder.Append("-target xdcamhd422 -tff -vtag xd5c ")
        builder.Append("-r 25 ")
        builder.Append(Quote(CreateDirectFfmbcOutputPath())).Append(" ")
        builder.Append("-acodec pcm_s24le -ar 48000 -newaudio -map_audio_channel ").Append(BuildMapAudioChannelArgument(currentDirectFfmbcAudioSource.InputIndex, currentDirectFfmbcAudioSource.StreamIndex, 0, 1)).Append(" ")

        If currentDirectFfmbcAudioSource.Channels >= 2 Then
            builder.Append("-acodec pcm_s24le -ar 48000 -newaudio -map_audio_channel ").Append(BuildMapAudioChannelArgument(currentDirectFfmbcAudioSource.InputIndex, currentDirectFfmbcAudioSource.StreamIndex, 1, 2)).Append(" ")
        Else
            builder.Append("-acodec pcm_s24le -ar 48000 -newaudio -map_audio_channel ").Append(BuildMapAudioChannelArgument(silenceInputIndex, 0, 0, 2)).Append(" ")
        End If

        For outputAudioStreamIndex = 3 To 8
            builder.Append("-acodec pcm_s24le -ar 48000 -newaudio -map_audio_channel ").Append(BuildMapAudioChannelArgument(silenceInputIndex, 0, 0, outputAudioStreamIndex)).Append(" ")
        Next

        Return builder.ToString()
    End Function

    Private Function BuildMapAudioChannelArgument(inputIndex As Integer, streamIndex As Integer, channelIndex As Integer, outputStreamIndex As Integer) As String
        Return $"{inputIndex}:{streamIndex}:{channelIndex}:0:{outputStreamIndex}:0"
    End Function

    Private Sub OnSettingsChanged(sender As Object, e As EventArgs)
        SaveStreamSettings()
    End Sub

    Private Sub LoadStreamSettings()
        Dim settingsFilePathValue = SettingsFilePath

        If Not File.Exists(settingsFilePathValue) Then
            Return
        End If

        suppressSettingsSave = True

        Try
            For Each rawLine In File.ReadAllLines(settingsFilePathValue)
                If String.IsNullOrWhiteSpace(rawLine) Then
                    Continue For
                End If

                Dim separatorIndex = rawLine.IndexOf("="c)

                If separatorIndex <= 0 Then
                    Continue For
                End If

                Dim key = rawLine.Substring(0, separatorIndex).Trim()
                Dim value = rawLine.Substring(separatorIndex + 1).Trim()

                Select Case key.ToLowerInvariant()
                    Case "url"
                        If Not String.IsNullOrWhiteSpace(value) Then
                            urlTextBox.Text = value
                        End If
                    Case "profile"
                        SelectProfileByName(value)
                    Case "interval"
                        Dim parsedInterval As Integer

                        If Integer.TryParse(value, parsedInterval) Then
                            intervalUpDown.Value = Math.Max(CInt(intervalUpDown.Minimum), Math.Min(CInt(intervalUpDown.Maximum), parsedInterval))
                        End If
                End Select
            Next
        Finally
            suppressSettingsSave = False
        End Try
    End Sub

    Private Sub SaveStreamSettings()
        If suppressSettingsSave Then
            Return
        End If

        Dim settingsFilePathValue = SettingsFilePath
        Dim settingsDirectory = Path.GetDirectoryName(settingsFilePathValue)

        If Not String.IsNullOrWhiteSpace(settingsDirectory) Then
            Directory.CreateDirectory(settingsDirectory)
        End If

        Dim lines = {
            $"url={urlTextBox.Text.Trim()}",
            $"profile={GetSelectedProfile().DisplayName}",
            $"interval={Decimal.ToInt32(intervalUpDown.Value)}"
        }

        File.WriteAllLines(settingsFilePathValue, lines)
    End Sub

    Private Sub SelectProfileByName(profileName As String)
        If String.IsNullOrWhiteSpace(profileName) Then
            Return
        End If

        For Each item As Object In profileComboBox.Items
            Dim profile = TryCast(item, StreamRecordingProfile)

            If profile IsNot Nothing AndAlso String.Equals(profile.DisplayName, profileName, StringComparison.OrdinalIgnoreCase) Then
                profileComboBox.SelectedItem = profile
                Return
            End If
        Next
    End Sub

    Private Function BuildPreviewArguments(inputUrls As IReadOnlyList(Of String)) As String
        Dim builder As New StringBuilder()
        builder.Append("-hide_banner -loglevel warning -fflags nobuffer -flags low_delay ")

        For Each inputUrl In inputUrls
            AppendPreviewInputArgument(builder, inputUrl)
        Next

        builder.Append("-filter_complex ").Append(Quote(BuildPreviewFilterGraph(inputUrls))).Append(" ")
        builder.Append("-map ").Append(Quote("[out]")).Append(" ")
        builder.Append("-an -flush_packets 1 -c:v mjpeg -q:v 6 -f mjpeg pipe:1")
        Return builder.ToString()
    End Function

    Private Sub AppendPreviewInputArgument(builder As StringBuilder, inputUrl As String)
        If builder Is Nothing Then
            Return
        End If

        builder.Append("-re ")
        builder.Append("-i ").Append(Quote(inputUrl)).Append(" ")
    End Sub

    Private Sub AppendInputArgument(builder As StringBuilder, inputUrl As String)
        If builder Is Nothing Then
            Return
        End If

        builder.Append("-i ").Append(Quote(inputUrl)).Append(" ")
    End Sub

    Private Shared Function CanUseDirectFfmbcInput(inputUrls As IReadOnlyList(Of String)) As Boolean
        If inputUrls Is Nothing OrElse inputUrls.Count = 0 Then
            Return False
        End If

        Return inputUrls.All(AddressOf CanUseDirectFfmbcInput)
    End Function

    Private Shared Function CanUseDirectFfmbcInput(inputUrl As String) As Boolean
        If String.IsNullOrWhiteSpace(inputUrl) Then
            Return False
        End If

        Dim parsedUri As Uri = Nothing

        If Uri.TryCreate(inputUrl, UriKind.Absolute, parsedUri) Then
            If parsedUri.IsFile Then
                Return True
            End If

            Return String.Equals(parsedUri.Scheme, "http", StringComparison.OrdinalIgnoreCase)
        End If

        Return Path.IsPathRooted(inputUrl) OrElse
            inputUrl.StartsWith(".\", StringComparison.OrdinalIgnoreCase) OrElse
            inputUrl.StartsWith("..\", StringComparison.OrdinalIgnoreCase)
    End Function

    Private Function ShouldUseDirectFfmbcSegmentMode(inputUrls As IReadOnlyList(Of String), ffprobePath As String) As Boolean
        If Not CanUseDirectFfmbcInput(inputUrls) Then
            Return False
        End If

        Return Not HasFiniteInputDuration(inputUrls, ffprobePath)
    End Function

    Private Function HasFiniteInputDuration(inputUrls As IReadOnlyList(Of String), ffprobePath As String) As Boolean
        If inputUrls Is Nothing OrElse inputUrls.Count = 0 Then
            Return False
        End If

        For Each inputUrl In inputUrls
            If HasFiniteInputDuration(inputUrl, ffprobePath) Then
                Return True
            End If
        Next

        Return False
    End Function

    Private Function HasFiniteInputDuration(inputUrl As String, ffprobePath As String) As Boolean
        If IsClearlyFiniteInput(inputUrl) Then
            Return True
        End If

        Dim durationSeconds = 0.0
        Return TryGetInputDurationSeconds(inputUrl, ffprobePath, durationSeconds) AndAlso durationSeconds > 0.1
    End Function

    Private Shared Function IsClearlyFiniteInput(inputUrl As String) As Boolean
        If String.IsNullOrWhiteSpace(inputUrl) Then
            Return False
        End If

        Dim parsedUri As Uri = Nothing

        If Uri.TryCreate(inputUrl, UriKind.Absolute, parsedUri) Then
            Return parsedUri.IsFile
        End If

        Return Path.IsPathRooted(inputUrl) OrElse
            inputUrl.StartsWith(".\", StringComparison.OrdinalIgnoreCase) OrElse
            inputUrl.StartsWith("..\", StringComparison.OrdinalIgnoreCase)
    End Function

    Private Shared Function TryGetInputDurationSeconds(inputUrl As String, ffprobePath As String, ByRef durationSeconds As Double) As Boolean
        durationSeconds = 0.0

        If String.IsNullOrWhiteSpace(inputUrl) OrElse String.IsNullOrWhiteSpace(ffprobePath) OrElse Not File.Exists(ffprobePath) Then
            Return False
        End If

        Dim startInfo As New ProcessStartInfo() With {
            .FileName = ffprobePath,
            .Arguments = $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 {Quote(inputUrl)}",
            .WorkingDirectory = AppContext.BaseDirectory,
            .UseShellExecute = False,
            .RedirectStandardOutput = True,
            .RedirectStandardError = True,
            .CreateNoWindow = True
        }

        Try
            Using process As New Process() With {.StartInfo = startInfo}
                If Not process.Start() Then
                    Return False
                End If

                Dim standardOutput = process.StandardOutput.ReadToEnd().Trim()
                process.StandardError.ReadToEnd()
                process.WaitForExit()

                If process.ExitCode <> 0 Then
                    Return False
                End If

                Return Double.TryParse(standardOutput, NumberStyles.Float Or NumberStyles.AllowThousands, CultureInfo.InvariantCulture, durationSeconds)
            End Using
        Catch
            Return False
        End Try
    End Function

    Private Function BuildPreviewFilterGraph(inputUrls As IReadOnlyList(Of String)) As String
        Dim audioInputLabel = If(inputUrls.Count >= 2, "[1:a]", "[0:a]")
        Dim previewWidth = 320
        Dim previewHeight = 180
        Dim meterChannelWidth = 80
        Dim meterOutputWidth = 18
        Dim rightMeterPan = "mono|c0=c1"

        Return $"[0:v]scale={previewWidth}:{previewHeight}:force_original_aspect_ratio=decrease,pad={previewWidth}:{previewHeight}:(ow-iw)/2:(oh-ih)/2,fps=8,format=yuv420p[video];{audioInputLabel}asplit=2[left_meter_src][right_meter_src];[left_meter_src]pan=mono|c0=c0,showvolume=r=8:w={meterChannelWidth}:h={previewHeight}:f=0.92:b=2:t=0:v=1:dm=1:o=v:ds=log:p=0.18:m=r[left_bar_src];[left_bar_src]scale={meterOutputWidth}:{previewHeight},format=yuv420p[left_bar];[right_meter_src]pan={rightMeterPan},showvolume=r=8:w={meterChannelWidth}:h={previewHeight}:f=0.92:b=2:t=0:v=1:dm=1:o=v:ds=log:p=0.18:m=r[right_bar_src];[right_bar_src]scale={meterOutputWidth}:{previewHeight},format=yuv420p[right_bar];[left_bar][video][right_bar]hstack=inputs=3[out]"
    End Function

    Private Function GetSelectedProfile() As StreamRecordingProfile
        Return If(TryCast(profileComboBox.SelectedItem, StreamRecordingProfile), DirectCast(profileComboBox.Items(0), StreamRecordingProfile))
    End Function

    Private Function ResolveFfprobePath() As String
        Dim ffprobePath = Path.Combine(AppContext.BaseDirectory, "ffprobe.exe")
        Return If(File.Exists(ffprobePath), ffprobePath, Nothing)
    End Function

    Private Function ResolveFfmbcPath() As String
        Dim primaryPath = Path.Combine(AppContext.BaseDirectory, "ffmbc.exe")

        If File.Exists(primaryPath) Then
            Return primaryPath
        End If

        For Each candidatePath In Directory.EnumerateFiles(AppContext.BaseDirectory, "ffmbc*.exe")
            If File.Exists(candidatePath) Then
                Return candidatePath
            End If
        Next

        Return Nothing
    End Function

    Private Function CreateFfmbcTempOutputFolder(finalOutputFolder As String) As String
        Dim tempFolder = Path.Combine(finalOutputFolder, ".ffmbc-temp", "STREAM", DateTime.Now.ToString("yyyyMMdd_HHmmss_fff"))
        Directory.CreateDirectory(tempFolder)
        Return tempFolder
    End Function

    Private Function CreateDirectFfmbcOutputPath() As String
        Dim timestamp = DateTime.Now.ToString("ddMMyyyy_HHmmss")
        Dim candidatePath = Path.Combine(OutputFolderPath, $"Stream_{timestamp}{currentDirectFfmbcProfile.ContainerExtension}")
        Dim suffix = 1

        While File.Exists(candidatePath)
            candidatePath = Path.Combine(OutputFolderPath, $"Stream_{timestamp}_{suffix:00}{currentDirectFfmbcProfile.ContainerExtension}")
            suffix += 1
        End While

        Return candidatePath
    End Function

    Private Function ProbePrimaryAudioSource(inputUrls As IReadOnlyList(Of String), ffprobePath As String) As StreamAudioSourceInfo
        Dim preferredInputIndex = If(inputUrls.Count >= 2, 1, 0)
        Dim preferredInput = inputUrls(preferredInputIndex)
        Dim probeArguments = $"-v error -select_streams a -show_entries stream=index,channels -of csv=p=0 {Quote(preferredInput)}"
        Dim startInfo As New ProcessStartInfo() With {
            .FileName = ffprobePath,
            .Arguments = probeArguments,
            .WorkingDirectory = AppContext.BaseDirectory,
            .UseShellExecute = False,
            .RedirectStandardOutput = True,
            .RedirectStandardError = True,
            .CreateNoWindow = True
        }

        Using process As New Process() With {.StartInfo = startInfo}
            If Not process.Start() Then
                Throw New InvalidOperationException("ffprobe could not be started.")
            End If

            Dim standardOutput = process.StandardOutput.ReadToEnd()
            Dim standardError = process.StandardError.ReadToEnd()
            process.WaitForExit()

            If process.ExitCode <> 0 Then
                Throw New InvalidOperationException($"ffprobe failed: {standardError.Trim()}")
            End If

            Dim firstLine = standardOutput.Split({ControlChars.CrLf, ControlChars.Lf}, StringSplitOptions.RemoveEmptyEntries).
                Select(Function(line) line.Trim()).
                FirstOrDefault(Function(line) Not String.IsNullOrWhiteSpace(line))

            If String.IsNullOrWhiteSpace(firstLine) Then
                Throw New InvalidOperationException("ffprobe did not find an audio stream.")
            End If

            Dim parts = firstLine.Split(","c)
            Dim streamIndex = 0
            Dim channels = 2

            If parts.Length >= 1 Then
                Integer.TryParse(parts(0).Trim(), streamIndex)
            End If

            If parts.Length >= 2 Then
                Integer.TryParse(parts(1).Trim(), channels)
            End If

            Return New StreamAudioSourceInfo(preferredInputIndex, streamIndex, Math.Max(1, channels))
        End Using
    End Function

    Private Function EnsureSilenceWavFile(durationSeconds As Integer) As String
        Dim safeDurationSeconds = Math.Max(1, durationSeconds)
        Dim cacheFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FfmpegRecorder", "SilenceCache")
        Directory.CreateDirectory(cacheFolder)

        Dim outputPath = Path.Combine(cacheFolder, $"silence_mono_48k_{safeDurationSeconds}s.wav")

        If File.Exists(outputPath) Then
            Return outputPath
        End If

        Const sampleRate As Integer = 48000
        Const bitsPerSample As Short = 16
        Const channels As Short = 1
        Dim bytesPerSample As Integer = bitsPerSample \ 8
        Dim totalSamples As Integer = sampleRate * safeDurationSeconds
        Dim dataSize As Integer = totalSamples * channels * bytesPerSample

        Using fileStream As New FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.Read)
            Using writer As New BinaryWriter(fileStream, Encoding.ASCII, leaveOpen:=True)
                writer.Write(Encoding.ASCII.GetBytes("RIFF"))
                writer.Write(36 + dataSize)
                writer.Write(Encoding.ASCII.GetBytes("WAVE"))
                writer.Write(Encoding.ASCII.GetBytes("fmt "))
                writer.Write(16)
                writer.Write(CShort(1))
                writer.Write(channels)
                writer.Write(sampleRate)
                writer.Write(sampleRate * channels * bytesPerSample)
                writer.Write(CShort(channels * bytesPerSample))
                writer.Write(bitsPerSample)
                writer.Write(Encoding.ASCII.GetBytes("data"))
                writer.Write(dataSize)
                writer.Flush()
            End Using

            fileStream.SetLength(44L + dataSize)
        End Using

        Return outputPath
    End Function

    Private Function BuildFfmbcSonyCompatibleArguments(inputFilePath As String, outputFilePath As String) As String
        Return $"-y -i {Quote(inputFilePath)} -an -target xdcamhd422 -tff -vtag xd5c {Quote(outputFilePath)} " &
            "-acodec pcm_s24le -ar 48000 -newaudio -map_audio_channel 0:1:0:0:1:0 " &
            "-acodec pcm_s24le -ar 48000 -newaudio -map_audio_channel 0:2:0:0:2:0 " &
            "-acodec pcm_s24le -ar 48000 -newaudio -map_audio_channel 0:3:0:0:3:0 " &
            "-acodec pcm_s24le -ar 48000 -newaudio -map_audio_channel 0:4:0:0:4:0 " &
            "-acodec pcm_s24le -ar 48000 -newaudio -map_audio_channel 0:5:0:0:5:0 " &
            "-acodec pcm_s24le -ar 48000 -newaudio -map_audio_channel 0:6:0:0:6:0 " &
            "-acodec pcm_s24le -ar 48000 -newaudio -map_audio_channel 0:7:0:0:7:0 " &
            "-acodec pcm_s24le -ar 48000 -newaudio -map_audio_channel 0:8:0:0:8:0"
    End Function

    Private Function RunFinalizeProcess(executablePath As String, arguments As String, workingDirectory As String) As String
        Dim startInfo As New ProcessStartInfo() With {
            .FileName = executablePath,
            .Arguments = arguments,
            .WorkingDirectory = workingDirectory,
            .UseShellExecute = False,
            .RedirectStandardOutput = True,
            .RedirectStandardError = True,
            .CreateNoWindow = True
        }

        Using process As New Process() With {.StartInfo = startInfo}
            If Not process.Start() Then
                Return "FFmbc could not be started."
            End If

            Dim standardOutput As New StringBuilder()
            Dim standardError As New StringBuilder()

            AddHandler process.OutputDataReceived,
                Sub(sender, e)
                    If e.Data IsNot Nothing Then
                        standardOutput.AppendLine(e.Data)
                    End If
                End Sub

            AddHandler process.ErrorDataReceived,
                Sub(sender, e)
                    If e.Data IsNot Nothing Then
                        standardError.AppendLine(e.Data)
                    End If
                End Sub

            process.BeginOutputReadLine()
            process.BeginErrorReadLine()
            process.WaitForExit()

            If standardOutput.Length > 0 Then
                For Each line In standardOutput.ToString().Split({ControlChars.CrLf, ControlChars.Lf}, StringSplitOptions.RemoveEmptyEntries)
                    AppendLog($"FFmbc: {line}")
                Next
            End If

            If standardError.Length > 0 Then
                For Each line In standardError.ToString().Split({ControlChars.CrLf, ControlChars.Lf}, StringSplitOptions.RemoveEmptyEntries)
                    AppendLog($"FFmbc: {line}")
                Next
            End If

            If process.ExitCode <> 0 Then
                Return $"FFmbc exited with code {process.ExitCode}."
            End If
        End Using

        Return Nothing
    End Function

    Private Sub ResetFfmbcFinalizeState()
        SyncLock ffmbcFinalizeSync
            ffmbcProcessedTempFiles.Clear()
            ffmbcProcessingTempFiles.Clear()
            ffmbcBackgroundFinalizeTask = Nothing
        End SyncLock
    End Sub

    Private Function GetFfmbcCandidateFiles(tempOutputFolder As String, includeNewestFile As Boolean) As List(Of String)
        If String.IsNullOrWhiteSpace(tempOutputFolder) OrElse Not Directory.Exists(tempOutputFolder) Then
            Return New List(Of String)()
        End If

        Dim allFiles = Directory.GetFiles(tempOutputFolder, "*.mxf", SearchOption.TopDirectoryOnly).
            OrderBy(Function(path) path, StringComparer.OrdinalIgnoreCase).
            ToArray()

        If allFiles.Length = 0 Then
            Return New List(Of String)()
        End If

        Dim candidateFiles = allFiles.AsEnumerable()

        If Not includeNewestFile AndAlso allFiles.Length > 0 Then
            candidateFiles = candidateFiles.Take(allFiles.Length - 1)
        End If

        SyncLock ffmbcFinalizeSync
            Return candidateFiles.
                Where(Function(path) Not ffmbcProcessedTempFiles.Contains(path) AndAlso Not ffmbcProcessingTempFiles.Contains(path)).
                ToList()
        End SyncLock
    End Function

    Private Function HasAnyFfmbcTempFiles(tempOutputFolder As String) As Boolean
        If String.IsNullOrWhiteSpace(tempOutputFolder) OrElse Not Directory.Exists(tempOutputFolder) Then
            Return False
        End If

        Return Directory.EnumerateFiles(tempOutputFolder, "*.mxf", SearchOption.TopDirectoryOnly).Any()
    End Function

    Private Function GetExistingFfmbcTempFileCount(tempOutputFolder As String) As Integer
        If String.IsNullOrWhiteSpace(tempOutputFolder) OrElse Not Directory.Exists(tempOutputFolder) Then
            Return 0
        End If

        Return Directory.EnumerateFiles(tempOutputFolder, "*.mxf", SearchOption.TopDirectoryOnly).Count()
    End Function

    Private Function GetPendingFfmbcFinalizeSessionsSnapshot() As List(Of PendingFfmbcFinalizeSession)
        SyncLock ffmbcFinalizeSync
            Return pendingFfmbcFinalizeSessions.ToList()
        End SyncLock
    End Function

    Private Sub AddPendingFfmbcFinalizeSession(tempOutputFolder As String, finalOutputFolder As String)
        If String.IsNullOrWhiteSpace(tempOutputFolder) OrElse String.IsNullOrWhiteSpace(finalOutputFolder) Then
            Return
        End If

        SyncLock ffmbcFinalizeSync
            If pendingFfmbcFinalizeSessions.Any(Function(session) String.Equals(session.TempOutputFolder, tempOutputFolder, StringComparison.OrdinalIgnoreCase)) Then
                Return
            End If

            pendingFfmbcFinalizeSessions.Add(New PendingFfmbcFinalizeSession(tempOutputFolder, finalOutputFolder))
        End SyncLock
    End Sub

    Private Function GetPendingFinalizeClipCount() As Integer
        Dim totalCount = 0

        For Each pendingSession In GetPendingFfmbcFinalizeSessionsSnapshot()
            Dim knownFiles As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase)

            If Directory.Exists(pendingSession.TempOutputFolder) Then
                For Each filePath In Directory.EnumerateFiles(pendingSession.TempOutputFolder, "*.mxf", SearchOption.TopDirectoryOnly)
                    knownFiles.Add(filePath)
                Next
            End If

            SyncLock ffmbcFinalizeSync
                For Each processingFilePath In ffmbcProcessingTempFiles
                    If String.Equals(Path.GetDirectoryName(processingFilePath), pendingSession.TempOutputFolder, StringComparison.OrdinalIgnoreCase) Then
                        knownFiles.Add(processingFilePath)
                    End If
                Next
            End SyncLock

            totalCount += knownFiles.Count
        Next

        Return totalCount
    End Function

    Private Function HasPendingFfmbcFinalizeWork() As Boolean
        If streamRunner IsNot Nothing AndAlso currentRecordingUsesFfmbcFallbackValue AndAlso Not String.IsNullOrWhiteSpace(currentRecordingTempOutputFolder) Then
            Return True
        End If

        If GetPendingFfmbcFinalizeSessionsSnapshot().Count > 0 Then
            Return True
        End If

        SyncLock ffmbcFinalizeSync
            Return ffmbcProcessingTempFiles.Count > 0
        End SyncLock
    End Function

    Private Sub CleanupCompletedFfmbcFinalizeSessions()
        SyncLock ffmbcFinalizeSync
            For index = pendingFfmbcFinalizeSessions.Count - 1 To 0 Step -1
                Dim pendingSession = pendingFfmbcFinalizeSessions(index)
                Dim hasFiles = Directory.Exists(pendingSession.TempOutputFolder) AndAlso Directory.EnumerateFiles(pendingSession.TempOutputFolder, "*.mxf", SearchOption.TopDirectoryOnly).Any()
                Dim hasProcessingFiles = ffmbcProcessingTempFiles.Any(Function(filePath) String.Equals(Path.GetDirectoryName(filePath), pendingSession.TempOutputFolder, StringComparison.OrdinalIgnoreCase))

                If hasFiles OrElse hasProcessingFiles Then
                    Continue For
                End If

                pendingFfmbcFinalizeSessions.RemoveAt(index)

                Try
                    If Directory.Exists(pendingSession.TempOutputFolder) AndAlso Directory.GetFileSystemEntries(pendingSession.TempOutputFolder).Length = 0 Then
                        Directory.Delete(pendingSession.TempOutputFolder, recursive:=False)
                    End If
                Catch
                End Try
            Next
        End SyncLock
    End Sub

    Private Sub UpdateFinalizeStatus(tempOutputFolder As String)
        If InvokeRequired Then
            BeginInvoke(New Action(Of String)(AddressOf UpdateFinalizeStatus), tempOutputFolder)
            Return
        End If

        CleanupCompletedFfmbcFinalizeSessions()

        Dim wasFinalizing = isFinalizingRecordingValue
        Dim remainingClipCount = GetPendingFinalizeClipCount()
        isFinalizingRecordingValue = remainingClipCount > 0

        If streamRunner IsNot Nothing Then
            UpdateStatusAccent()
            Return
        End If

        If remainingClipCount > 0 Then
            statusValueLabel.Text = $"Finalizing {remainingClipCount} clip{If(remainingClipCount = 1, "", "s")}..."
            statusValueLabel.ForeColor = Color.DarkOrange
        Else
            statusValueLabel.Text = "Ready"
            statusValueLabel.ForeColor = Color.DarkGreen
            If wasFinalizing Then
                AppendLog("Ready. Finalization queue is empty.")
            End If
        End If

        UpdateStatusAccent()
        UpdateUiState(False)
    End Sub

    Private Function ClaimFfmbcCandidateFiles(tempOutputFolder As String, includeNewestFile As Boolean) As List(Of String)
        Dim candidateFiles = GetFfmbcCandidateFiles(tempOutputFolder, includeNewestFile)

        If candidateFiles.Count = 0 Then
            Return candidateFiles
        End If

        SyncLock ffmbcFinalizeSync
            For Each candidateFile In candidateFiles
                ffmbcProcessingTempFiles.Add(candidateFile)
            Next
        End SyncLock

        Return candidateFiles
    End Function

    Private Function FinalizeFfmbcFiles(tempFilePaths As IEnumerable(Of String), finalOutputFolder As String, Optional isBackgroundBatch As Boolean = False) As String
        Dim ffmbcPath = ResolveFfmbcPath()

        If String.IsNullOrWhiteSpace(ffmbcPath) OrElse Not File.Exists(ffmbcPath) Then
            Return $"ffmbc.exe was not found in {AppContext.BaseDirectory}."
        End If

        Directory.CreateDirectory(finalOutputFolder)
        Dim finalizedFiles As New List(Of String)()
        Dim claimedFiles = tempFilePaths.ToList()

        Try
            For Each tempFilePath In claimedFiles
                UpdateFinalizeStatus(Path.GetDirectoryName(tempFilePath))
                Dim finalFilePath = Path.Combine(finalOutputFolder, Path.GetFileName(tempFilePath))
                Dim prefix = If(isBackgroundBatch, "Background finalizing", "Finalizing")
                AppendLog($"{prefix} {Path.GetFileName(tempFilePath)} with FFmbc...")

                Dim finalizeError = RunFinalizeProcess(ffmbcPath, BuildFfmbcSonyCompatibleArguments(tempFilePath, finalFilePath), finalOutputFolder)

                If Not String.IsNullOrWhiteSpace(finalizeError) Then
                    If isBackgroundBatch Then
                        AppendLog($"Sony-compatible background finalization failed for {Path.GetFileName(tempFilePath)}: {finalizeError}")
                    End If

                    Return finalizeError
                End If

                finalizedFiles.Add(tempFilePath)

                Try
                    File.Delete(tempFilePath)
                Catch ex As Exception
                    AppendLog($"FFmbc finalize succeeded, but the temp file could not be deleted: {ex.Message}")
                End Try

                UpdateFinalizeStatus(Path.GetDirectoryName(tempFilePath))
            Next
        Finally
            SyncLock ffmbcFinalizeSync
                For Each claimedFile In claimedFiles
                    ffmbcProcessingTempFiles.Remove(claimedFile)
                Next

                For Each finalizedFile In finalizedFiles
                    ffmbcProcessedTempFiles.Add(finalizedFile)
                Next
            End SyncLock
        End Try

        Return Nothing
    End Function

    Private Sub StartBackgroundFfmbcFinalization()
        Dim finalizeSessions = GetPendingFfmbcFinalizeSessionsSnapshot()

        If currentRecordingUsesFfmbcFallbackValue AndAlso streamRunner IsNot Nothing AndAlso
            Not String.IsNullOrWhiteSpace(currentRecordingTempOutputFolder) AndAlso
            Not String.IsNullOrWhiteSpace(currentRecordingFinalOutputFolder) Then
            finalizeSessions.Add(New PendingFfmbcFinalizeSession(currentRecordingTempOutputFolder, currentRecordingFinalOutputFolder))
        End If

        For Each finalizeSession In finalizeSessions
            Dim isCurrentRecordingSession = streamRunner IsNot Nothing AndAlso
                currentRecordingUsesFfmbcFallbackValue AndAlso
                String.Equals(finalizeSession.TempOutputFolder, currentRecordingTempOutputFolder, StringComparison.OrdinalIgnoreCase)

            Dim candidateFiles = ClaimFfmbcCandidateFiles(finalizeSession.TempOutputFolder, includeNewestFile:=Not isCurrentRecordingSession)
            QueueFfmbcFiles(candidateFiles, finalizeSession.FinalOutputFolder, isBackgroundBatch:=True)
        Next
    End Sub

    Private Sub QueueFfmbcFiles(tempFilePaths As IEnumerable(Of String), finalOutputFolder As String, Optional isBackgroundBatch As Boolean = False)
        If tempFilePaths Is Nothing Then
            Return
        End If

        For Each tempFilePath In tempFilePaths
            Dim queuedFilePath = tempFilePath
            Dim queuedOutputFolder = finalOutputFolder
            Dim queuedIsBackgroundBatch = isBackgroundBatch

            FfmbcConversionQueue.Enqueue(
                Sub()
                    ProcessQueuedFfmbcFile(queuedFilePath, queuedOutputFolder, queuedIsBackgroundBatch)
                End Sub)
        Next
    End Sub

    Private Sub ProcessQueuedFfmbcFile(tempFilePath As String, finalOutputFolder As String, isBackgroundBatch As Boolean)
        Dim tempOutputFolder = Path.GetDirectoryName(tempFilePath)
        Dim finalFilePath = Path.Combine(finalOutputFolder, Path.GetFileName(tempFilePath))
        Dim prefix = If(isBackgroundBatch, "Background finalizing", "Finalizing")
        Dim finalizeError As String = Nothing

        Try
            Dim ffmbcPath = ResolveFfmbcPath()

            If String.IsNullOrWhiteSpace(ffmbcPath) OrElse Not File.Exists(ffmbcPath) Then
                finalizeError = $"ffmbc.exe was not found in {AppContext.BaseDirectory}."
                Return
            End If

            Directory.CreateDirectory(finalOutputFolder)
            UpdateFinalizeStatus(tempOutputFolder)
            AppendLog($"{prefix} {Path.GetFileName(tempFilePath)} with FFmbc...")
            finalizeError = RunFinalizeProcess(ffmbcPath, BuildFfmbcSonyCompatibleArguments(tempFilePath, finalFilePath), finalOutputFolder)

            If String.IsNullOrWhiteSpace(finalizeError) Then
                Try
                    File.Delete(tempFilePath)
                Catch ex As Exception
                    AppendLog($"FFmbc finalize succeeded, but the temp file could not be deleted: {ex.Message}")
                End Try
            End If
        Finally
            SyncLock ffmbcFinalizeSync
                ffmbcProcessingTempFiles.Remove(tempFilePath)

                If String.IsNullOrWhiteSpace(finalizeError) Then
                    ffmbcProcessedTempFiles.Add(tempFilePath)
                End If
            End SyncLock

            If Not String.IsNullOrWhiteSpace(finalizeError) Then
                AppendLog($"Sony-compatible stream finalization failed: {finalizeError}")
            End If

            CleanupCompletedFfmbcFinalizeSessions()
            UpdateFinalizeStatus(tempOutputFolder)
        End Try
    End Sub

    Private Async Function AwaitBackgroundFfmbcFinalizationAsync() As Task(Of String)
        Dim backgroundTask As Task(Of String) = Nothing

        SyncLock ffmbcFinalizeSync
            backgroundTask = ffmbcBackgroundFinalizeTask
        End SyncLock

        If backgroundTask Is Nothing Then
            Return Nothing
        End If

        Dim finalizeError = Await backgroundTask

        SyncLock ffmbcFinalizeSync
            If Object.ReferenceEquals(ffmbcBackgroundFinalizeTask, backgroundTask) Then
                ffmbcBackgroundFinalizeTask = Nothing
            End If
        End SyncLock

        Return finalizeError
    End Function

    Private Async Function FinalizeAllPendingFfmbcFilesAsync(tempOutputFolder As String, finalOutputFolder As String) As Task(Of String)
        Dim emptyPassCount = 0

        Do
            Dim backgroundError = Await AwaitBackgroundFfmbcFinalizationAsync()

            If Not String.IsNullOrWhiteSpace(backgroundError) Then
                Return backgroundError
            End If

            Dim finalizeError = Await Task.Run(Function() FinalizeRemainingRecordingWithFfmbc(tempOutputFolder, finalOutputFolder))

            If Not String.IsNullOrWhiteSpace(finalizeError) Then
                Return finalizeError
            End If

            Dim hasPendingFiles = GetFfmbcCandidateFiles(tempOutputFolder, includeNewestFile:=True).Count > 0
            Dim hasActiveBackgroundTask As Boolean

            SyncLock ffmbcFinalizeSync
                hasActiveBackgroundTask = ffmbcBackgroundFinalizeTask IsNot Nothing AndAlso Not ffmbcBackgroundFinalizeTask.IsCompleted
            End SyncLock

            If hasPendingFiles OrElse hasActiveBackgroundTask Then
                emptyPassCount = 0
                Continue Do
            End If

            emptyPassCount += 1

            If emptyPassCount >= 2 Then
                Return Nothing
            End If

            Await Task.Delay(250)
        Loop
    End Function

    Private Function FinalizeRemainingRecordingWithFfmbc(tempOutputFolder As String, finalOutputFolder As String) As String
        If String.IsNullOrWhiteSpace(tempOutputFolder) OrElse Not Directory.Exists(tempOutputFolder) Then
            Return Nothing
        End If

        Dim remainingFiles = ClaimFfmbcCandidateFiles(tempOutputFolder, includeNewestFile:=True)

        If remainingFiles.Count = 0 Then
            Return Nothing
        End If

        Dim finalizeError = FinalizeFfmbcFiles(remainingFiles, finalOutputFolder)

        Try
            If Directory.Exists(tempOutputFolder) AndAlso Directory.GetFileSystemEntries(tempOutputFolder).Length = 0 Then
                Directory.Delete(tempOutputFolder, recursive:=False)
            End If
        Catch
        End Try

        Return finalizeError
    End Function

    Private Sub StartFfmbcFinalizeTimer()
        If Not ffmbcFinalizeTimer.Enabled Then
            ffmbcFinalizeTimer.Start()
        End If
    End Sub

    Private Sub StopFfmbcFinalizeTimer()
        ffmbcFinalizeTimer.Stop()
    End Sub

    Private Sub OnFfmbcFinalizeTimerTick(sender As Object, e As EventArgs)
        CleanupCompletedFfmbcFinalizeSessions()

        If Not HasPendingFfmbcFinalizeWork() Then
            StopFfmbcFinalizeTimer()
            Return
        End If

        StartBackgroundFfmbcFinalization()
    End Sub

    Private Shared Function GetSegmentFormat(profile As StreamRecordingProfile) As String
        Return If(String.IsNullOrWhiteSpace(profile.ContainerExtension), "mp4", profile.ContainerExtension.Trim().TrimStart("."c))
    End Function

    Private Function ResolveInputUrls(sourceValue As String) As IReadOnlyList(Of String)
        If Not RequiresYtDlpResolution(sourceValue) Then
            Return {sourceValue}
        End If

        Dim ytDlpPath = Path.Combine(AppContext.BaseDirectory, "yt-dlp.exe")

        If Not File.Exists(ytDlpPath) Then
            Throw New FileNotFoundException($"yt-dlp.exe was not found in {AppContext.BaseDirectory}. Copy yt-dlp.exe there to use page URLs like YouTube or Facebook.")
        End If

        AppendLog($"Resolving {GetResolvableSourceName(sourceValue)} media URL with yt-dlp...")

        Dim startInfo As New ProcessStartInfo() With {
            .FileName = ytDlpPath,
            .Arguments = $"-g --no-playlist -f ""bv*+ba/b"" {Quote(sourceValue)}",
            .WorkingDirectory = AppContext.BaseDirectory,
            .UseShellExecute = False,
            .RedirectStandardOutput = True,
            .RedirectStandardError = True,
            .CreateNoWindow = True
        }

        Using process As New Process() With {.StartInfo = startInfo}
            Dim outputBuilder As New StringBuilder()
            Dim errorBuilder As New StringBuilder()

            AddHandler process.OutputDataReceived,
                Sub(sender, e)
                    If Not String.IsNullOrWhiteSpace(e.Data) Then
                        outputBuilder.AppendLine(e.Data.Trim())
                    End If
                End Sub

            AddHandler process.ErrorDataReceived,
                Sub(sender, e)
                    If Not String.IsNullOrWhiteSpace(e.Data) Then
                        errorBuilder.AppendLine(e.Data.Trim())
                    End If
                End Sub

            If Not process.Start() Then
                Throw New InvalidOperationException("yt-dlp could not be started.")
            End If

            process.BeginOutputReadLine()
            process.BeginErrorReadLine()

            If Not process.WaitForExit(30000) Then
                Try
                    process.Kill(True)
                Catch
                End Try

                Throw New TimeoutException("yt-dlp did not finish within 30 seconds.")
            End If

            process.WaitForExit()

            If process.ExitCode <> 0 Then
                Throw New InvalidOperationException($"yt-dlp failed: {errorBuilder.ToString().Trim()}")
            End If

            Dim resolvedUrls = outputBuilder.ToString().
                Split({ControlChars.Cr, ControlChars.Lf}, StringSplitOptions.RemoveEmptyEntries).
                Select(Function(line) line.Trim()).
                Where(Function(line) Not String.IsNullOrWhiteSpace(line)).
                Take(2).
                ToArray()

            If resolvedUrls.Length = 0 Then
                Throw New InvalidOperationException("yt-dlp did not return a media URL.")
            End If

            AppendLog($"Resolved {resolvedUrls.Length} media input(s).")
            Return resolvedUrls
        End Using
    End Function

    Private Shared Function RequiresYtDlpResolution(sourceValue As String) As Boolean
        If String.IsNullOrWhiteSpace(sourceValue) Then
            Return False
        End If

        Return IsYouTubeUrl(sourceValue) OrElse IsFacebookUrl(sourceValue)
    End Function

    Private Shared Function GetResolvableSourceName(sourceValue As String) As String
        If IsYouTubeUrl(sourceValue) Then
            Return "YouTube"
        End If

        If IsFacebookUrl(sourceValue) Then
            Return "Facebook"
        End If

        Return "stream"
    End Function

    Private Shared Function IsYouTubeUrl(sourceValue As String) As Boolean
        If String.IsNullOrWhiteSpace(sourceValue) Then
            Return False
        End If

        Return sourceValue.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) OrElse
            sourceValue.Contains("youtu.be", StringComparison.OrdinalIgnoreCase)
    End Function

    Private Shared Function IsFacebookUrl(sourceValue As String) As Boolean
        If String.IsNullOrWhiteSpace(sourceValue) Then
            Return False
        End If

        Return sourceValue.Contains("facebook.com", StringComparison.OrdinalIgnoreCase) OrElse
            sourceValue.Contains("fb.watch", StringComparison.OrdinalIgnoreCase)
    End Function

    Private Async Sub StopRecording(sender As Object, e As EventArgs)
        If streamRunner Is Nothing OrElse isStoppingRecordingValue Then
            Return
        End If

        Dim runner = streamRunner
        isStoppingRecordingValue = True
        stopButton.Enabled = False
        continueDirectFfmbcRecordingValue = False
        statusValueLabel.Text = "Stopping"
        statusValueLabel.ForeColor = Color.DarkOrange
        AppendLog("Stopping stream recording...")
        UpdateUiState(True)
        UpdateStatusAccent()

        Try
            Await Task.Run(Sub() runner.Stop())
        Catch ex As Exception
            AppendLog($"Stop request failed: {ex.Message}")
        Finally
            isStoppingRecordingValue = False
            UpdateUiState(streamRunner IsNot Nothing)
            UpdateStatusAccent()
        End Try
    End Sub

    Private Async Sub StartPreview(sender As Object, e As EventArgs)
        If previewRunner IsNot Nothing OrElse streamRunner IsNot Nothing OrElse isStartingPreviewValue OrElse isStoppingPreviewValue OrElse isStartingRecordingValue OrElse isStoppingRecordingValue Then
            Return
        End If

        Dim sourceValue = urlTextBox.Text.Trim()

        If String.IsNullOrWhiteSpace(sourceValue) Then
            AppendLog("Enter a URL or file path before preview.")
            Return
        End If

        Dim ffmpegPath = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe")

        If Not File.Exists(ffmpegPath) Then
            AppendLog($"ffmpeg.exe was not found in {AppContext.BaseDirectory}.")
            Return
        End If

        isStartingPreviewValue = True
        statusValueLabel.Text = "Starting Preview"
        statusValueLabel.ForeColor = Color.DarkOrange
        previewStateLabel.Text = "Starting preview..."
        previewStateLabel.ForeColor = Color.DarkOrange
        previewStateLabel.Visible = True
        UpdateUiState(False)
        UpdateStatusAccent()

        Try
            Directory.CreateDirectory(OutputFolderPath)
            Dim inputUrls = Await Task.Run(Function() ResolveInputUrls(sourceValue))

            If IsDisposed Then
                Return
            End If

            previewRunner = New PreviewFrameReader()
            previewRunner.Start(ffmpegPath, BuildPreviewArguments(inputUrls), OutputFolderPath)
            StartPreviewAudio(inputUrls)
            previewStateLabel.Visible = False
            statusValueLabel.Text = "Preview"
            statusValueLabel.ForeColor = Color.DarkGreen
        Catch ex As Exception
            AppendLog($"Failed to start stream preview: {ex.Message}")
            TearDownPreview()
            statusValueLabel.Text = "Idle"
            statusValueLabel.ForeColor = Color.DarkGreen
        Finally
            isStartingPreviewValue = False
            UpdateUiState(False)
            UpdateStatusAccent()
        End Try
    End Sub

    Private Async Sub StopPreview(sender As Object, e As EventArgs)
        If previewRunner Is Nothing OrElse isStoppingPreviewValue Then
            Return
        End If

        Dim runner = previewRunner
        isStoppingPreviewValue = True
        statusValueLabel.Text = "Stopping Preview"
        statusValueLabel.ForeColor = Color.DarkOrange
        previewStateLabel.Text = "Stopping preview..."
        previewStateLabel.ForeColor = Color.DarkOrange
        previewStateLabel.Visible = True
        UpdateUiState(False)
        UpdateStatusAccent()

        Try
            Await Task.Run(Sub() runner.Stop())
        Catch ex As Exception
            AppendLog($"Preview stop failed: {ex.Message}")
        Finally
            isStoppingPreviewValue = False
        End Try

        TearDownPreview()
        statusValueLabel.Text = "Idle"
        statusValueLabel.ForeColor = Color.DarkGreen
        previewStateLabel.Text = "Preview stopped"
        previewStateLabel.Visible = True
        UpdateUiState(False)
        UpdateStatusAccent()
    End Sub

    Private Sub StartPreviewAudio(inputUrls As IReadOnlyList(Of String))
        TearDownPreviewAudio()

        Dim ffplayPath = ResolveFfplayPath()

        If String.IsNullOrWhiteSpace(ffplayPath) Then
            AppendLog($"ffplay.exe was not found in {AppContext.BaseDirectory}. Stream preview audio is unavailable.")
            Return
        End If

        Dim audioInputUrl = If(inputUrls.Count >= 2, inputUrls(1), inputUrls(0))
        Dim arguments = $"-hide_banner -loglevel warning -nodisp -fflags nobuffer -flags low_delay -sync ext -i {Quote(audioInputUrl)} -af {Quote($"adelay={PreviewAudioDelayMilliseconds}:all=1")}"

        Try
            previewAudioRunner = New FfmpegProcessRunner()
            previewAudioRunner.Start(ffplayPath, arguments, OutputFolderPath)
            AppendLog("Preview audio started.")
        Catch ex As Exception
            TearDownPreviewAudio()
            AppendLog($"Preview audio failed: {ex.Message}")
        End Try
    End Sub

    Private Function ResolveFfplayPath() As String
        Dim ffplayPath = Path.Combine(AppContext.BaseDirectory, "ffplay.exe")
        Return If(File.Exists(ffplayPath), ffplayPath, Nothing)
    End Function

    Private Sub UpdateUiState(isRecording As Boolean)
        Dim isRecordingBusy = isRecording OrElse isStartingRecordingValue OrElse isStoppingRecordingValue
        Dim isPreviewTransitioning = isStartingPreviewValue OrElse isStoppingPreviewValue
        Dim hasPendingOperation = isRecordingBusy OrElse isPreviewTransitioning

        recordButton.Enabled = Not hasPendingOperation
        stopButton.Enabled = isRecording AndAlso Not isStartingRecordingValue AndAlso Not isStoppingRecordingValue
        urlTextBox.Enabled = Not hasPendingOperation
        intervalUpDown.Enabled = Not hasPendingOperation
        profileComboBox.Enabled = Not hasPendingOperation
        previewButton.Enabled = Not hasPendingOperation AndAlso previewRunner Is Nothing
        stopPreviewButton.Enabled = Not hasPendingOperation AndAlso previewRunner IsNot Nothing
    End Sub

    Private Sub OnElapsedTimerTick(sender As Object, e As EventArgs)
        UpdateElapsedDisplay()
    End Sub

    Private Sub UpdateElapsedDisplay()
        If Not recordingStartedAtUtc.HasValue Then
            elapsedLabel.Visible = False
            Return
        End If

        Dim elapsed = DateTime.UtcNow - recordingStartedAtUtc.Value
        elapsedLabel.Text = $"REC {CInt(Math.Floor(elapsed.TotalHours)):00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
        elapsedLabel.Visible = True
    End Sub

    Private Sub UpdateStatusAccent()
        If streamRunner IsNot Nothing Then
            statusStrip.BackColor = Color.FromArgb(220, 53, 69)
        ElseIf isFinalizingRecordingValue Then
            statusStrip.BackColor = Color.FromArgb(245, 159, 0)
        ElseIf previewRunner IsNot Nothing Then
            statusStrip.BackColor = Color.FromArgb(47, 158, 68)
        ElseIf String.Equals(statusValueLabel.Text, "Idle", StringComparison.OrdinalIgnoreCase) OrElse
            String.Equals(statusValueLabel.Text, "Ready", StringComparison.OrdinalIgnoreCase) Then
            statusStrip.BackColor = Color.FromArgb(47, 158, 68)
        Else
            statusStrip.BackColor = Color.FromArgb(245, 159, 0)
        End If
    End Sub

    Private Sub AppendLog(message As String)
        If InvokeRequired Then
            BeginInvoke(New Action(Of String)(AddressOf AppendLog), message)
            Return
        End If

        logTextBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}")
    End Sub

    Private Sub streamRunner_LogReceived(message As String) Handles streamRunner.LogReceived
        AppendLog(message)
    End Sub

    Private Sub streamRunner_Exited(exitCode As Integer) Handles streamRunner.Exited
        If InvokeRequired Then
            BeginInvoke(New Action(Of Integer)(AddressOf streamRunner_Exited), exitCode)
            Return
        End If

        Dim tempOutputFolder = currentRecordingTempOutputFolder
        Dim finalOutputFolder = currentRecordingFinalOutputFolder
        Dim shouldFinalizeWithFfmbc = currentRecordingUsesFfmbcFallbackValue AndAlso (exitCode = 0 OrElse HasAnyFfmbcTempFiles(tempOutputFolder))
        StopFfmbcFinalizeTimer()
        TearDownRunner()
        AppendLog($"Stream recording exited with code {exitCode}.")

        If currentRecordingUsesDirectFfmbcValue AndAlso continueDirectFfmbcRecordingValue AndAlso exitCode = 0 Then
            Try
                Dim ffmbcPath = ResolveFfmbcPath()

                If String.IsNullOrWhiteSpace(ffmbcPath) OrElse Not File.Exists(ffmbcPath) Then
                    Throw New InvalidOperationException($"ffmbc.exe was not found in {AppContext.BaseDirectory}.")
                End If

                streamRunner = New FfmpegProcessRunner()
                streamRunner.Start(ffmbcPath, BuildDirectFfmbcRecordingArguments(), OutputFolderPath)
                statusValueLabel.Text = "Recording"
                statusValueLabel.ForeColor = Color.Firebrick
                UpdateUiState(True)
                UpdateStatusAccent()
                If HasPendingFfmbcFinalizeWork() Then
                    StartFfmbcFinalizeTimer()
                End If
                Return
            Catch ex As Exception
                AppendLog($"Failed to start next Sony-compatible stream segment: {ex.Message}")
            End Try
        End If

        currentRecordingUsesFfmbcFallbackValue = False
        currentRecordingUsesDirectFfmbcValue = False
        continueDirectFfmbcRecordingValue = False
        currentDirectFfmbcInputUrls = Nothing
        currentDirectFfmbcProfile = Nothing
        currentDirectFfmbcAudioSource = Nothing
        currentDirectFfmbcSilenceFilePath = Nothing
        currentRecordingTempOutputFolder = Nothing
        currentRecordingFinalOutputFolder = Nothing
        recordingStartedAtUtc = Nothing
        elapsedTimer.Stop()
        UpdateElapsedDisplay()

        If shouldFinalizeWithFfmbc Then
            AddPendingFfmbcFinalizeSession(tempOutputFolder, finalOutputFolder)
            If exitCode <> 0 Then
                AppendLog("Recording stopped with a non-zero exit code, but valid Sony-compatible temp clips were found. Finalizing them with FFmbc...")
            Else
                AppendLog("Finalizing Sony-compatible stream clips with FFmbc...")
            End If
            StartFfmbcFinalizeTimer()
            StartBackgroundFfmbcFinalization()
            UpdateFinalizeStatus(tempOutputFolder)
        ElseIf HasPendingFfmbcFinalizeWork() Then
            StartFfmbcFinalizeTimer()
            StartBackgroundFfmbcFinalization()
            UpdateFinalizeStatus(tempOutputFolder)
        Else
            statusValueLabel.Text = If(exitCode = 0, "Idle", $"Stopped (Exit {exitCode})")
            statusValueLabel.ForeColor = If(exitCode = 0, Color.DarkGreen, Color.DarkOrange)
        End If

        UpdateUiState(False)
        UpdateStatusAccent()
    End Sub

    Private Sub previewRunner_FrameReady(frame As Bitmap) Handles previewRunner.FrameReady
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
        AppendLog($"Preview: {message}")
    End Sub

    Private Sub previewAudioRunner_LogReceived(message As String) Handles previewAudioRunner.LogReceived
        AppendLog($"Preview audio: {message}")
    End Sub

    Private Sub previewAudioRunner_Exited(exitCode As Integer) Handles previewAudioRunner.Exited
        If InvokeRequired Then
            BeginInvoke(New Action(Of Integer)(AddressOf previewAudioRunner_Exited), exitCode)
            Return
        End If

        TearDownPreviewAudio()
        AppendLog($"Preview audio exited with code {exitCode}.")
    End Sub

    Private Sub previewRunner_Exited(exitCode As Integer) Handles previewRunner.Exited
        If InvokeRequired Then
            BeginInvoke(New Action(Of Integer)(AddressOf previewRunner_Exited), exitCode)
            Return
        End If

        TearDownPreview()
        statusValueLabel.Text = If(exitCode = 0, "Idle", $"Preview Exit {exitCode}")
        statusValueLabel.ForeColor = If(exitCode = 0, Color.DarkGreen, Color.DarkOrange)
        previewStateLabel.Text = If(exitCode = 0, "Preview stopped", $"Preview stopped (Exit {exitCode})")
        previewStateLabel.Visible = True
        UpdateUiState(False)
        UpdateStatusAccent()
    End Sub

    Private Sub TearDownRunner()
        If streamRunner Is Nothing Then
            Return
        End If

        Dim runner = streamRunner
        streamRunner = Nothing
        runner.Dispose()
    End Sub

    Private Sub TearDownPreview()
        TearDownPreviewAudio()

        If previewRunner Is Nothing Then
            Return
        End If

        Dim runner = previewRunner
        previewRunner = Nothing
        runner.Dispose()
    End Sub

    Private Sub TearDownPreviewAudio()
        If previewAudioRunner Is Nothing Then
            Return
        End If

        Dim runner = previewAudioRunner
        previewAudioRunner = Nothing
        runner.Dispose()
    End Sub

    Protected Overrides Sub Dispose(disposing As Boolean)
        If disposing Then
            elapsedTimer.Stop()
            elapsedTimer.Dispose()
            ffmbcFinalizeTimer.Stop()
            ffmbcFinalizeTimer.Dispose()
            TearDownPreview()
            TearDownRunner()

            If previewPictureBox.Image IsNot Nothing Then
                previewPictureBox.Image.Dispose()
                previewPictureBox.Image = Nothing
            End If
        End If

        MyBase.Dispose(disposing)
    End Sub

    Private Shared Function Quote(value As String) As String
        Dim safeValue = If(value, String.Empty).Replace("""", String.Empty)
        Return $"""{safeValue}"""
    End Function
End Class
