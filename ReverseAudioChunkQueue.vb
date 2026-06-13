Imports System.Buffers.Binary
Imports System.Diagnostics
Imports System.Globalization
Imports System.IO
Imports System.Threading
Imports System.Threading.Tasks

Friend NotInheritable Class ReverseAudioChunkQueue
    Implements IDisposable

    Private Const AudioSampleRate As Integer = 48000
    Private Const AudioBytesPerSample As Integer = 4
    Private Shared ReadOnly DecodeTimeout As TimeSpan = TimeSpan.FromSeconds(3)

    Private ReadOnly ffmpegPath As String
    Private ReadOnly inputPath As String
    Private ReadOnly speed As Double
    Private ReadOnly channels As Integer
    Private ReadOnly bytesPerSampleFrame As Integer
    Private ReadOnly logLine As Action(Of String)
    Private ReadOnly gate As New Object()
    Private ReadOnly chunks As New Queue(Of Byte())()
    Private ReadOnly cancellation As New CancellationTokenSource()
    Private nextDecodeEnd As TimeSpan
    Private chunkOffset As Integer
    Private queuedBytes As Integer
    Private decodeRunning As Boolean
    Private sourceEnded As Boolean
    Private disposed As Boolean

    Public Sub New(ffmpegPath As String, inputPath As String, playbackSpeed As Double, channels As Integer, startPosition As TimeSpan, logLine As Action(Of String))
        If channels <= 0 Then
            Throw New ArgumentOutOfRangeException(NameOf(channels), "Audio channels must be positive.")
        End If

        Me.ffmpegPath = ffmpegPath
        Me.inputPath = inputPath
        Me.speed = Math.Max(1.0R, Math.Min(20.0R, Math.Abs(playbackSpeed)))
        Me.channels = channels
        Me.bytesPerSampleFrame = channels * AudioBytesPerSample
        Me.nextDecodeEnd = If(startPosition > TimeSpan.Zero, startPosition, TimeSpan.Zero)
        Me.logLine = logLine
        StartDecodeIfNeeded()
    End Sub

    Public Function ReadFrame(frameDuration As TimeSpan) As ReverseAudioFrame
        Dim sampleFrames = Math.Max(1, CInt(Math.Round(frameDuration.TotalSeconds * AudioSampleRate, MidpointRounding.AwayFromZero)))
        Dim output(sampleFrames * bytesPerSampleFrame - 1) As Byte
        Dim copied = 0

        SyncLock gate
            While copied < output.Length AndAlso chunks.Count > 0
                Dim chunk = chunks.Peek()
                Dim available = chunk.Length - chunkOffset
                Dim toCopy = Math.Min(output.Length - copied, available)
                Buffer.BlockCopy(chunk, chunkOffset, output, copied, toCopy)
                copied += toCopy
                chunkOffset += toCopy
                queuedBytes -= toCopy

                If chunkOffset >= chunk.Length Then
                    chunks.Dequeue()
                    chunkOffset = 0
                End If
            End While
        End SyncLock

        StartDecodeIfNeeded()
        Return New ReverseAudioFrame(output, sampleFrames, copied > 0)
    End Function

    Private Sub StartDecodeIfNeeded()
        Dim decodeEnd As TimeSpan
        Dim sourceStart As TimeSpan
        Dim sourceDuration As TimeSpan

        SyncLock gate
            If disposed OrElse decodeRunning OrElse sourceEnded OrElse queuedBytes >= GetLowWaterBytes() Then
                Return
            End If

            If nextDecodeEnd <= TimeSpan.Zero Then
                sourceEnded = True
                Return
            End If

            sourceDuration = TimeSpan.FromTicks(CLng(Math.Round(GetDecodeOutputBlockDuration(speed).Ticks * speed, MidpointRounding.AwayFromZero)))
            decodeEnd = nextDecodeEnd
            sourceStart = decodeEnd - sourceDuration

            If sourceStart < TimeSpan.Zero Then
                sourceStart = TimeSpan.Zero
                sourceDuration = decodeEnd
            End If

            If sourceDuration <= TimeSpan.Zero Then
                sourceEnded = True
                Return
            End If

            nextDecodeEnd = sourceStart
            decodeRunning = True
        End SyncLock

        Task.Run(Function() DecodeBlockAsync(sourceStart, sourceDuration))
    End Sub

    Private Async Function DecodeBlockAsync(sourceStart As TimeSpan, sourceDuration As TimeSpan) As Task
        Try
            Dim pcm = Await DecodeReverseAudioAsync(sourceStart, sourceDuration, cancellation.Token)

            If pcm.Length > 0 Then
                SyncLock gate
                    If Not disposed Then
                        chunks.Enqueue(pcm)
                        queuedBytes += pcm.Length
                    End If
                End SyncLock
            End If
        Catch ex As OperationCanceledException
        Catch ex As Exception
            SyncLock gate
                sourceEnded = True
            End SyncLock

            If logLine IsNot Nothing Then
                logLine($"Reverse audio disabled: {ex.Message}")
            End If
        Finally
            SyncLock gate
                decodeRunning = False
            End SyncLock

            StartDecodeIfNeeded()
        End Try
    End Function

    Private Async Function DecodeReverseAudioAsync(sourceStart As TimeSpan, sourceDuration As TimeSpan, cancellationToken As CancellationToken) As Task(Of Byte())
        Using timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
            timeout.CancelAfter(DecodeTimeout)

            Using process As New Process()
                process.StartInfo = New ProcessStartInfo() With {
                    .FileName = ffmpegPath,
                    .UseShellExecute = False,
                    .RedirectStandardOutput = True,
                    .RedirectStandardError = True,
                    .CreateNoWindow = True
                }

                For Each argument In BuildArguments(sourceStart, sourceDuration)
                    process.StartInfo.ArgumentList.Add(argument)
                Next

                If Not process.Start() Then
                    Throw New InvalidOperationException("FFmpeg reverse audio decoder could not be started.")
                End If

                Try
                    Using output As New MemoryStream()
                        Dim stdoutTask = process.StandardOutput.BaseStream.CopyToAsync(output, timeout.Token)
                        Dim stderrTask = process.StandardError.ReadToEndAsync(timeout.Token)
                        Await Task.WhenAll(stdoutTask, process.WaitForExitAsync(timeout.Token), stderrTask)

                        If process.ExitCode <> 0 Then
                            Dim errorMessage = FirstLine(stderrTask.Result)
                            Throw New InvalidOperationException(If(String.IsNullOrWhiteSpace(errorMessage), $"ffmpeg exited with code {process.ExitCode}", errorMessage))
                        End If

                        Dim sourcePcm = output.ToArray()
                        Dim alignedLength = sourcePcm.Length - (sourcePcm.Length Mod bytesPerSampleFrame)

                        If alignedLength <> sourcePcm.Length Then
                            Array.Resize(sourcePcm, alignedLength)
                        End If

                        Return BuildReverseShuttlePcm(sourcePcm)
                    End Using
                Catch
                    TryKill(process)
                    Throw
                End Try
            End Using
        End Using
    End Function

    Private Function BuildArguments(sourceStart As TimeSpan, sourceDuration As TimeSpan) As IReadOnlyList(Of String)
        Return New List(Of String) From {
            "-hide_banner",
            "-loglevel",
            "warning",
            "-nostats",
            "-ss",
            FormatFfmpegTimestamp(sourceStart),
            "-t",
            FormatFfmpegTimestamp(sourceDuration),
            "-i",
            inputPath,
            "-map",
            "0:a:0?",
            "-vn",
            "-af",
            "aresample=async=0:first_pts=0",
            "-ac",
            channels.ToString(CultureInfo.InvariantCulture),
            "-ar",
            AudioSampleRate.ToString(CultureInfo.InvariantCulture),
            "-sample_fmt",
            "s32",
            "-f",
            "s32le",
            "pipe:1"
        }
    End Function

    Private Function BuildReverseShuttlePcm(sourcePcm As Byte()) As Byte()
        Dim sourceSampleFrames = sourcePcm.Length \ bytesPerSampleFrame

        If sourceSampleFrames <= 0 Then
            Return Array.Empty(Of Byte)()
        End If

        Dim outputSampleFrames = Math.Max(1, CInt(Math.Floor(sourceSampleFrames / speed)))
        Dim outputPcm(outputSampleFrames * bytesPerSampleFrame - 1) As Byte
        Dim previous(channels - 1) As Double
        Dim smoothing = GetSmoothing(speed)

        For outputFrame = 0 To outputSampleFrames - 1
            Dim sourcePosition = Math.Max(0.0R, sourceSampleFrames - 1.0R - (outputFrame * speed))
            Dim sourceFrame0 = CInt(Math.Floor(sourcePosition))
            Dim sourceFrame1 = Math.Min(sourceSampleFrames - 1, sourceFrame0 + 1)
            Dim fraction = sourcePosition - sourceFrame0

            For channel = 0 To channels - 1
                Dim sample0 = ReadSample(sourcePcm, sourceFrame0, channel)
                Dim sample1 = ReadSample(sourcePcm, sourceFrame1, channel)
                Dim sample = sample0 + ((sample1 - sample0) * fraction)

                If outputFrame > 0 AndAlso smoothing > 0.0R Then
                    sample = sample * (1.0R - smoothing) + previous(channel) * smoothing
                End If

                previous(channel) = sample
                WriteSample(outputPcm, outputFrame, channel, sample)
            Next
        Next

        Return outputPcm
    End Function

    Private Function ReadSample(pcm As Byte(), sampleFrame As Integer, channel As Integer) As Double
        Dim offset = (sampleFrame * channels + channel) * AudioBytesPerSample
        Return BinaryPrimitives.ReadInt32LittleEndian(pcm.AsSpan(offset, AudioBytesPerSample))
    End Function

    Private Sub WriteSample(pcm As Byte(), sampleFrame As Integer, channel As Integer, sample As Double)
        Dim offset = (sampleFrame * channels + channel) * AudioBytesPerSample
        Dim value = CInt(Math.Max(Integer.MinValue, Math.Min(Integer.MaxValue, sample)))
        BinaryPrimitives.WriteInt32LittleEndian(pcm.AsSpan(offset, AudioBytesPerSample), value)
    End Sub

    Private Shared Function GetDecodeOutputBlockDuration(playbackSpeed As Double) As TimeSpan
        If playbackSpeed >= 10.0R Then
            Return TimeSpan.FromMilliseconds(300)
        End If

        If playbackSpeed >= 5.0R Then
            Return TimeSpan.FromMilliseconds(400)
        End If

        If playbackSpeed >= 2.0R Then
            Return TimeSpan.FromMilliseconds(1200)
        End If

        Return TimeSpan.FromMilliseconds(2200)
    End Function

    Private Function GetLowWaterBytes() As Integer
        Dim seconds = If(speed >= 10.0R, 0.8R, If(speed >= 5.0R, 1.0R, If(speed >= 2.0R, 1.5R, 3.0R)))
        Return CInt(AudioSampleRate * seconds) * bytesPerSampleFrame
    End Function

    Private Shared Function GetSmoothing(playbackSpeed As Double) As Double
        If playbackSpeed >= 10.0R Then
            Return 0.35R
        End If

        Return If(playbackSpeed >= 5.0R, 0.2R, 0.0R)
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

    Private Shared Function FormatFfmpegTimestamp(value As TimeSpan) As String
        Return value.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture)
    End Function

    Private Shared Sub TryKill(process As Process)
        Try
            If process IsNot Nothing AndAlso Not process.HasExited Then
                process.Kill(True)
            End If
        Catch
        End Try
    End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        SyncLock gate
            If disposed Then
                Return
            End If

            disposed = True
            chunks.Clear()
            queuedBytes = 0
        End SyncLock

        cancellation.Cancel()
        cancellation.Dispose()
    End Sub
End Class

Friend Structure ReverseAudioFrame
    Public Sub New(pcm As Byte(), sampleFrames As Integer, hasAudio As Boolean)
        Me.Pcm = pcm
        Me.SampleFrames = sampleFrames
        Me.HasAudio = hasAudio
    End Sub

    Public ReadOnly Property Pcm As Byte()
    Public ReadOnly Property SampleFrames As Integer
    Public ReadOnly Property HasAudio As Boolean
End Structure
