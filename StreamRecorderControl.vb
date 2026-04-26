Imports System.ComponentModel
Imports System.Diagnostics
Imports System.IO
Imports System.Text

Public Class StreamRecorderControl
    Inherits UserControl

    Private Const PreviewAudioDelayMilliseconds As Integer = 700

    Private NotInheritable Class StreamRecordingProfile
        Public Sub New(displayName As String, containerExtension As String, outputOptions As String)
            Me.DisplayName = displayName
            Me.ContainerExtension = containerExtension
            Me.OutputOptions = outputOptions
        End Sub

        Public ReadOnly Property DisplayName As String
        Public ReadOnly Property ContainerExtension As String
        Public ReadOnly Property OutputOptions As String

        Public Overrides Function ToString() As String
            Return DisplayName
        End Function
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

    Private WithEvents streamRunner As FfmpegProcessRunner
    Private WithEvents previewRunner As PreviewFrameReader
    Private WithEvents previewAudioRunner As FfmpegProcessRunner
    Private recordingStartedAtUtc As DateTime?
    Private darkModeEnabledValue As Boolean
    Private suppressSettingsSave As Boolean

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

    Private ReadOnly Property OutputFolderPath As String
        Get
            Return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "FFmpegRecorder")
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
        profileComboBox.Margin = New Padding(0)
        profileComboBox.Items.AddRange(New Object() {
            New StreamRecordingProfile("XDCAM HD422", ".mxf", "-c:v mpeg2video -pix_fmt yuv422p -b:v 50000k -minrate 50000k -maxrate 50000k -bufsize 17825792 -rc_init_occupancy 17825792 -g 12 -bf 2 -flags +ildct+ilme -top 1 -qmin 1 -qmax 12 -dc 10 -intra_vlc 1 -color_primaries bt709 -color_trc bt709 -colorspace bt709 -c:a pcm_s16le -ar 48000 -ac 2"),
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

    Private Sub StartRecording(sender As Object, e As EventArgs)
        If streamRunner IsNot Nothing Then
            Return
        End If

        Dim sourceValue = urlTextBox.Text.Trim()

        If String.IsNullOrWhiteSpace(sourceValue) Then
            AppendLog("Enter a URL or file path before recording.")
            Return
        End If

        Dim ffmpegPath = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe")

        If Not File.Exists(ffmpegPath) Then
            AppendLog($"ffmpeg.exe was not found in {AppContext.BaseDirectory}.")
            Return
        End If

        Directory.CreateDirectory(OutputFolderPath)
        logTextBox.Clear()

        Try
            Dim inputUrls = ResolveInputUrls(sourceValue)
            Dim selectedProfile = GetSelectedProfile()
            Dim outputPattern = Path.Combine(OutputFolderPath, $"Stream_%d%m%Y_%H%M%S{selectedProfile.ContainerExtension}")
            Dim arguments = BuildRecordingArguments(inputUrls, outputPattern)

            streamRunner = New FfmpegProcessRunner()
            streamRunner.Start(ffmpegPath, arguments, OutputFolderPath)
            recordingStartedAtUtc = DateTime.UtcNow
            elapsedTimer.Start()
            statusValueLabel.Text = "Recording"
            statusValueLabel.ForeColor = Color.Firebrick
            UpdateElapsedDisplay()
            UpdateUiState(True)
            UpdateStatusAccent()
        Catch ex As Exception
            AppendLog($"Failed to start stream recording: {ex.Message}")
            TearDownRunner()
            recordingStartedAtUtc = Nothing
            elapsedTimer.Stop()
            UpdateUiState(False)
            statusValueLabel.Text = "Idle"
            statusValueLabel.ForeColor = Color.DarkGreen
            UpdateStatusAccent()
        End Try
    End Sub

    Private Function BuildRecordingArguments(inputUrls As IReadOnlyList(Of String), outputPattern As String) As String
        Dim selectedProfile = GetSelectedProfile()
        Dim builder As New StringBuilder()
        builder.Append("-hide_banner -y ")

        For Each inputUrl In inputUrls
            builder.Append("-re -i ").Append(Quote(inputUrl)).Append(" ")
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
            builder.Append("-re -i ").Append(Quote(inputUrl)).Append(" ")
        Next

        builder.Append("-filter_complex ").Append(Quote(BuildPreviewFilterGraph(inputUrls))).Append(" ")
        builder.Append("-map ").Append(Quote("[out]")).Append(" ")
        builder.Append("-an -flush_packets 1 -c:v mjpeg -q:v 6 -f mjpeg pipe:1")
        Return builder.ToString()
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

    Private Shared Function GetSegmentFormat(profile As StreamRecordingProfile) As String
        Return If(String.IsNullOrWhiteSpace(profile.ContainerExtension), "mp4", profile.ContainerExtension.Trim().TrimStart("."c))
    End Function

    Private Function ResolveInputUrls(sourceValue As String) As IReadOnlyList(Of String)
        If Not IsYouTubeUrl(sourceValue) Then
            Return {sourceValue}
        End If

        Dim ytDlpPath = Path.Combine(AppContext.BaseDirectory, "yt-dlp.exe")

        If Not File.Exists(ytDlpPath) Then
            Throw New FileNotFoundException($"yt-dlp.exe was not found in {AppContext.BaseDirectory}. Copy yt-dlp.exe there to record YouTube URLs.")
        End If

        AppendLog("Resolving YouTube media URL with yt-dlp...")

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

            AppendLog($"Resolved {resolvedUrls.Length} YouTube media input(s).")
            Return resolvedUrls
        End Using
    End Function

    Private Shared Function IsYouTubeUrl(sourceValue As String) As Boolean
        If String.IsNullOrWhiteSpace(sourceValue) Then
            Return False
        End If

        Return sourceValue.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) OrElse
            sourceValue.Contains("youtu.be", StringComparison.OrdinalIgnoreCase)
    End Function

    Private Sub StopRecording(sender As Object, e As EventArgs)
        If streamRunner Is Nothing Then
            Return
        End If

        stopButton.Enabled = False
        AppendLog("Stopping stream recording...")
        streamRunner.Stop()
    End Sub

    Private Sub StartPreview(sender As Object, e As EventArgs)
        If previewRunner IsNot Nothing OrElse streamRunner IsNot Nothing Then
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

        Try
            Directory.CreateDirectory(OutputFolderPath)
            Dim inputUrls = ResolveInputUrls(sourceValue)
            previewRunner = New PreviewFrameReader()
            previewRunner.Start(ffmpegPath, BuildPreviewArguments(inputUrls), OutputFolderPath)
            StartPreviewAudio(inputUrls)
            previewStateLabel.Visible = False
            statusValueLabel.Text = "Preview"
            statusValueLabel.ForeColor = Color.DarkGreen
            UpdateUiState(False)
            UpdateStatusAccent()
        Catch ex As Exception
            AppendLog($"Failed to start stream preview: {ex.Message}")
            TearDownPreview()
            statusValueLabel.Text = "Idle"
            statusValueLabel.ForeColor = Color.DarkGreen
            UpdateUiState(False)
            UpdateStatusAccent()
        End Try
    End Sub

    Private Sub StopPreview(sender As Object, e As EventArgs)
        If previewRunner Is Nothing Then
            Return
        End If

        previewRunner.Stop()
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
        recordButton.Enabled = Not isRecording
        stopButton.Enabled = isRecording
        urlTextBox.Enabled = Not isRecording
        intervalUpDown.Enabled = Not isRecording
        profileComboBox.Enabled = Not isRecording
        previewButton.Enabled = Not isRecording AndAlso previewRunner Is Nothing
        stopPreviewButton.Enabled = Not isRecording AndAlso previewRunner IsNot Nothing
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
        ElseIf previewRunner IsNot Nothing Then
            statusStrip.BackColor = Color.FromArgb(47, 158, 68)
        ElseIf String.Equals(statusValueLabel.Text, "Idle", StringComparison.OrdinalIgnoreCase) Then
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

        TearDownRunner()
        recordingStartedAtUtc = Nothing
        elapsedTimer.Stop()
        UpdateElapsedDisplay()
        statusValueLabel.Text = If(exitCode = 0, "Idle", $"Stopped (Exit {exitCode})")
        statusValueLabel.ForeColor = If(exitCode = 0, Color.DarkGreen, Color.DarkOrange)
        AppendLog($"Stream recording exited with code {exitCode}.")
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
