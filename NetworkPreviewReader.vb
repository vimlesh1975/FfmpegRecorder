Imports System.Drawing
Imports System.IO
Imports System.Net.Sockets
Imports System.Threading
Imports System.Threading.Tasks

Friend Class NetworkPreviewReader
    Implements IDisposable

    Private client As TcpClient
    Private readTask As Task
    Private cancellationSource As CancellationTokenSource

    Public Event FrameReady(frame As Bitmap)
    Public Event LogReceived(message As String)
    Public Event Exited()

    Public Sub Start(host As String, port As Integer, Optional connectTimeoutMs As Integer = 5000)
        If client IsNot Nothing Then
            Throw New InvalidOperationException("Preview network reader is already running.")
        End If

        cancellationSource = New CancellationTokenSource()
        client = ConnectWithRetry(host, port, connectTimeoutMs, cancellationSource.Token)
        RaiseEvent LogReceived($"Connected preview stream to {host}:{port}.")
        readTask = Task.Run(Sub() ReadFrames(client.GetStream(), cancellationSource.Token), cancellationSource.Token)
    End Sub

    Public Sub [Stop]()
        If cancellationSource IsNot Nothing Then
            cancellationSource.Cancel()
        End If

        If client IsNot Nothing Then
            Try
                client.Close()
            Catch
            End Try
        End If

        If readTask IsNot Nothing Then
            Try
                readTask.Wait(1000)
            Catch
            End Try
        End If
    End Sub

    Private Function ConnectWithRetry(host As String, port As Integer, connectTimeoutMs As Integer, cancellationToken As CancellationToken) As TcpClient
        Dim deadline = DateTime.UtcNow.AddMilliseconds(connectTimeoutMs)
        Dim lastException As Exception = Nothing

        Do
            cancellationToken.ThrowIfCancellationRequested()

            Dim attempt As New TcpClient()

            Try
                Dim connectTask = attempt.ConnectAsync(host, port)

                If connectTask.Wait(120, cancellationToken) AndAlso attempt.Connected Then
                    Return attempt
                End If

                attempt.Dispose()
            Catch ex As Exception
                lastException = ex
                attempt.Dispose()
            End Try

            Thread.Sleep(60)
        Loop While DateTime.UtcNow < deadline

        Throw New InvalidOperationException($"Unable to connect preview stream on {host}:{port}.", lastException)
    End Function

    Private Sub ReadFrames(stream As Stream, cancellationToken As CancellationToken)
        Dim readBuffer(8191) As Byte
        Dim pendingBytes As New List(Of Byte)()

        Try
            Do While Not cancellationToken.IsCancellationRequested
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
            If Not cancellationToken.IsCancellationRequested Then
                RaiseEvent LogReceived($"Preview stream error: {ex.Message}")
            End If
        Finally
            RaiseEvent Exited()
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

    Public Sub Dispose() Implements IDisposable.Dispose
        [Stop]()

        If client IsNot Nothing Then
            client.Dispose()
            client = Nothing
        End If

        If cancellationSource IsNot Nothing Then
            cancellationSource.Dispose()
            cancellationSource = Nothing
        End If
    End Sub
End Class
