Imports System.Collections.Generic
Imports System.Diagnostics
Imports System.Globalization
Imports System.IO
Imports System.Runtime.InteropServices
Imports System.Threading
Imports System.Threading.Tasks
Imports DeckLinkAPI

Friend NotInheritable Class InProcessDeckLinkOutputRunner
    Implements IDisposable

    Private Const BytesPerPixelUyvy As Integer = 2
    Private Const AudioSampleRate As Integer = 48000
    Private Const AudioChannels As Integer = 2
    Private Const AudioBytesPerSample As Integer = 4
    Private Const AudioChunkSampleFrames As Integer = 480
    Private Const AudioWriteZeroRetryDelayMs As Integer = 2
    Private Const AudioWriteStallLogMilliseconds As Integer = 500

    Private Shared ReadOnly ModeByCode As New Dictionary(Of String, _BMDDisplayMode)(StringComparer.Ordinal) From {
        {"pal", _BMDDisplayMode.bmdModePAL},
        {"ntsc", _BMDDisplayMode.bmdModeNTSC},
        {"Hp25", _BMDDisplayMode.bmdModeHD1080p25},
        {"Hp50", _BMDDisplayMode.bmdModeHD1080p50},
        {"Hi50", _BMDDisplayMode.bmdModeHD1080i50}
    }

    Private Shared ReadOnly ImageExtensions As New HashSet(Of String)(StringComparer.OrdinalIgnoreCase) From {
        ".bmp",
        ".jpeg",
        ".jpg",
        ".png"
    }

    Private ReadOnly lifecycleLock As New Object()
    Private ReadOnly outputLock As New Object()
    Private deckLink As IDeckLink
    Private output As IDeckLinkOutput_v14_2_1
    Private currentDeviceName As String
    Private currentDisplayMode As _BMDDisplayMode
    Private videoOutputEnabled As Boolean
    Private audioOutputEnabled As Boolean
    Private outputWidth As Integer
    Private outputHeight As Integer
    Private outputRowBytes As Integer
    Private outputFrameBytes As Integer
    Private outputFrameDuration As Long
    Private outputTimeScale As Long
    Private playbackCancellation As CancellationTokenSource
    Private playbackTask As Task
    Private videoDecoder As Process
    Private audioDecoder As Process
    Private disposed As Boolean

    Public Event LogReceived(message As String)
    Public Event Exited(exitCode As Integer)
    Public Event PlaybackEnded(exitCode As Integer)

    Public Async Function DisplayScrubFrameAsync(ffmpegPath As String, filePath As String, deviceName As String, formatCode As String, width As Integer, height As Integer, frameRate As String, isInterlaced As Boolean, startOffset As TimeSpan, cancellationToken As CancellationToken) As Task
        ThrowIfDisposed()
        cancellationToken.ThrowIfCancellationRequested()

        Await Task.Run(
            Sub()
                cancellationToken.ThrowIfCancellationRequested()
                StopPlayback(disableVideoOutput:=False)

                SyncLock lifecycleLock
                    ThrowIfDisposed()
                    cancellationToken.ThrowIfCancellationRequested()
                    EnsureOutputLocked(deviceName, formatCode, width, height, frameRate)
                    Dim frame = DecodeSingleFrame(ffmpegPath, filePath, outputWidth, outputHeight, frameRate, isInterlaced, startOffset, cancellationToken)
                    cancellationToken.ThrowIfCancellationRequested()
                    DisplayRawFrameLocked(frame)
                End SyncLock
            End Sub,
            cancellationToken)
    End Function

    Public Async Function StartPlaybackAsync(ffmpegPath As String, filePath As String, deviceName As String, formatCode As String, width As Integer, height As Integer, frameRate As String, isInterlaced As Boolean, hasAudioStream As Boolean, startOffset As TimeSpan, playbackSpeed As Double) As Task
        ThrowIfDisposed()

        Await Task.Run(
            Sub()
                StopPlayback(disableVideoOutput:=False)
                Dim normalizedSpeed = NormalizePlaybackSpeed(playbackSpeed)

                Dim tokenSource As New CancellationTokenSource()
                Dim localVideoDecoder As Process = Nothing
                Dim localAudioDecoder As Process = Nothing

                Try
                    SyncLock lifecycleLock
                        ThrowIfDisposed()
                        EnsureOutputLocked(deviceName, formatCode, width, height, frameRate)

                        Dim playAudio = hasAudioStream AndAlso Not IsImageFile(filePath)
                        If playAudio Then
                            EnableAudioOutputLocked()
                        Else
                            DisableAudioOutputLocked()
                        End If

                        localVideoDecoder = StartDecoder(ffmpegPath, BuildVideoDecoderArguments(filePath, outputWidth, outputHeight, frameRate, isInterlaced, startOffset, normalizedSpeed))

                        If playAudio Then
                            localAudioDecoder = StartDecoder(ffmpegPath, BuildAudioDecoderArguments(filePath, startOffset, normalizedSpeed))
                        End If

                        playbackCancellation = tokenSource
                        videoDecoder = localVideoDecoder
                        audioDecoder = localAudioDecoder

                        Dim activeOutput = output
                        Dim activeWidth = outputWidth
                        Dim activeHeight = outputHeight
                        Dim activeRowBytes = outputRowBytes
                        Dim activeFrameBytes = outputFrameBytes
                        Dim activeFrameDuration = outputFrameDuration
                        Dim activeTimeScale = outputTimeScale
                        playbackTask = Task.Run(
                            Async Function()
                                Await RunPlaybackAsync(tokenSource, localVideoDecoder, localAudioDecoder, activeOutput, activeWidth, activeHeight, activeRowBytes, activeFrameBytes, activeFrameDuration, activeTimeScale)
                            End Function)
                    End SyncLock
                Catch
                    TryKill(localVideoDecoder)
                    TryKill(localAudioDecoder)
                    DisposeProcess(localVideoDecoder)
                    DisposeProcess(localAudioDecoder)
                    tokenSource.Dispose()

                    SyncLock lifecycleLock
                        DisableAudioOutputLocked()
                    End SyncLock

                    Throw
                End Try
            End Sub)
    End Function

    Public Sub [Stop]()
        StopPlayback(disableVideoOutput:=True)
    End Sub

    Private Async Function RunPlaybackAsync(tokenSource As CancellationTokenSource, localVideoDecoder As Process, localAudioDecoder As Process, activeOutput As IDeckLinkOutput_v14_2_1, width As Integer, height As Integer, rowBytes As Integer, frameBytes As Integer, frameDuration As Long, timeScale As Long) As Task
        Dim token = tokenSource.Token
        Dim exitCode = 0
        Dim shouldRaiseExit = False
        Dim shouldRaisePlaybackEnded = False
        Dim videoStderrTask As Task = Nothing
        Dim audioStderrTask As Task = Nothing
        Dim audioPumpTask As Task = Nothing

        Try
            videoStderrTask = Task.Run(
                Async Function()
                    Await PumpErrorAsync(localVideoDecoder, "decklink-video", token)
                End Function)

            If localAudioDecoder IsNot Nothing Then
                audioStderrTask = Task.Run(
                    Async Function()
                        Await PumpErrorAsync(localAudioDecoder, "decklink-audio", token)
                    End Function)
                audioPumpTask = Task.Run(
                    Async Function()
                        Await PumpAudioAsync(activeOutput, localAudioDecoder, token)
                    End Function)
            End If

            Dim frameBuffer(frameBytes - 1) As Byte
            Dim frameNumber = 0L
            Dim playbackStopwatch = Stopwatch.StartNew()
            Dim frameTicks = Math.Max(1L, Stopwatch.Frequency * frameDuration \ Math.Max(1L, timeScale))

            While Not token.IsCancellationRequested
                If Not Await ReadExactFrameAsync(localVideoDecoder.StandardOutput.BaseStream, frameBuffer, token) Then
                    Exit While
                End If

                DisplayRawFrame(activeOutput, frameBuffer, width, height, rowBytes, frameBytes)
                frameNumber += 1

                If frameNumber Mod 100 = 0 Then
                    RaiseEvent LogReceived($"sdk_frame={frameNumber}")
                End If

                Dim targetTicks = frameNumber * frameTicks
                Dim remainingTicks = targetTicks - playbackStopwatch.ElapsedTicks

                If remainingTicks > 0 Then
                    Dim delayMs = CInt(Math.Min(remainingTicks * 1000 \ Stopwatch.Frequency, 100L))

                    If delayMs > 0 Then
                        Await Task.Delay(delayMs, token)
                    End If
                End If
            End While

            TryKill(localAudioDecoder)
            Await WaitForProcessExitAsync(localVideoDecoder)
            Await WaitForProcessExitAsync(localAudioDecoder)

            If localVideoDecoder IsNot Nothing Then
                exitCode = If(token.IsCancellationRequested, 0, localVideoDecoder.ExitCode)
            End If
        Catch ex As OperationCanceledException
            exitCode = 0
        Catch ex As Exception
            exitCode = -1
            RaiseEvent LogReceived($"DeckLink SDK output failed: {ex.Message}")
        End Try

        TryKill(localVideoDecoder)
        TryKill(localAudioDecoder)
        Await IgnoreCancellationAsync(videoStderrTask)
        Await IgnoreCancellationAsync(audioStderrTask)
        Await IgnoreCancellationAsync(audioPumpTask)
        DisposeProcess(localVideoDecoder)
        DisposeProcess(localAudioDecoder)

        Dim shouldHoldLastFrame = exitCode = 0 AndAlso Not token.IsCancellationRequested

        SyncLock lifecycleLock
            If Object.ReferenceEquals(playbackCancellation, tokenSource) Then
                playbackCancellation = Nothing
                playbackTask = Nothing
                videoDecoder = Nothing
                audioDecoder = Nothing
                DisableAudioOutputLocked()

                If shouldHoldLastFrame Then
                    shouldRaisePlaybackEnded = Not disposed
                Else
                    ReleaseOutputLocked()
                    shouldRaiseExit = Not disposed AndAlso Not token.IsCancellationRequested
                End If
            End If
        End SyncLock

        tokenSource.Dispose()

        If shouldRaisePlaybackEnded Then
            RaiseEvent PlaybackEnded(exitCode)
        End If

        If shouldRaiseExit Then
            RaiseEvent Exited(exitCode)
        End If
    End Function

    Private Sub StopPlayback(disableVideoOutput As Boolean)
        Dim source As CancellationTokenSource = Nothing
        Dim taskToWait As Task = Nothing
        Dim localVideoDecoder As Process = Nothing
        Dim localAudioDecoder As Process = Nothing

        SyncLock lifecycleLock
            source = playbackCancellation
            taskToWait = playbackTask
            localVideoDecoder = videoDecoder
            localAudioDecoder = audioDecoder
            playbackCancellation = Nothing
            playbackTask = Nothing
            videoDecoder = Nothing
            audioDecoder = Nothing
        End SyncLock

        If source IsNot Nothing Then
            Try
                source.Cancel()
            Catch
            End Try
        End If

        TryKill(localVideoDecoder)
        TryKill(localAudioDecoder)

        If taskToWait IsNot Nothing Then
            Try
                taskToWait.Wait(2000)
            Catch ex As AggregateException
                ex.Handle(Function(inner) TypeOf inner Is OperationCanceledException OrElse TypeOf inner Is ObjectDisposedException)
            Catch
            End Try
        End If

        DisposeProcess(localVideoDecoder)
        DisposeProcess(localAudioDecoder)

        If source IsNot Nothing Then
            Try
                source.Dispose()
            Catch
            End Try
        End If

        SyncLock lifecycleLock
            DisableAudioOutputLocked()

            If disableVideoOutput Then
                ReleaseOutputLocked()
            End If
        End SyncLock
    End Sub

    Private Sub EnsureOutputLocked(deviceName As String, formatCode As String, requestedWidth As Integer, requestedHeight As Integer, frameRate As String)
        Dim displayMode = ResolveDisplayMode(formatCode, requestedWidth, requestedHeight, frameRate)

        If output IsNot Nothing AndAlso
            String.Equals(currentDeviceName, deviceName, StringComparison.OrdinalIgnoreCase) AndAlso
            currentDisplayMode = displayMode Then

            If Not videoOutputEnabled Then
                output.EnableVideoOutput(displayMode, _BMDVideoOutputFlags.bmdVideoOutputFlagDefault)
                videoOutputEnabled = True
            End If

            Return
        End If

        ReleaseOutputLocked()

        deckLink = FindDeckLink(deviceName)
        output = CType(deckLink, IDeckLinkOutput_v14_2_1)
        currentDeviceName = deviceName
        currentDisplayMode = displayMode

        Dim displayModeInfo As IDeckLinkDisplayMode = Nothing

        Try
            output.GetDisplayMode(displayMode, displayModeInfo)

            If displayModeInfo Is Nothing Then
                Throw New InvalidOperationException($"DeckLink display mode is unavailable: {formatCode}.")
            End If

            outputWidth = displayModeInfo.GetWidth()
            outputHeight = displayModeInfo.GetHeight()
            displayModeInfo.GetFrameRate(outputFrameDuration, outputTimeScale)
        Finally
            ReleaseCom(displayModeInfo)
        End Try

        If outputWidth <= 0 Then
            outputWidth = requestedWidth
        End If

        If outputHeight <= 0 Then
            outputHeight = requestedHeight
        End If

        outputRowBytes = outputWidth * BytesPerPixelUyvy
        outputFrameBytes = outputRowBytes * outputHeight
        output.EnableVideoOutput(displayMode, _BMDVideoOutputFlags.bmdVideoOutputFlagDefault)
        videoOutputEnabled = True
        RaiseEvent LogReceived($"DeckLink API output enabled: {deviceName}, {outputWidth}x{outputHeight}, {NormalizeRateString(frameRate)} fps.")
    End Sub

    Private Sub EnableAudioOutputLocked()
        If output Is Nothing Then
            Return
        End If

        If audioOutputEnabled Then
            DisableAudioOutputLocked()
        End If

        output.EnableAudioOutput(_BMDAudioSampleRate.bmdAudioSampleRate48kHz, _BMDAudioSampleType.bmdAudioSampleType32bitInteger, CUInt(AudioChannels), _BMDAudioOutputStreamType.bmdAudioOutputStreamContinuous)
        audioOutputEnabled = True
    End Sub

    Private Sub DisableAudioOutputLocked()
        If output Is Nothing OrElse Not audioOutputEnabled Then
            audioOutputEnabled = False
            Return
        End If

        Try
            output.FlushBufferedAudioSamples()
        Catch
        End Try

        Try
            output.DisableAudioOutput()
        Catch
        End Try

        audioOutputEnabled = False
    End Sub

    Private Sub ReleaseOutputLocked()
        DisableAudioOutputLocked()

        If output IsNot Nothing AndAlso videoOutputEnabled Then
            Try
                output.DisableVideoOutput()
            Catch
            End Try
        End If

        videoOutputEnabled = False
        ReleaseCom(output)
        ReleaseCom(deckLink)
        output = Nothing
        deckLink = Nothing
        currentDeviceName = Nothing
        outputWidth = 0
        outputHeight = 0
        outputRowBytes = 0
        outputFrameBytes = 0
        outputFrameDuration = 0
        outputTimeScale = 0
    End Sub

    Private Sub DisplayRawFrameLocked(frameBytes As Byte())
        If output Is Nothing Then
            Throw New InvalidOperationException("DeckLink output is not enabled.")
        End If

        DisplayRawFrame(output, frameBytes, outputWidth, outputHeight, outputRowBytes, outputFrameBytes)
    End Sub

    Private Sub DisplayRawFrame(activeOutput As IDeckLinkOutput_v14_2_1, frameBytes As Byte(), width As Integer, height As Integer, rowBytes As Integer, expectedFrameBytes As Integer)
        If frameBytes Is Nothing OrElse frameBytes.Length < expectedFrameBytes Then
            Throw New InvalidOperationException($"Decoded frame is too small. Expected {expectedFrameBytes} bytes, got {If(frameBytes?.Length, 0)}.")
        End If

        Dim mutableFrame As IDeckLinkMutableVideoFrame_v14_2_1 = Nothing

        Try
            activeOutput.CreateVideoFrame(width, height, rowBytes, _BMDPixelFormat.bmdFormat8BitYUV, _BMDFrameFlags.bmdFrameFlagDefault, mutableFrame)
            Dim destination As IntPtr = IntPtr.Zero
            mutableFrame.GetBytes(destination)
            Marshal.Copy(frameBytes, 0, destination, expectedFrameBytes)

            SyncLock outputLock
                activeOutput.DisplayVideoFrameSync(mutableFrame)
            End SyncLock
        Finally
            ReleaseCom(mutableFrame)
        End Try
    End Sub

    Private Shared Function DecodeSingleFrame(ffmpegPath As String, filePath As String, width As Integer, height As Integer, frameRate As String, isInterlaced As Boolean, startOffset As TimeSpan, cancellationToken As CancellationToken) As Byte()
        Dim frameBytes = width * height * BytesPerPixelUyvy
        Dim buffer(frameBytes - 1) As Byte
        Dim process = StartDecoder(ffmpegPath, BuildSingleFrameDecoderArguments(filePath, width, height, frameRate, isInterlaced, startOffset))

        Try
            Using cancellationToken.Register(Sub() TryKill(process))
                If Not ReadExactFrame(process.StandardOutput.BaseStream, buffer, cancellationToken) Then
                    Dim errorText = process.StandardError.ReadToEnd()
                    Throw New InvalidOperationException(If(String.IsNullOrWhiteSpace(errorText), "FFmpeg did not decode a scrub frame.", errorText.Trim()))
                End If

                cancellationToken.ThrowIfCancellationRequested()

                Dim waitStartedAt = DateTime.UtcNow
                While Not process.WaitForExit(25)
                    cancellationToken.ThrowIfCancellationRequested()

                    If DateTime.UtcNow - waitStartedAt >= TimeSpan.FromSeconds(5) Then
                        Exit While
                    End If
                End While

                If Not process.HasExited Then
                    TryKill(process)
                    Return Nothing
                End If
            End Using

            If buffer Is Nothing Then
                Dim errorText = process.StandardError.ReadToEnd()
                Throw New InvalidOperationException(If(String.IsNullOrWhiteSpace(errorText), "FFmpeg did not decode a scrub frame.", errorText.Trim()))
            End If

            Return buffer
        Finally
            DisposeProcess(process)
        End Try
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

        Dim process As New Process() With {
            .StartInfo = startInfo
        }

        If Not process.Start() Then
            process.Dispose()
            Throw New InvalidOperationException("FFmpeg decoder could not be started.")
        End If

        Return process
    End Function

    Private Shared Function BuildSingleFrameDecoderArguments(filePath As String, width As Integer, height As Integer, frameRate As String, isInterlaced As Boolean, startOffset As TimeSpan) As IReadOnlyList(Of String)
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

        args.Add("-i")
        args.Add(filePath)
        args.Add("-map")
        args.Add("0:v:0")
        args.Add("-an")
        args.Add("-frames:v")
        args.Add("1")
        args.Add("-vf")
        args.Add(BuildVideoFilter(width, height, frameRate, isInterlaced))
        args.Add("-pix_fmt")
        args.Add("uyvy422")
        args.Add("-f")
        args.Add("rawvideo")
        args.Add("pipe:1")

        Return args
    End Function

    Private Shared Function BuildVideoDecoderArguments(filePath As String, width As Integer, height As Integer, frameRate As String, isInterlaced As Boolean, startOffset As TimeSpan, playbackSpeed As Double) As IReadOnlyList(Of String)
        Dim args As New List(Of String) From {
            "-hide_banner",
            "-loglevel",
            "warning",
            "-nostats"
        }

        If IsImageFile(filePath) Then
            args.Add("-loop")
            args.Add("1")
            args.Add("-framerate")
            args.Add(NormalizeRateString(frameRate))
        ElseIf startOffset > TimeSpan.Zero Then
            args.Add("-ss")
            args.Add(FormatFfmpegTimestamp(startOffset))
        End If

        args.Add("-i")
        args.Add(filePath)
        args.Add("-map")
        args.Add("0:v:0")
        args.Add("-an")
        args.Add("-vf")
        args.Add(BuildVideoFilter(width, height, frameRate, isInterlaced, playbackSpeed))
        args.Add("-s")
        args.Add($"{width}x{height}")
        args.Add("-pix_fmt")
        args.Add("uyvy422")
        args.Add("-f")
        args.Add("rawvideo")
        args.Add("pipe:1")

        Return args
    End Function

    Private Shared Function BuildAudioDecoderArguments(filePath As String, startOffset As TimeSpan, playbackSpeed As Double) As IReadOnlyList(Of String)
        Dim args As New List(Of String) From {
            "-hide_banner",
            "-loglevel",
            "warning",
            "-nostats"
        }

        If startOffset > TimeSpan.Zero Then
            args.Add("-ss")
            args.Add(FormatFfmpegTimestamp(startOffset))
        End If

        args.Add("-i")
        args.Add(filePath)
        args.Add("-map")
        args.Add("0:a:0")
        args.Add("-vn")
        args.Add("-af")
        args.Add(BuildAudioFilter(playbackSpeed))
        args.Add("-ac")
        args.Add(AudioChannels.ToString(CultureInfo.InvariantCulture))
        args.Add("-ar")
        args.Add(AudioSampleRate.ToString(CultureInfo.InvariantCulture))
        args.Add("-sample_fmt")
        args.Add("s32")
        args.Add("-f")
        args.Add("s32le")
        args.Add("pipe:1")

        Return args
    End Function

    Private Shared Function BuildAudioFilter(playbackSpeed As Double) As String
        Dim audioSpeedFilter = BuildAudioSpeedFilterChain(playbackSpeed)
        Dim filters As New List(Of String)()

        If Not String.IsNullOrWhiteSpace(audioSpeedFilter) Then
            filters.Add(audioSpeedFilter)
        End If

        filters.Add("aresample=async=1000:first_pts=0")
        filters.Add("aformat=sample_fmts=s32:channel_layouts=stereo")
        filters.Add("asetpts=N/SR/TB")
        Return String.Join(",", filters)
    End Function

    Private Shared Function BuildVideoFilter(width As Integer, height As Integer, frameRate As String, isInterlaced As Boolean, Optional playbackSpeed As Double = 1.0R) As String
        Dim normalizedRate = NormalizeRateString(frameRate)
        Dim speedNumber = FormatFilterNumber(NormalizePlaybackSpeed(playbackSpeed))
        Dim parts As New List(Of String) From {
            $"setpts=PTS/{speedNumber}",
            $"scale={width}:{height}:force_original_aspect_ratio=decrease",
            $"pad={width}:{height}:(ow-iw)/2:(oh-ih)/2",
            "setsar=1",
            $"fps={normalizedRate}:start_time=0",
            $"setpts=N/({normalizedRate}*TB)"
        }

        If isInterlaced Then
            parts.Add("setfield=tff")
        End If

        parts.Add("format=uyvy422")
        Return String.Join(",", parts)
    End Function

    Private Shared Function NormalizePlaybackSpeed(speed As Double) As Double
        If Double.IsNaN(speed) OrElse Double.IsInfinity(speed) Then
            Return 1.0R
        End If

        Dim clamped = Math.Max(0.1R, Math.Min(20.0R, Math.Abs(speed)))
        Return Math.Round(clamped, 1, MidpointRounding.AwayFromZero)
    End Function

    Private Shared Function FormatFilterNumber(value As Double) As String
        Return Math.Max(0.001R, Math.Abs(value)).ToString("0.###", CultureInfo.InvariantCulture)
    End Function

    Private Shared Function BuildAudioSpeedFilterChain(playbackSpeed As Double) As String
        Dim speed = NormalizePlaybackSpeed(playbackSpeed)

        If Math.Abs(speed - 1.0R) < 0.001R Then
            Return String.Empty
        End If

        Dim remaining = speed
        Dim filters As New List(Of String)()

        While remaining > 2.0R
            filters.Add("atempo=2")
            remaining /= 2.0R
        End While

        While remaining < 0.5R
            filters.Add("atempo=0.5")
            remaining /= 0.5R
        End While

        If Math.Abs(remaining - 1.0R) >= 0.001R Then
            filters.Add($"atempo={FormatFilterNumber(remaining)}")
        End If

        Return String.Join(",", filters)
    End Function

    Private Shared Function ResolveDisplayMode(formatCode As String, width As Integer, height As Integer, frameRate As String) As _BMDDisplayMode
        Dim displayMode As _BMDDisplayMode

        If Not String.IsNullOrWhiteSpace(formatCode) AndAlso ModeByCode.TryGetValue(formatCode, displayMode) Then
            Return displayMode
        End If

        Dim normalizedRate = NormalizeRateString(frameRate)

        If width = 1920 AndAlso height = 1080 AndAlso normalizedRate = "25" Then
            Return _BMDDisplayMode.bmdModeHD1080p25
        End If

        If width = 1920 AndAlso height = 1080 AndAlso normalizedRate = "50" Then
            Return _BMDDisplayMode.bmdModeHD1080p50
        End If

        If width = 720 AndAlso height = 576 Then
            Return _BMDDisplayMode.bmdModePAL
        End If

        Throw New InvalidOperationException($"DeckLink display mode is not mapped: {formatCode} {width}x{height} {frameRate}.")
    End Function

    Private Shared Function FindDeckLink(requestedDevice As String) As IDeckLink
        Dim iterator As CDeckLinkIteratorClass = Nothing

        Try
            iterator = New CDeckLinkIteratorClass()

            Do
                Dim candidate As IDeckLink = Nothing

                Try
                    iterator.Next(candidate)
                Catch ex As COMException
                    Exit Do
                End Try

                If candidate Is Nothing Then
                    Exit Do
                End If

                Dim displayName As String = Nothing
                Dim modelName As String = Nothing

                Try
                    candidate.GetDisplayName(displayName)
                    candidate.GetModelName(modelName)

                    If DeviceNameMatches(displayName, requestedDevice) OrElse DeviceNameMatches(modelName, requestedDevice) Then
                        Return candidate
                    End If
                Finally
                    If Not DeviceNameMatches(displayName, requestedDevice) AndAlso Not DeviceNameMatches(modelName, requestedDevice) Then
                        ReleaseCom(candidate)
                    End If
                End Try
            Loop
        Finally
            ReleaseCom(iterator)
        End Try

        Throw New InvalidOperationException($"DeckLink SDK device not found: {requestedDevice}")
    End Function

    Private Shared Function DeviceNameMatches(candidateName As String, requestedDevice As String) As Boolean
        If String.IsNullOrWhiteSpace(candidateName) OrElse String.IsNullOrWhiteSpace(requestedDevice) Then
            Return False
        End If

        Return String.Equals(candidateName, requestedDevice, StringComparison.OrdinalIgnoreCase) OrElse
            candidateName.IndexOf(requestedDevice, StringComparison.OrdinalIgnoreCase) >= 0
    End Function

    Private Async Function PumpErrorAsync(process As Process, prefix As String, cancellationToken As CancellationToken) As Task
        If process Is Nothing Then
            Return
        End If

        Try
            While Not cancellationToken.IsCancellationRequested
                Dim line = Await process.StandardError.ReadLineAsync(cancellationToken)

                If line Is Nothing Then
                    Exit While
                End If

                If Not String.IsNullOrWhiteSpace(line) Then
                    RaiseEvent LogReceived($"{prefix}: {line}")
                End If
            End While
        Catch ex As OperationCanceledException
        Catch ex As ObjectDisposedException
        End Try
    End Function

    Private Async Function PumpAudioAsync(activeOutput As IDeckLinkOutput_v14_2_1, process As Process, cancellationToken As CancellationToken) As Task
        Dim bytesPerSampleFrame = AudioChannels * AudioBytesPerSample
        Dim managedBuffer(AudioChunkSampleFrames * bytesPerSampleFrame - 1) As Byte
        Dim unmanagedBuffer = Marshal.AllocHGlobal(managedBuffer.Length)

        Try
            While Not cancellationToken.IsCancellationRequested
                Dim bytesRead = Await ReadAlignedAudioBlockAsync(process.StandardOutput.BaseStream, managedBuffer, bytesPerSampleFrame, cancellationToken)

                If bytesRead <= 0 Then
                    Exit While
                End If

                Dim sampleFrameCount = bytesRead \ bytesPerSampleFrame
                Marshal.Copy(managedBuffer, 0, unmanagedBuffer, bytesRead)
                Await WriteAudioSamplesFullyAsync(activeOutput, unmanagedBuffer, sampleFrameCount, bytesPerSampleFrame, cancellationToken)
            End While
        Catch ex As OperationCanceledException
        Catch ex As ObjectDisposedException
        Finally
            Marshal.FreeHGlobal(unmanagedBuffer)
        End Try
    End Function

    Private Async Function WriteAudioSamplesFullyAsync(activeOutput As IDeckLinkOutput_v14_2_1, buffer As IntPtr, sampleFrameCount As Integer, bytesPerSampleFrame As Integer, cancellationToken As CancellationToken) As Task
        Dim totalWritten As UInteger = 0UI
        Dim zeroWriteWait = Stopwatch.StartNew()
        Dim loggedStall = False

        While totalWritten < CUInt(sampleFrameCount)
            cancellationToken.ThrowIfCancellationRequested()

            Dim remaining = CUInt(sampleFrameCount) - totalWritten
            Dim written As UInteger = 0UI

            SyncLock outputLock
                activeOutput.WriteAudioSamplesSync(IntPtr.Add(buffer, CInt(totalWritten) * bytesPerSampleFrame), remaining, written)
            End SyncLock

            If written > 0UI Then
                totalWritten += written
                zeroWriteWait.Restart()
                loggedStall = False
            Else
                If Not loggedStall AndAlso zeroWriteWait.ElapsedMilliseconds >= AudioWriteStallLogMilliseconds Then
                    RaiseEvent LogReceived($"sdk_audio_wait remaining={remaining}")
                    loggedStall = True
                End If

                Await Task.Delay(AudioWriteZeroRetryDelayMs, cancellationToken)
            End If
        End While
    End Function

    Private Shared Async Function ReadExactFrameAsync(stream As Stream, buffer As Byte(), cancellationToken As CancellationToken) As Task(Of Boolean)
        Dim offset = 0

        While offset < buffer.Length
            Dim bytesRead = Await stream.ReadAsync(buffer, offset, buffer.Length - offset, cancellationToken)

            If bytesRead = 0 Then
                Return False
            End If

            offset += bytesRead
        End While

        Return True
    End Function

    Private Shared Function ReadExactFrame(stream As Stream, buffer As Byte(), cancellationToken As CancellationToken) As Boolean
        Dim offset = 0

        While offset < buffer.Length
            cancellationToken.ThrowIfCancellationRequested()
            Dim bytesRead = stream.Read(buffer, offset, buffer.Length - offset)

            If bytesRead = 0 Then
                Return False
            End If

            offset += bytesRead
        End While

        Return True
    End Function

    Private Shared Async Function ReadAlignedAudioBlockAsync(stream As Stream, buffer As Byte(), bytesPerSampleFrame As Integer, cancellationToken As CancellationToken) As Task(Of Integer)
        Dim bytesRead = Await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken)

        While bytesRead > 0 AndAlso bytesRead Mod bytesPerSampleFrame <> 0 AndAlso bytesRead < buffer.Length
            Dim needed = Math.Min(bytesPerSampleFrame - (bytesRead Mod bytesPerSampleFrame), buffer.Length - bytesRead)
            Dim extra = Await stream.ReadAsync(buffer, bytesRead, needed, cancellationToken)

            If extra = 0 Then
                Exit While
            End If

            bytesRead += extra
        End While

        Return bytesRead - (bytesRead Mod bytesPerSampleFrame)
    End Function

    Private Shared Async Function WaitForProcessExitAsync(process As Process) As Task
        If process Is Nothing Then
            Return
        End If

        Try
            Await process.WaitForExitAsync(CancellationToken.None)
        Catch
        End Try
    End Function

    Private Shared Async Function IgnoreCancellationAsync(task As Task) As Task
        If task Is Nothing Then
            Return
        End If

        Try
            Await task
        Catch ex As OperationCanceledException
        Catch ex As ObjectDisposedException
        End Try
    End Function

    Private Shared Sub TryKill(process As Process)
        If process Is Nothing Then
            Return
        End If

        Try
            If Not process.HasExited Then
                process.Kill(entireProcessTree:=True)
            End If
        Catch
        End Try
    End Sub

    Private Shared Sub DisposeProcess(process As Process)
        If process Is Nothing Then
            Return
        End If

        Try
            process.Dispose()
        Catch
        End Try
    End Sub

    Private Shared Function IsImageFile(filePath As String) As Boolean
        Return ImageExtensions.Contains(Path.GetExtension(filePath))
    End Function

    Private Shared Function NormalizeRateString(frameRate As String) As String
        If String.IsNullOrWhiteSpace(frameRate) Then
            Return "25"
        End If

        Dim value = frameRate.Trim()

        If value.Contains("/"c) Then
            Return value
        End If

        Dim numericValue As Double
        If Double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, numericValue) Then
            Dim roundedValue = Math.Round(numericValue)

            If Math.Abs(numericValue - roundedValue) < 0.0001R Then
                Return CInt(roundedValue).ToString(CultureInfo.InvariantCulture)
            End If

            Return numericValue.ToString("0.###", CultureInfo.InvariantCulture)
        End If

        Return value
    End Function

    Private Shared Function FormatFfmpegTimestamp(value As TimeSpan) As String
        If value < TimeSpan.Zero Then
            value = TimeSpan.Zero
        End If

        Return value.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)
    End Function

    Private Shared Sub ReleaseCom(value As Object)
        If value IsNot Nothing AndAlso Marshal.IsComObject(value) Then
            Marshal.ReleaseComObject(value)
        End If
    End Sub

    Private Sub ThrowIfDisposed()
        If disposed Then
            Throw New ObjectDisposedException(NameOf(InProcessDeckLinkOutputRunner))
        End If
    End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        If disposed Then
            Return
        End If

        disposed = True
        StopPlayback(disableVideoOutput:=True)
    End Sub
End Class
