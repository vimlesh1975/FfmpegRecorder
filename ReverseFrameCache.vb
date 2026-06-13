Imports System.Diagnostics
Imports System.Globalization
Imports System.IO
Imports System.Threading
Imports System.Threading.Tasks

Friend NotInheritable Class ReverseFrameCache
    Implements IDisposable

    Private Const MaxCacheFrames As Integer = 90
    Private Const MinCacheFrames As Integer = 8
    Private Const MaxParallelPrefetchBlocks As Integer = 3
    Private Const MaxTotalCacheBytes As Long = 1024L * 1024L * 1024L
    Private Shared ReadOnly PrefetchSwitchWait As TimeSpan = TimeSpan.FromMilliseconds(80)
    Private Shared ReadOnly BlockBoundaryTolerance As TimeSpan = TimeSpan.FromMilliseconds(2)

    Private ReadOnly ffmpegPath As String
    Private ReadOnly inputPath As String
    Private ReadOnly width As Integer
    Private ReadOnly height As Integer
    Private ReadOnly frameBytes As Integer
    Private ReadOnly pixelFormat As String
    Private ReadOnly isInterlaced As Boolean
    Private ReadOnly cacheFrameInterval As TimeSpan
    Private ReadOnly cacheFrameStep As Integer
    Private ReadOnly cacheFrameCount As Integer
    Private ReadOnly prefetchBlockTarget As Integer
    Private ReadOnly fastReverseDecode As Boolean
    Private ReadOnly logLine As Action(Of String)
    Private ReadOnly prefetchGate As New Object()
    Private ReadOnly prefetchedBlocks As New List(Of ReverseFrameBlock)()
    Private ReadOnly prefetchJobs As New List(Of PrefetchJob)()
    Private currentBlock As ReverseFrameBlock
    Private disposed As Boolean

    Public Sub New(ffmpegPath As String, inputPath As String, width As Integer, height As Integer, bytesPerPixel As Integer, pixelFormat As String, isInterlaced As Boolean, frameDuration As TimeSpan, playbackSpeed As Double, logLine As Action(Of String))
        If frameDuration <= TimeSpan.Zero Then
            Throw New ArgumentOutOfRangeException(NameOf(frameDuration), "Frame duration must be positive.")
        End If

        Me.ffmpegPath = ffmpegPath
        Me.inputPath = inputPath
        Me.width = width
        Me.height = height
        Me.frameBytes = Math.Max(1, width * height * bytesPerPixel)
        Me.pixelFormat = pixelFormat
        Me.isInterlaced = isInterlaced
        Me.cacheFrameStep = GetCacheFrameStep(playbackSpeed)
        Me.fastReverseDecode = cacheFrameStep >= 10
        Me.cacheFrameInterval = TimeSpan.FromTicks(frameDuration.Ticks * cacheFrameStep)
        Me.prefetchBlockTarget = GetPrefetchBlockTarget(cacheFrameStep)
        Me.logLine = logLine

        Dim activeBlockCount = 1 + prefetchBlockTarget
        Dim maxBlockBytes = Math.Max(CLng(Me.frameBytes) * MinCacheFrames, MaxTotalCacheBytes \ activeBlockCount)
        Dim memoryLimitedFrames = CInt(Math.Max(MinCacheFrames, Math.Min(MaxCacheFrames, maxBlockBytes \ Math.Max(1, Me.frameBytes))))
        Me.cacheFrameCount = Math.Max(MinCacheFrames, Math.Min(MaxCacheFrames, Math.Min(memoryLimitedFrames, GetMaxFramesForSpeed(cacheFrameStep))))
    End Sub

    Public Function GetFrame(target As TimeSpan, cancellationToken As CancellationToken) As ReverseDecodedFrame
        ThrowIfDisposed()
        cancellationToken.ThrowIfCancellationRequested()

        If target < TimeSpan.Zero Then
            target = TimeSpan.Zero
        End If

        If currentBlock Is Nothing OrElse Not currentBlock.Contains(target, cacheFrameInterval) Then
            If Not TryUsePrefetchedBlock(target, cancellationToken) Then
                ClearPrefetch()
                currentBlock = DecodeBlockEndingAt(target, cancellationToken)
                If logLine IsNot Nothing Then
                    logLine($"Reverse cache loaded {currentBlock.Frames.Count} frame(s), step {cacheFrameStep}, {FormatClock(currentBlock.Start)} to {FormatClock(currentBlock.EndPosition)}.")
                End If
            End If
        End If

        StartPrefetchPreviousBlocks()
        Return currentBlock.GetNearestFrame(target, cacheFrameInterval)
    End Function

    Private Function TryUsePrefetchedBlock(target As TimeSpan, cancellationToken As CancellationToken) As Boolean
        Dim jobToWait As PrefetchJob = Nothing

        SyncLock prefetchGate
            HarvestCompletedPrefetchUnderLock()

            Dim block As ReverseFrameBlock = Nothing
            If TryTakePrefetchedBlockUnderLock(target, block) Then
                currentBlock = block
                LogPrefetchSwitch("switched to", block)
                Return True
            End If

            If cacheFrameStep >= 10 AndAlso TryTakeOlderPrefetchedBlockUnderLock(target, block) Then
                currentBlock = block
                LogPrefetchSwitch("skipped to", block)
                Return True
            End If

            If cacheFrameStep >= 10 AndAlso TryTakeAnyUsefulPrefetchedBlockUnderLock(target, block) Then
                currentBlock = block
                LogPrefetchSwitch("jumped to", block)
                Return True
            End If

            For Each job In prefetchJobs
                If TargetCouldUseBlockEndingAt(target, job.BlockEnd) Then
                    jobToWait = job
                    Exit For
                End If
            Next
        End SyncLock

        If jobToWait Is Nothing Then
            Return False
        End If

        Try
            If Not jobToWait.Work.Wait(PrefetchSwitchWait, cancellationToken) Then
                SyncLock prefetchGate
                    HarvestCompletedPrefetchUnderLock()

                    Dim lateBlock As ReverseFrameBlock = Nothing
                    If cacheFrameStep >= 10 AndAlso TryTakeOlderPrefetchedBlockUnderLock(target, lateBlock) Then
                        currentBlock = lateBlock
                        LogPrefetchSwitch("caught late", lateBlock)
                        Return True
                    End If

                    If cacheFrameStep >= 10 AndAlso TryTakeAnyUsefulPrefetchedBlockUnderLock(target, lateBlock) Then
                        currentBlock = lateBlock
                        LogPrefetchSwitch("jumped to", lateBlock)
                        Return True
                    End If
                End SyncLock

                If logLine IsNot Nothing Then
                    logLine("Reverse cache prefetch not ready; decoding next block inline.")
                End If

                Return False
            End If
        Catch ex As OperationCanceledException
            Throw
        Catch ex As Exception
            RemovePrefetchJob(jobToWait)
            If logLine IsNot Nothing Then
                logLine($"Reverse cache prefetch skipped: {ex.GetBaseException().Message}")
            End If
            Return False
        End Try

        SyncLock prefetchGate
            HarvestCompletedPrefetchUnderLock()

            Dim block As ReverseFrameBlock = Nothing
            If TryTakePrefetchedBlockUnderLock(target, block) Then
                currentBlock = block
                LogPrefetchSwitch("switched to", block)
                Return True
            End If

            If cacheFrameStep >= 10 AndAlso TryTakeOlderPrefetchedBlockUnderLock(target, block) Then
                currentBlock = block
                LogPrefetchSwitch("skipped to", block)
                Return True
            End If

            If cacheFrameStep >= 10 AndAlso TryTakeAnyUsefulPrefetchedBlockUnderLock(target, block) Then
                currentBlock = block
                LogPrefetchSwitch("jumped to", block)
                Return True
            End If
        End SyncLock

        Return False
    End Function

    Private Function TryTakePrefetchedBlockUnderLock(target As TimeSpan, ByRef block As ReverseFrameBlock) As Boolean
        For index = 0 To prefetchedBlocks.Count - 1
            Dim candidate = prefetchedBlocks(index)
            If candidate.Contains(target, cacheFrameInterval) Then
                block = candidate
                prefetchedBlocks.RemoveRange(0, index + 1)
                Return True
            End If
        Next

        block = Nothing
        Return False
    End Function

    Private Function TryTakeOlderPrefetchedBlockUnderLock(target As TimeSpan, ByRef block As ReverseFrameBlock) As Boolean
        For index = 0 To prefetchedBlocks.Count - 1
            Dim candidate = prefetchedBlocks(index)
            If candidate.EndPosition < target Then
                block = candidate
                prefetchedBlocks.RemoveRange(0, index + 1)
                Return True
            End If
        Next

        block = Nothing
        Return False
    End Function

    Private Function TryTakeAnyUsefulPrefetchedBlockUnderLock(target As TimeSpan, ByRef block As ReverseFrameBlock) As Boolean
        For index = 0 To prefetchedBlocks.Count - 1
            Dim candidate = prefetchedBlocks(index)
            If candidate.Start < target Then
                block = candidate
                prefetchedBlocks.RemoveRange(0, index + 1)
                Return True
            End If
        Next

        block = Nothing
        Return False
    End Function

    Private Sub StartPrefetchPreviousBlocks()
        If currentBlock Is Nothing OrElse currentBlock.Start <= TimeSpan.Zero Then
            Return
        End If

        SyncLock prefetchGate
            HarvestCompletedPrefetchUnderLock()

            While prefetchedBlocks.Count + prefetchJobs.Count < prefetchBlockTarget
                Dim prefetchEnd = GetNextPrefetchEndUnderLock()

                If prefetchEnd <= TimeSpan.Zero AndAlso HasBlockOrJobEndingAt(TimeSpan.Zero) Then
                    Return
                End If

                If HasBlockOrJobEndingAt(prefetchEnd) Then
                    Return
                End If

                Dim cancellation As New CancellationTokenSource()
                Dim token = cancellation.Token
                Dim prefetchEndCopy = prefetchEnd
                Dim work = Task.Run(Function() DecodeBlockEndingAt(prefetchEndCopy, token), token)
                prefetchJobs.Add(New PrefetchJob(prefetchEndCopy, work, cancellation))
            End While
        End SyncLock
    End Sub

    Private Sub HarvestCompletedPrefetchUnderLock()
        For index = prefetchJobs.Count - 1 To 0 Step -1
            Dim job = prefetchJobs(index)

            If Not job.Work.IsCompleted Then
                Continue For
            End If

            prefetchJobs.RemoveAt(index)
            job.Cancellation.Dispose()

            Try
                Dim block = job.Work.GetAwaiter().GetResult()
                prefetchedBlocks.Add(block)
                prefetchedBlocks.Sort(Function(left, right) right.EndPosition.CompareTo(left.EndPosition))

                If logLine IsNot Nothing Then
                    logLine($"Reverse cache prefetched {block.Frames.Count} frame(s), step {cacheFrameStep}, {FormatClock(block.Start)} to {FormatClock(block.EndPosition)}. Queue {prefetchedBlocks.Count}/{prefetchBlockTarget}, running {prefetchJobs.Count}.")
                End If
            Catch ex As OperationCanceledException
            Catch ex As Exception
                If logLine IsNot Nothing Then
                    logLine($"Reverse cache prefetch skipped: {ex.GetBaseException().Message}")
                End If
            End Try
        Next
    End Sub

    Private Function GetNextPrefetchEndUnderLock() As TimeSpan
        Dim baseStart = GetOldestQueuedStartUnderLock()
        Dim endTicks = baseStart.Ticks - cacheFrameInterval.Ticks
        Return If(endTicks > 0, TimeSpan.FromTicks(endTicks), TimeSpan.Zero)
    End Function

    Private Function GetOldestQueuedStartUnderLock() As TimeSpan
        Dim oldest = If(currentBlock IsNot Nothing, currentBlock.Start, TimeSpan.Zero)

        For Each block In prefetchedBlocks
            If block.Start < oldest Then
                oldest = block.Start
            End If
        Next

        For Each job In prefetchJobs
            Dim jobStart = GetBlockStartForEnd(job.BlockEnd)
            If jobStart < oldest Then
                oldest = jobStart
            End If
        Next

        Return oldest
    End Function

    Private Function HasBlockOrJobEndingAt(blockEnd As TimeSpan) As Boolean
        For Each block In prefetchedBlocks
            If NearlyEqual(block.EndPosition, blockEnd) Then
                Return True
            End If
        Next

        For Each job In prefetchJobs
            If NearlyEqual(job.BlockEnd, blockEnd) Then
                Return True
            End If
        Next

        Return False
    End Function

    Private Sub RemovePrefetchJob(job As PrefetchJob)
        SyncLock prefetchGate
            If prefetchJobs.Remove(job) Then
                job.Cancellation.Cancel()
                job.Cancellation.Dispose()
            End If
        End SyncLock
    End Sub

    Private Function TargetCouldUseBlockEndingAt(target As TimeSpan, blockEnd As TimeSpan) As Boolean
        Dim expectedStart = GetBlockStartForEnd(blockEnd)
        Return target >= expectedStart - BlockBoundaryTolerance AndAlso target <= blockEnd + BlockBoundaryTolerance
    End Function

    Private Function DecodeBlockEndingAt(target As TimeSpan, cancellationToken As CancellationToken) As ReverseFrameBlock
        Dim blockStart = GetBlockStartForEnd(target)
        Dim availableTicks = Math.Max(0L, target.Ticks - blockStart.Ticks)
        Dim frameCount = Math.Max(1, Math.Min(cacheFrameCount, CInt(availableTicks \ cacheFrameInterval.Ticks) + 1))
        Dim decodedFrames = DecodeFrames(blockStart, frameCount, cancellationToken)

        If decodedFrames.Count = 0 Then
            Throw New InvalidOperationException("No reverse cache frames decoded.")
        End If

        Return New ReverseFrameBlock(decodedFrames)
    End Function

    Private Function DecodeFrames(startOffset As TimeSpan, frameCount As Integer, cancellationToken As CancellationToken) As List(Of ReverseDecodedFrame)
        Dim frames As New List(Of ReverseDecodedFrame)(frameCount)
        Dim process = StartDecoder(BuildDecoderArguments(startOffset, frameCount))

        Try
            Using cancellationToken.Register(Sub() TryKillProcess(process))
                Dim stderrTask = process.StandardError.ReadToEndAsync(cancellationToken)

                For frameIndex = 0 To frameCount - 1
                    cancellationToken.ThrowIfCancellationRequested()
                    Dim buffer(frameBytes - 1) As Byte

                    If Not ReadExact(process.StandardOutput.BaseStream, buffer, cancellationToken) Then
                        Exit For
                    End If

                    frames.Add(New ReverseDecodedFrame(startOffset + TimeSpan.FromTicks(cacheFrameInterval.Ticks * CLng(frameIndex)), buffer))
                Next

                Dim waitStartedAt = DateTime.UtcNow
                While Not process.WaitForExit(25)
                    cancellationToken.ThrowIfCancellationRequested()

                    If DateTime.UtcNow - waitStartedAt > TimeSpan.FromSeconds(5) Then
                        TryKillProcess(process)
                        Exit While
                    End If
                End While

                Try
                    stderrTask.GetAwaiter().GetResult()
                Catch ex As OperationCanceledException
                End Try
            End Using
        Finally
            TryKillProcess(process)
            process.Dispose()
        End Try

        Return frames
    End Function

    Private Function BuildDecoderArguments(startOffset As TimeSpan, frameCount As Integer) As IReadOnlyList(Of String)
        Dim args As New List(Of String) From {
            "-hide_banner",
            "-loglevel",
            "warning",
            "-nostats"
        }

        If fastReverseDecode Then
            args.Add("-skip_frame")
            args.Add("noref")
        End If

        If startOffset > TimeSpan.Zero Then
            args.Add("-ss")
            args.Add(FormatFfmpegTimestamp(startOffset))
        End If

        args.Add("-i")
        args.Add(inputPath)
        args.Add("-map")
        args.Add("0:v:0")
        args.Add("-an")
        args.Add("-vf")
        args.Add(BuildVideoFilter())
        args.Add("-frames:v")
        args.Add(Math.Max(1, frameCount).ToString(CultureInfo.InvariantCulture))
        args.Add("-pix_fmt")
        args.Add(pixelFormat)
        args.Add("-f")
        args.Add("rawvideo")
        args.Add("pipe:1")
        Return args
    End Function

    Private Function BuildVideoFilter() As String
        Dim rate = FormatFilterNumber(1.0R / cacheFrameInterval.TotalSeconds)
        Dim parts As New List(Of String) From {
            $"scale={width}:{height}:force_original_aspect_ratio=decrease",
            $"pad={width}:{height}:(ow-iw)/2:(oh-ih)/2",
            "setsar=1",
            $"fps={rate}:start_time=0",
            $"setpts=N/({rate}*TB)"
        }

        If isInterlaced Then
            parts.Add("setfield=tff")
        End If

        parts.Add($"format={pixelFormat}")
        Return String.Join(",", parts)
    End Function

    Private Function StartDecoder(arguments As IReadOnlyList(Of String)) As Process
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
            Throw New InvalidOperationException("FFmpeg reverse cache decoder could not be started.")
        End If

        Return process
    End Function

    Private Function GetBlockStartForEnd(target As TimeSpan) As TimeSpan
        Dim requestedStartTicks = target.Ticks - (cacheFrameInterval.Ticks * Math.Max(0, cacheFrameCount - 1))
        Return If(requestedStartTicks > 0, TimeSpan.FromTicks(requestedStartTicks), TimeSpan.Zero)
    End Function

    Private Shared Function GetCacheFrameStep(playbackSpeed As Double) As Integer
        Dim speed = Math.Abs(playbackSpeed)

        If Double.IsNaN(speed) OrElse Double.IsInfinity(speed) OrElse speed < 2.0R Then
            Return 1
        End If

        Return Math.Max(1, Math.Min(20, CInt(Math.Floor(speed + 0.001R))))
    End Function

    Private Shared Function GetPrefetchBlockTarget(cacheFrameStep As Integer) As Integer
        If cacheFrameStep >= 10 Then
            Return MaxParallelPrefetchBlocks
        End If

        Return If(cacheFrameStep > 1, 2, 1)
    End Function

    Private Shared Function GetMaxFramesForSpeed(cacheFrameStep As Integer) As Integer
        If cacheFrameStep >= 20 Then
            Return 18
        End If

        If cacheFrameStep >= 10 Then
            Return 24
        End If

        If cacheFrameStep >= 5 Then
            Return 40
        End If

        Return MaxCacheFrames
    End Function

    Private Sub ClearPrefetch()
        SyncLock prefetchGate
            For Each job In prefetchJobs
                job.Cancellation.Cancel()
                job.Cancellation.Dispose()
            Next

            prefetchJobs.Clear()
            prefetchedBlocks.Clear()
        End SyncLock
    End Sub

    Private Shared Function NearlyEqual(left As TimeSpan, right As TimeSpan) As Boolean
        Return Math.Abs((left - right).Ticks) <= TimeSpan.TicksPerMillisecond
    End Function

    Private Sub LogPrefetchSwitch(action As String, block As ReverseFrameBlock)
        If logLine IsNot Nothing Then
            logLine($"Reverse cache {action} prefetched block {FormatClock(block.Start)} to {FormatClock(block.EndPosition)}.")
        End If
    End Sub

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

    Private Shared Function FormatFfmpegTimestamp(value As TimeSpan) As String
        Return value.TotalSeconds.ToString("0.000", CultureInfo.InvariantCulture)
    End Function

    Private Shared Function FormatFilterNumber(value As Double) As String
        Return Math.Max(0.001R, Math.Abs(value)).ToString("0.###", CultureInfo.InvariantCulture)
    End Function

    Private Shared Function FormatClock(value As TimeSpan) As String
        If value < TimeSpan.Zero Then
            value = TimeSpan.Zero
        End If

        Return value.ToString("hh\:mm\:ss\.fff", CultureInfo.InvariantCulture)
    End Function

    Private Shared Sub TryKillProcess(process As Process)
        Try
            If process IsNot Nothing AndAlso Not process.HasExited Then
                process.Kill(True)
            End If
        Catch
        End Try
    End Sub

    Private Sub ThrowIfDisposed()
        If disposed Then
            Throw New ObjectDisposedException(NameOf(ReverseFrameCache))
        End If
    End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        If disposed Then
            Return
        End If

        disposed = True
        ClearPrefetch()
        currentBlock = Nothing
    End Sub

    Private NotInheritable Class ReverseFrameBlock
        Public Sub New(frames As List(Of ReverseDecodedFrame))
            If frames Is Nothing OrElse frames.Count = 0 Then
                Throw New ArgumentException("Reverse frame block cannot be empty.", NameOf(frames))
            End If

            Me.Frames = frames
            Me.Start = frames(0).Position
            Me.EndPosition = frames(frames.Count - 1).Position
        End Sub

        Public ReadOnly Property Frames As List(Of ReverseDecodedFrame)
        Public ReadOnly Property Start As TimeSpan
        Public ReadOnly Property EndPosition As TimeSpan

        Public Function Contains(target As TimeSpan, frameInterval As TimeSpan) As Boolean
            Return target >= Start - BlockBoundaryTolerance AndAlso target <= EndPosition + BlockBoundaryTolerance
        End Function

        Public Function GetNearestFrame(target As TimeSpan, frameInterval As TimeSpan) As ReverseDecodedFrame
            Dim index = CInt(Math.Round((target - Start).Ticks / CDbl(frameInterval.Ticks), MidpointRounding.AwayFromZero))
            index = Math.Max(0, Math.Min(Frames.Count - 1, index))
            Return Frames(index)
        End Function
    End Class

    Private NotInheritable Class PrefetchJob
        Public Sub New(blockEnd As TimeSpan, work As Task(Of ReverseFrameBlock), cancellation As CancellationTokenSource)
            Me.BlockEnd = blockEnd
            Me.Work = work
            Me.Cancellation = cancellation
        End Sub

        Public ReadOnly Property BlockEnd As TimeSpan
        Public ReadOnly Property Work As Task(Of ReverseFrameBlock)
        Public ReadOnly Property Cancellation As CancellationTokenSource
    End Class
End Class

Friend NotInheritable Class ReverseDecodedFrame
    Public Sub New(position As TimeSpan, data As Byte())
        Me.Position = position
        Me.Data = data
    End Sub

    Public ReadOnly Property Position As TimeSpan
    Public ReadOnly Property Data As Byte()
End Class
