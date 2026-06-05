Imports System.Collections.Generic
Imports System.Diagnostics
Imports System.IO

Friend Module Program

    <STAThread()>
    Friend Sub Main(args As String())
        StopBundledHelperProcesses()
        AddHandler Application.ApplicationExit, Sub() StopBundledHelperProcesses()
        AddHandler AppDomain.CurrentDomain.ProcessExit, Sub() StopBundledHelperProcesses()
        Application.SetHighDpiMode(HighDpiMode.SystemAware)
        Application.EnableVisualStyles()
        Application.SetCompatibleTextRenderingDefault(False)

        Try
            Application.Run(New RecorderHostForm())
        Finally
            StopBundledHelperProcesses()
        End Try
    End Sub

    Friend Sub StopBundledHelperProcesses()
        Dim appDirectory = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        Dim helperNames = New HashSet(Of String)(StringComparer.OrdinalIgnoreCase) From {
            "DeckLinkOutputHelper",
            "ffmpeg",
            "ffplay",
            "ffprobe",
            "yt-dlp"
        }

        For Each runningProcess In Process.GetProcesses()
            Try
                Dim processName = runningProcess.ProcessName

                If Not helperNames.Contains(processName) AndAlso
                    Not processName.StartsWith("ffmbc", StringComparison.OrdinalIgnoreCase) AndAlso
                    Not processName.StartsWith("decklinkplayer", StringComparison.OrdinalIgnoreCase) Then
                    Continue For
                End If

                Dim modulePath = runningProcess.MainModule?.FileName

                If String.IsNullOrWhiteSpace(modulePath) Then
                    Continue For
                End If

                Dim moduleDirectory = Path.GetDirectoryName(Path.GetFullPath(modulePath))?.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)

                If String.Equals(moduleDirectory, appDirectory, StringComparison.OrdinalIgnoreCase) AndAlso Not runningProcess.HasExited Then
                    runningProcess.Kill(entireProcessTree:=True)
                End If
            Catch
            Finally
                runningProcess.Dispose()
            End Try
        Next
    End Sub

End Module
