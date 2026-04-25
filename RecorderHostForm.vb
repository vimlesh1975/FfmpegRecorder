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

    Public Sub New()
        InitializeComponent()

        AddHandler leftRecorderControl.CpuUsageChanged, AddressOf OnRecorderCpuUsageChanged
        AddHandler rightRecorderControl.CpuUsageChanged, AddressOf OnRecorderCpuUsageChanged
        AddHandler thirdRecorderControl.CpuUsageChanged, AddressOf OnRecorderCpuUsageChanged
        AddHandler fourthRecorderControl.CpuUsageChanged, AddressOf OnRecorderCpuUsageChanged
        AddHandler systemCpuTimer.Tick, AddressOf OnSystemCpuTimerTick
        AddHandler audioListenComboBox.SelectedIndexChanged, AddressOf OnAudioListenSelectionChanged

        audioListenComboBox.Items.AddRange(New Object() {"Off", "CAM1", "CAM2", "CAM3", "CAM4"})
        audioListenComboBox.SelectedItem = "CAM1"

        ReadSystemCpuSample(lastSystemCpuSample)
        hasSystemCpuSample = True
        systemCpuTimer.Start()
        ApplyAudioListenSelection()
        UpdateCpuLabels()
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

    Private Sub ApplyAudioListenSelection()
        Dim selectedCameraName = If(TryCast(audioListenComboBox.SelectedItem, String), "Off")

        leftRecorderControl.SpeakerMonitorEnabled = String.Equals(selectedCameraName, "CAM1", StringComparison.OrdinalIgnoreCase)
        rightRecorderControl.SpeakerMonitorEnabled = String.Equals(selectedCameraName, "CAM2", StringComparison.OrdinalIgnoreCase)
        thirdRecorderControl.SpeakerMonitorEnabled = String.Equals(selectedCameraName, "CAM3", StringComparison.OrdinalIgnoreCase)
        fourthRecorderControl.SpeakerMonitorEnabled = String.Equals(selectedCameraName, "CAM4", StringComparison.OrdinalIgnoreCase)
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
