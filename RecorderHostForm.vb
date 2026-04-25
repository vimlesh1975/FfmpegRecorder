Imports System.Diagnostics
Imports System.Drawing
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
    Private hasSystemCpuSample As Boolean
    Private lastSystemCpuSample As SystemCpuSample
    Private suppressSharedOperatorEvents As Boolean

    Public Sub New()
        InitializeComponent()
        ApplyVisualTheme()

        AddHandler leftRecorderControl.CpuUsageChanged, AddressOf OnRecorderCpuUsageChanged
        AddHandler rightRecorderControl.CpuUsageChanged, AddressOf OnRecorderCpuUsageChanged
        AddHandler thirdRecorderControl.CpuUsageChanged, AddressOf OnRecorderCpuUsageChanged
        AddHandler fourthRecorderControl.CpuUsageChanged, AddressOf OnRecorderCpuUsageChanged
        AddHandler systemCpuTimer.Tick, AddressOf OnSystemCpuTimerTick
        AddHandler audioListenComboBox.SelectedIndexChanged, AddressOf OnAudioListenSelectionChanged
        AddHandler profileComboBox.SelectedIndexChanged, AddressOf OnSharedProfileChanged
        AddHandler intervalUpDown.ValueChanged, AddressOf OnSharedIntervalChanged
        AddHandler recordAllButton.Click, AddressOf OnRecordAllClicked
        AddHandler stopAllButton.Click, AddressOf OnStopAllClicked
        AddHandler openRecordingsButton.Click, AddressOf OnOpenRecordingsClicked
        AddHandler deleteAllButton.Click, AddressOf OnDeleteAllClicked
        AddHandler Load, AddressOf RecorderHostForm_Load

        profileComboBox.Items.Clear()
        profileComboBox.Items.AddRange(leftRecorderControl.AvailableProfileNames.ToArray())

        audioListenComboBox.Items.AddRange(New Object() {"Off", "CAM1", "CAM2", "CAM3", "CAM4"})
        audioListenComboBox.SelectedItem = "CAM1"

        ReadSystemCpuSample(lastSystemCpuSample)
        hasSystemCpuSample = True
        systemCpuTimer.Start()
        ApplyAudioListenSelection()
        UpdateCpuLabels()
    End Sub

    Private Sub ApplyVisualTheme()
        Dim appBackground = Color.FromArgb(232, 236, 238)
        Dim commonBackground = Color.FromArgb(236, 229, 214)
        Dim cam1Background = Color.FromArgb(232, 240, 248)
        Dim cam2Background = Color.FromArgb(246, 238, 224)
        Dim cam3Background = Color.FromArgb(232, 242, 233)
        Dim cam4Background = Color.FromArgb(245, 232, 235)

        BackColor = appBackground
        mainLayout.BackColor = appBackground
        cameraGrid.BackColor = appBackground

        commonGroupBox.BackColor = commonBackground
        commonGroupBox.ForeColor = Color.FromArgb(36, 53, 69)
        commonPanel.BackColor = commonBackground

        StyleCameraSection(cam1GroupBox, leftRecorderControl, cam1Background)
        StyleCameraSection(cam2GroupBox, rightRecorderControl, cam2Background)
        StyleCameraSection(cam3GroupBox, thirdRecorderControl, cam3Background)
        StyleCameraSection(cam4GroupBox, fourthRecorderControl, cam4Background)
    End Sub

    Private Sub StyleCameraSection(groupBox As GroupBox, recorderControl As RecorderControl, baseColor As Color)
        groupBox.BackColor = baseColor
        groupBox.ForeColor = Color.FromArgb(52, 60, 68)
        recorderControl.BackColor = LightenColor(baseColor, 10)
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
    End Sub

    Private Sub OnSystemCpuTimerTick(sender As Object, e As EventArgs)
        UpdateSystemCpuLabel()
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
            recorderControl.StartRecordingRequested()
        Next
    End Sub

    Private Sub OnStopAllClicked(sender As Object, e As EventArgs)
        For Each recorderControl In GetRecorderControls()
            recorderControl.StopRecordingRequested()
        Next
    End Sub

    Private Sub OnOpenRecordingsClicked(sender As Object, e As EventArgs)
        Dim outputFolderPath = leftRecorderControl.OutputFolderPath
        Directory.CreateDirectory(outputFolderPath)
        Process.Start(New ProcessStartInfo(outputFolderPath) With {.UseShellExecute = True})
    End Sub

    Private Sub OnDeleteAllClicked(sender As Object, e As EventArgs)
        Dim outputFolderPath = leftRecorderControl.OutputFolderPath

        If GetRecorderControls().Any(Function(recorderControl) recorderControl.IsRecording) Then
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
                totalCpuValueLabel.Text = $"{(busyTicks * 100.0R / totalTicks):0.0}%"
            End If
        End If

        lastSystemCpuSample = currentSample
        hasSystemCpuSample = True
    End Sub

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
        systemCpuTimer.Dispose()
    End Sub
End Class
