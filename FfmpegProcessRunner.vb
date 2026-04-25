Imports System.Diagnostics

Friend Class FfmpegProcessRunner
    Implements IDisposable

    Private currentProcess As Process
    Private ReadOnly syncRoot As New Object()

    Public Event LogReceived(message As String)
    Public Event Exited(exitCode As Integer)

    Public Sub Start(executablePath As String, arguments As String, workingDirectory As String)
        SyncLock syncRoot
            If currentProcess IsNot Nothing Then
                Throw New InvalidOperationException("Recording is already running.")
            End If
        End SyncLock

        Dim startInfo As New ProcessStartInfo() With {
            .FileName = executablePath,
            .Arguments = arguments,
            .WorkingDirectory = workingDirectory,
            .UseShellExecute = False,
            .RedirectStandardInput = True,
            .RedirectStandardOutput = True,
            .RedirectStandardError = True,
            .CreateNoWindow = True
        }

        Dim process As New Process() With {
            .StartInfo = startInfo,
            .EnableRaisingEvents = True
        }

        AddHandler process.OutputDataReceived, AddressOf OnDataReceived
        AddHandler process.ErrorDataReceived, AddressOf OnDataReceived
        AddHandler process.Exited, AddressOf OnExited

        If Not process.Start() Then
            process.Dispose()
            Throw New InvalidOperationException("FFmpeg could not be started.")
        End If

        SyncLock syncRoot
            currentProcess = process
        End SyncLock

        RaiseEvent LogReceived($"Started: {executablePath} {arguments}")
        process.BeginOutputReadLine()
        process.BeginErrorReadLine()
    End Sub

    Public Sub [Stop]()
        Dim process As Process = Nothing

        SyncLock syncRoot
            process = currentProcess
        End SyncLock

        If process Is Nothing Then
            Return
        End If

        Try
            If Not process.HasExited Then
                process.StandardInput.WriteLine("q")

                If Not process.WaitForExit(3000) Then
                    RaiseEvent LogReceived("FFmpeg did not exit after 'q'. Killing the process tree.")
                    process.Kill(True)
                End If
            End If
        Catch ex As Exception
            RaiseEvent LogReceived($"Stop request failed: {ex.Message}")

            Try
                If process IsNot Nothing AndAlso Not process.HasExited Then
                    process.Kill(True)
                End If
            Catch
            End Try
        End Try
    End Sub

    Private Sub OnDataReceived(sender As Object, e As DataReceivedEventArgs)
        If Not String.IsNullOrWhiteSpace(e.Data) Then
            RaiseEvent LogReceived(e.Data)
        End If
    End Sub

    Private Sub OnExited(sender As Object, e As EventArgs)
        Dim exitedProcess = DirectCast(sender, Process)
        Dim exitCode = exitedProcess.ExitCode

        Try
            RemoveHandler exitedProcess.OutputDataReceived, AddressOf OnDataReceived
            RemoveHandler exitedProcess.ErrorDataReceived, AddressOf OnDataReceived
            RemoveHandler exitedProcess.Exited, AddressOf OnExited
        Catch
        End Try

        SyncLock syncRoot
            If Object.ReferenceEquals(currentProcess, exitedProcess) Then
                currentProcess = Nothing
            End If
        End SyncLock

        Try
            exitedProcess.Dispose()
        Catch
        End Try

        RaiseEvent Exited(exitCode)
    End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        Dim process As Process = Nothing

        SyncLock syncRoot
            process = currentProcess
            currentProcess = Nothing
        End SyncLock

        If process Is Nothing Then
            Return
        End If

        Try
            If Not process.HasExited Then
                process.Kill(True)
            End If
        Catch
        End Try

        Try
            process.Dispose()
        Catch
        End Try
    End Sub
End Class
