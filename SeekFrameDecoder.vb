Imports System.Diagnostics
Imports System.Globalization
Imports System.IO
Imports System.Threading

Friend NotInheritable Class SeekFrameDecoder
    Private Sub New()
    End Sub

    Public Shared Function DecodeFrame(ffmpegPath As String, filePath As String, width As Integer, height As Integer, bytesPerPixel As Integer, pixelFormat As String, isInterlaced As Boolean, startOffset As TimeSpan, cancellationToken As CancellationToken) As SeekDecodedFrame
        If width <= 0 OrElse height <= 0 Then
            Throw New ArgumentOutOfRangeException(NameOf(width), "Seek frame size must be positive.")
        End If

        Dim frameBytes = Math.Max(1, width * height * bytesPerPixel)
        Dim buffer(frameBytes - 1) As Byte
        Dim process = StartDecoder(ffmpegPath, BuildDecoderArguments(filePath, width, height, pixelFormat, isInterlaced, startOffset))

        Try
            Using cancellationToken.Register(Sub() TryKillProcess(process))
                If Not ReadExact(process.StandardOutput.BaseStream, buffer, cancellationToken) Then
                    Dim errorText = process.StandardError.ReadToEnd()
                    Throw New InvalidOperationException(If(String.IsNullOrWhiteSpace(errorText), "FFmpeg did not decode a seek frame.", FirstLine(errorText)))
                End If

                cancellationToken.ThrowIfCancellationRequested()

                Dim waitStartedAt = DateTime.UtcNow
                While Not process.WaitForExit(25)
                    cancellationToken.ThrowIfCancellationRequested()

                    If DateTime.UtcNow - waitStartedAt >= TimeSpan.FromSeconds(5) Then
                        TryKillProcess(process)
                        Throw New TimeoutException("FFmpeg seek frame decode timed out.")
                    End If
                End While

                If process.ExitCode <> 0 Then
                    Dim errorText = process.StandardError.ReadToEnd()
                    Throw New InvalidOperationException(If(String.IsNullOrWhiteSpace(errorText), $"FFmpeg exited with code {process.ExitCode}.", FirstLine(errorText)))
                End If
            End Using
        Finally
            TryKillProcess(process)
            process.Dispose()
        End Try

        Return New SeekDecodedFrame(width, height, pixelFormat, buffer)
    End Function

    Public Shared Sub DecodeFrameBurst(ffmpegPath As String, filePath As String, width As Integer, height As Integer, bytesPerPixel As Integer, pixelFormat As String, isInterlaced As Boolean, startOffset As TimeSpan, frameCount As Integer, frameInterval As TimeSpan, frameReady As Action(Of SeekDecodedFrame), cancellationToken As CancellationToken, Optional keyframeOffset As TimeSpan? = Nothing)
        If width <= 0 OrElse height <= 0 Then
            Throw New ArgumentOutOfRangeException(NameOf(width), "Seek frame size must be positive.")
        End If

        If frameReady Is Nothing Then
            Throw New ArgumentNullException(NameOf(frameReady))
        End If

        Dim safeFrameCount = Math.Max(1, frameCount)
        Dim frameBytes = Math.Max(1, width * height * bytesPerPixel)
        Dim process = StartDecoder(ffmpegPath, BuildBurstDecoderArguments(filePath, width, height, pixelFormat, isInterlaced, startOffset, safeFrameCount, frameInterval, keyframeOffset))
        Dim decodedFrameCount = 0

        Try
            Using cancellationToken.Register(Sub() TryKillProcess(process))
                While decodedFrameCount < safeFrameCount
                    Dim buffer(frameBytes - 1) As Byte

                    If Not ReadExact(process.StandardOutput.BaseStream, buffer, cancellationToken) Then
                        Exit While
                    End If

                    cancellationToken.ThrowIfCancellationRequested()
                    decodedFrameCount += 1
                    frameReady(New SeekDecodedFrame(width, height, pixelFormat, buffer))
                End While

                If decodedFrameCount = 0 Then
                    Dim errorText = process.StandardError.ReadToEnd()
                    Throw New InvalidOperationException(If(String.IsNullOrWhiteSpace(errorText), "FFmpeg did not decode any seek preview frames.", FirstLine(errorText)))
                End If

                If decodedFrameCount >= safeFrameCount Then
                    TryKillProcess(process)
                End If

                Dim waitStartedAt = DateTime.UtcNow
                While Not process.WaitForExit(25)
                    cancellationToken.ThrowIfCancellationRequested()

                    If DateTime.UtcNow - waitStartedAt >= TimeSpan.FromSeconds(5) Then
                        TryKillProcess(process)
                        Throw New TimeoutException("FFmpeg seek preview burst decode timed out.")
                    End If
                End While

                If process.ExitCode <> 0 AndAlso decodedFrameCount = 0 Then
                    Dim errorText = process.StandardError.ReadToEnd()
                    Throw New InvalidOperationException(If(String.IsNullOrWhiteSpace(errorText), $"FFmpeg exited with code {process.ExitCode}.", FirstLine(errorText)))
                End If
            End Using
        Finally
            TryKillProcess(process)
            process.Dispose()
        End Try
    End Sub

    Private Shared Function BuildDecoderArguments(filePath As String, width As Integer, height As Integer, pixelFormat As String, isInterlaced As Boolean, startOffset As TimeSpan) As IReadOnlyList(Of String)
        Dim args As New List(Of String) From {
            "-hide_banner",
            "-loglevel",
            "warning",
            "-nostats"
        }

        If startOffset > TimeSpan.Zero AndAlso Not IsImageFile(filePath) Then
            args.Add("-ss")
            args.Add(FormatFfmpegTimestamp(startOffset))
        End If

        If IsImageFile(filePath) Then
            args.Add("-loop")
            args.Add("1")
        End If

        args.Add("-i")
        args.Add(filePath)
        args.Add("-map")
        args.Add("0:v:0")
        args.Add("-an")
        args.Add("-frames:v")
        args.Add("1")
        args.Add("-vf")
        args.Add(BuildVideoFilter(width, height, pixelFormat, isInterlaced))
        args.Add("-pix_fmt")
        args.Add(pixelFormat)
        args.Add("-f")
        args.Add("rawvideo")
        args.Add("pipe:1")
        Return args
    End Function

    Private Shared Function BuildBurstDecoderArguments(filePath As String, width As Integer, height As Integer, pixelFormat As String, isInterlaced As Boolean, startOffset As TimeSpan, frameCount As Integer, frameInterval As TimeSpan, keyframeOffset As TimeSpan?) As IReadOnlyList(Of String)
        Dim args As New List(Of String) From {
            "-hide_banner",
            "-loglevel",
            "warning",
            "-nostats"
        }

        Dim inputSeekOffset = startOffset
        Dim outputSeekOffset = TimeSpan.Zero

        If keyframeOffset.HasValue AndAlso keyframeOffset.Value >= TimeSpan.Zero AndAlso keyframeOffset.Value < startOffset AndAlso startOffset - keyframeOffset.Value <= TimeSpan.FromSeconds(15) Then
            inputSeekOffset = keyframeOffset.Value
            outputSeekOffset = startOffset - keyframeOffset.Value
        End If

        If inputSeekOffset > TimeSpan.Zero AndAlso Not IsImageFile(filePath) Then
            args.Add("-ss")
            args.Add(FormatFfmpegTimestamp(inputSeekOffset))
        End If

        If IsImageFile(filePath) Then
            args.Add("-loop")
            args.Add("1")
        End If

        args.Add("-i")
        args.Add(filePath)

        If outputSeekOffset > TimeSpan.Zero AndAlso Not IsImageFile(filePath) Then
            args.Add("-ss")
            args.Add(FormatFfmpegTimestamp(outputSeekOffset))
        End If

        args.Add("-map")
        args.Add("0:v:0")
        args.Add("-an")
        args.Add("-frames:v")
        args.Add(frameCount.ToString(CultureInfo.InvariantCulture))
        args.Add("-vf")
        args.Add(BuildBurstVideoFilter(width, height, pixelFormat, isInterlaced, frameInterval))
        args.Add("-pix_fmt")
        args.Add(pixelFormat)
        args.Add("-f")
        args.Add("rawvideo")
        args.Add("pipe:1")
        Return args
    End Function

    Private Shared Function BuildVideoFilter(width As Integer, height As Integer, pixelFormat As String, isInterlaced As Boolean) As String
        Dim parts As New List(Of String) From {
            $"scale={width}:{height}:force_original_aspect_ratio=decrease",
            $"pad={width}:{height}:(ow-iw)/2:(oh-ih)/2",
            "setsar=1"
        }

        If isInterlaced Then
            parts.Add("setfield=tff")
        End If

        parts.Add($"format={pixelFormat}")
        Return String.Join(",", parts)
    End Function

    Private Shared Function BuildBurstVideoFilter(width As Integer, height As Integer, pixelFormat As String, isInterlaced As Boolean, frameInterval As TimeSpan) As String
        Dim frameRate = If(frameInterval > TimeSpan.Zero, 1.0R / frameInterval.TotalSeconds, 25.0R)
        Dim frameRateText = frameRate.ToString("0.###", CultureInfo.InvariantCulture)
        Dim parts As New List(Of String) From {
            $"scale={width}:{height}:force_original_aspect_ratio=decrease",
            $"pad={width}:{height}:(ow-iw)/2:(oh-ih)/2",
            "setsar=1"
        }

        If isInterlaced Then
            parts.Add("setfield=tff")
        End If

        parts.Add($"fps={frameRateText}:start_time=0")
        parts.Add($"setpts=N/({frameRateText}*TB)")
        parts.Add($"format={pixelFormat}")
        Return String.Join(",", parts)
    End Function

    Private Shared Function StartDecoder(ffmpegPath As String, arguments As IReadOnlyList(Of String)) As Process
        Dim startInfo As New ProcessStartInfo() With {
            .FileName = ffmpegPath,
            .UseShellExecute = False,
            .RedirectStandardOutput = True,
            .RedirectStandardError = True,
            .CreateNoWindow = True
        }

        For Each argument In arguments
            startInfo.ArgumentList.Add(argument)
        Next

        Dim process As New Process() With {.StartInfo = startInfo}

        If Not process.Start() Then
            process.Dispose()
            Throw New InvalidOperationException("FFmpeg seek decoder could not be started.")
        End If

        Return process
    End Function

    Private Shared Function ReadExact(stream As Stream, buffer As Byte(), cancellationToken As CancellationToken) As Boolean
        Dim offset = 0

        While offset < buffer.Length
            cancellationToken.ThrowIfCancellationRequested()
            Dim bytesRead = stream.Read(buffer, offset, buffer.Length - offset)

            If bytesRead <= 0 Then
                Return False
            End If

            offset += bytesRead
        End While

        Return True
    End Function

    Private Shared Function IsImageFile(filePath As String) As Boolean
        Dim extension = Path.GetExtension(filePath)
        Return String.Equals(extension, ".bmp", StringComparison.OrdinalIgnoreCase) OrElse
            String.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase) OrElse
            String.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase) OrElse
            String.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase)
    End Function

    Private Shared Function FormatFfmpegTimestamp(value As TimeSpan) As String
        Return value.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture)
    End Function

    Private Shared Function FirstLine(text As String) As String
        If String.IsNullOrWhiteSpace(text) Then
            Return String.Empty
        End If

        For Each line In text.Split({ControlChars.Cr, ControlChars.Lf}, StringSplitOptions.RemoveEmptyEntries)
            If Not String.IsNullOrWhiteSpace(line) Then
                Return line.Trim()
            End If
        Next

        Return String.Empty
    End Function

    Private Shared Sub TryKillProcess(process As Process)
        Try
            If process IsNot Nothing AndAlso Not process.HasExited Then
                process.Kill(True)
            End If
        Catch
        End Try
    End Sub
End Class

Friend NotInheritable Class SeekDecodedFrame
    Public Sub New(width As Integer, height As Integer, pixelFormat As String, data As Byte())
        Me.Width = width
        Me.Height = height
        Me.PixelFormat = pixelFormat
        Me.Data = data
    End Sub

    Public ReadOnly Property Width As Integer
    Public ReadOnly Property Height As Integer
    Public ReadOnly Property PixelFormat As String
    Public ReadOnly Property Data As Byte()
End Class
