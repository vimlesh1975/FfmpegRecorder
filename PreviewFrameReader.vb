Imports System.Diagnostics
Imports System.Drawing
Imports System.IO
Imports System.Threading.Tasks

Friend Class PreviewFrameReader
    Implements IDisposable

    Private currentProcess As Process
    Private outputReadTask As Task

    Public Event FrameReady(frame As Bitmap)
    Public Event LogReceived(message As String)
    Public Event Exited(exitCode As Integer)

    Public Sub Start(executablePath As String, arguments As String, workingDirectory As String)
        If currentProcess IsNot Nothing Then
            Throw New InvalidOperationException("Preview is already running.")
        End If

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

        AddHandler process.ErrorDataReceived, AddressOf OnDataReceived
        AddHandler process.Exited, AddressOf OnExited

        If Not process.Start() Then
            process.Dispose()
            Throw New InvalidOperationException("Preview process could not be started.")
        End If

        currentProcess = process
        RaiseEvent LogReceived("Preview started.")
        process.BeginErrorReadLine()
        outputReadTask = Task.Run(Sub() ReadFrames(process.StandardOutput.BaseStream))
    End Sub

    Public Sub [Stop]()
        If currentProcess Is Nothing Then
            Return
        End If

        Try
            If Not currentProcess.HasExited Then
                currentProcess.StandardInput.WriteLine("q")

                If Not currentProcess.WaitForExit(2500) Then
                    currentProcess.Kill(True)
                End If
            End If

            If outputReadTask IsNot Nothing Then
                outputReadTask.Wait(1000)
            End If
        Catch ex As Exception
            RaiseEvent LogReceived($"Preview stop failed: {ex.Message}")

            Try
                If currentProcess IsNot Nothing AndAlso Not currentProcess.HasExited Then
                    currentProcess.Kill(True)
                End If
            Catch
            End Try
        End Try
    End Sub

    Private Sub ReadFrames(stream As Stream)
        Dim readBuffer(8191) As Byte
        Dim pendingBytes As New List(Of Byte)()

        Try
            Do
                Dim bytesRead = stream.Read(readBuffer, 0, readBuffer.Length)

                If bytesRead <= 0 Then
                    Exit Do
                End If

                For index = 0 To bytesRead - 1
                    pendingBytes.Add(readBuffer(index))
                Next

                EmitCompleteFrames(pendingBytes)
            Loop
        Catch ex As ObjectDisposedException
        Catch ex As IOException
            RaiseEvent LogReceived($"Preview stream closed: {ex.Message}")
        Catch ex As Exception
            RaiseEvent LogReceived($"Preview stream error: {ex.Message}")
        End Try
    End Sub

    Private Sub EmitCompleteFrames(pendingBytes As List(Of Byte))
        Dim startIndex = FindMarker(pendingBytes, &HFF, &HD8, 0)

        If startIndex > 0 Then
            pendingBytes.RemoveRange(0, startIndex)
        ElseIf startIndex < 0 Then
            pendingBytes.Clear()
            Return
        End If

        Do
            Dim endIndex = FindMarker(pendingBytes, &HFF, &HD9, 2)

            If endIndex < 0 Then
                Exit Do
            End If

            Dim frameBytes = pendingBytes.GetRange(0, endIndex + 2).ToArray()
            pendingBytes.RemoveRange(0, endIndex + 2)
            RaiseFrame(frameBytes)

            Dim nextStart = FindMarker(pendingBytes, &HFF, &HD8, 0)

            If nextStart > 0 Then
                pendingBytes.RemoveRange(0, nextStart)
            ElseIf nextStart < 0 Then
                pendingBytes.Clear()
                Exit Do
            End If
        Loop

        If pendingBytes.Count > 4 * 1024 * 1024 Then
            pendingBytes.Clear()
        End If
    End Sub

    Private Function FindMarker(buffer As List(Of Byte), firstByte As Byte, secondByte As Byte, startIndex As Integer) As Integer
        For index = startIndex To buffer.Count - 2
            If buffer(index) = firstByte AndAlso buffer(index + 1) = secondByte Then
                Return index
            End If
        Next

        Return -1
    End Function

    Private Sub RaiseFrame(frameBytes As Byte())
        Try
            Using memory As New MemoryStream(frameBytes)
                Using sourceImage As Image = Image.FromStream(memory)
                    RaiseEvent FrameReady(New Bitmap(sourceImage))
                End Using
            End Using
        Catch
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

        RemoveHandler exitedProcess.ErrorDataReceived, AddressOf OnDataReceived
        RemoveHandler exitedProcess.Exited, AddressOf OnExited

        exitedProcess.Dispose()
        currentProcess = Nothing

        RaiseEvent Exited(exitCode)
    End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        If currentProcess IsNot Nothing Then
            If Not currentProcess.HasExited Then
                currentProcess.Kill(True)
            End If

            currentProcess.Dispose()
            currentProcess = Nothing
        End If
    End Sub
End Class
