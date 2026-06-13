Imports System.Diagnostics
Imports System.Globalization
Imports System.IO
Imports System.Threading
Imports System.Threading.Tasks

Friend NotInheritable Class ScrubPreviewFrameCache
    Implements IDisposable

    Private Const ImmediateFramesBehind As Integer = 4
    Private Const ImmediateFramesAhead As Integer = 12
    Private Const PrefetchFramesBehind As Integer = 36
    Private Const PrefetchFramesAhead As Integer = 72
    Private Const MaxParallelPrefetchJobs As Integer = 2
    Private Const MaxCacheBytes As Long = 512L * 1024L * 1024L

    Private ReadOnly ffmpegPath As String
    Private ReadOnly inputPath As String
    Private ReadOnly width As Integer
    Private ReadOnly height As Integer
    Private ReadOnly frameBytes As Integer
    Private ReadOnly pixelFormat As String
    Private ReadOnly isInterlaced As Boolean
    Private ReadOnly frameInterval As TimeSpan
    Private ReadOnly duration As TimeSpan?
    Private ReadOnly maxCacheFrames As Integer
    Private ReadOnly logLine As Action(Of String)
    Private ReadOnly keyframeIndex As ScrubGopIndex
    Private ReadOnly cacheGate As New Object()
    Private ReadOnly cachedFrames As New Dictionary(Of Long, LinkedListNode(Of CacheEntry))()
    Private ReadOnly cacheOrder As New LinkedList(Of CacheEntry)()
    Private ReadOnly prefetchJobs As New List(Of PrefetchJob)()
    Private cacheBytes As Long
    Private disposed As Boolean

    Public Sub New(ffmpegPath As String, inputPath As String, width As Integer, height As Integer, bytesPerPixel As Integer, pixelFormat As String, isInterlaced As Boolean, frameInterval As TimeSpan, duration As TimeSpan?, logLine As Action(Of String))
        If frameInterval <= TimeSpan.Zero Then
            Throw New ArgumentOutOfRangeException(NameOf(frameInterval), "Frame interval must be positive.")
        End If

        Me.ffmpegPath = ffmpegPath
        Me.inputPath = inputPath
        Me.width = width
        Me.height = height
        Me.frameBytes = Math.Max(1, width * height * bytesPerPixel)
        Me.pixelFormat = pixelFormat
        Me.isInterlaced = isInterlaced
        Me.frameInterval = frameInterval
        Me.duration = duration
        Me.maxCacheFrames = Math.Max(ImmediateFramesAhead + ImmediateFramesBehind + 1, CInt(Math.Max(1L, MaxCacheBytes \ Math.Max(1, Me.frameBytes))))
        Me.logLine = logLine

        Dim ffprobePath = Path.Combine(Path.GetDirectoryName(ffmpegPath), "ffprobe.exe")
        If File.Exists(ffprobePath) AndAlso Not IsImageFile(inputPath) Then
            keyframeIndex = New ScrubGopIndex(ffprobePath, inputPath, logLine)
        End If
    End Sub

    Public ReadOnly Property SourcePath As String
        Get
            Return inputPath
        End Get
    End Property

    Public ReadOnly Property ProxyWidth As Integer
        Get
            Return width
        End Get
    End Property

    Public ReadOnly Property ProxyHeight As Integer
        Get
            Return height
        End Get
    End Property

    Public Function Matches(filePath As String, width As Integer, height As Integer, pixelFormat As String) As Boolean
        Return String.Equals(inputPath, filePath, StringComparison.OrdinalIgnoreCase) AndAlso
            Me.width = width AndAlso
            Me.height = height AndAlso
            String.Equals(Me.pixelFormat, pixelFormat, StringComparison.OrdinalIgnoreCase)
    End Function

    Public Function GetPreviewRun(target As TimeSpan, direction As Integer, requestedFrameCount As Integer, cancellationToken As CancellationToken) As List(Of ScrubPreviewFrame)
        ThrowIfDisposed()
        cancellationToken.ThrowIfCancellationRequested()

        Dim centerIndex = ClampFrameIndex(TimeToFrameIndex(target))
        Dim runDirection = If(direction < 0, -1, 1)
        Dim centerFrame As ScrubPreviewFrame = Nothing

        If Not TryGetCachedFrame(centerIndex, centerFrame) Then
            DecodeWindowForTarget(centerIndex, cancellationToken)

            If Not TryGetCachedFrame(centerIndex, centerFrame) Then
                centerFrame = FindNearestCachedFrame(centerIndex, ImmediateFramesAhead + ImmediateFramesBehind + 1)
            End If
        End If

        If centerFrame Is Nothing Then
            Throw New InvalidOperationException("No scrub preview frame decoded.")
        End If

        StartPrefetchAround(centerIndex, runDirection)

        Dim frames As New List(Of ScrubPreviewFrame)(Math.Max(1, requestedFrameCount)) From {
            centerFrame
        }

        For offset = 1 To Math.Max(1, requestedFrameCount) - 1
            Dim frameIndex = ClampFrameIndex(centerIndex + (CLng(runDirection) * offset))
            Dim frame As ScrubPreviewFrame = Nothing

            If TryGetCachedFrame(frameIndex, frame) Then
                frames.Add(frame)
            End If
        Next

        Return frames
    End Function

    Private Sub DecodeWindowForTarget(centerIndex As Long, cancellationToken As CancellationToken)
        Dim startIndex = Math.Max(0L, centerIndex - ImmediateFramesBehind)
        Dim endIndex = ClampFrameIndex(centerIndex + ImmediateFramesAhead)
        DecodeBlock(startIndex, CInt(Math.Max(1L, endIndex - startIndex + 1L)), cancellationToken, force:=True)
    End Sub

    Private Sub DecodeBlock(startIndex As Long, frameCount As Integer, cancellationToken As CancellationToken, Optional force As Boolean = False)
        ThrowIfDisposed()
        cancellationToken.ThrowIfCancellationRequested()

        If frameCount <= 0 OrElse (Not force AndAlso IsBlockMostlyCached(startIndex, frameCount)) Then
            Return
        End If

        Dim safeStartIndex = ClampFrameIndex(startIndex)
        Dim safeEndIndex = ClampFrameIndex(safeStartIndex + frameCount - 1L)
        Dim safeFrameCount = CInt(Math.Max(1L, safeEndIndex - safeStartIndex + 1L))
        Dim startOffset = FrameIndexToTime(safeStartIndex)
        Dim keyframeOffset As TimeSpan? = Nothing

        If keyframeIndex IsNot Nothing Then
            keyframeOffset = keyframeIndex.FindPreviousKeyframe(startOffset)
        End If

        Dim decodedIndex = safeStartIndex

        SeekFrameDecoder.DecodeFrameBurst(
            ffmpegPath,
            inputPath,
            width,
            height,
            Math.Max(1, frameBytes \ Math.Max(1, width * height)),
            pixelFormat,
            isInterlaced,
            startOffset,
            safeFrameCount,
            frameInterval,
            Sub(decodedFrame)
                cancellationToken.ThrowIfCancellationRequested()
                Dim frameIndex = decodedIndex
                decodedIndex += 1
                AddOrUpdateFrame(frameIndex, New ScrubPreviewFrame(FrameIndexToTime(frameIndex), decodedFrame.Width, decodedFrame.Height, decodedFrame.PixelFormat, decodedFrame.Data))
            End Sub,
            cancellationToken,
            keyframeOffset)
    End Sub

    Private Sub StartPrefetchAround(centerIndex As Long, direction As Integer)
        If disposed Then
            Return
        End If

        SyncLock cacheGate
            HarvestPrefetchUnderLock()
            CancelDistantPrefetchUnderLock(centerIndex)

            If direction < 0 Then
                QueuePrefetchUnderLock(Math.Max(0L, centerIndex - ImmediateFramesBehind - PrefetchFramesBehind), PrefetchFramesBehind)
                QueuePrefetchUnderLock(centerIndex + ImmediateFramesAhead + 1L, PrefetchFramesAhead)
            Else
                QueuePrefetchUnderLock(centerIndex + ImmediateFramesAhead + 1L, PrefetchFramesAhead)
                QueuePrefetchUnderLock(Math.Max(0L, centerIndex - ImmediateFramesBehind - PrefetchFramesBehind), PrefetchFramesBehind)
            End If
        End SyncLock
    End Sub

    Private Sub QueuePrefetchUnderLock(startIndex As Long, frameCount As Integer)
        If prefetchJobs.Count >= MaxParallelPrefetchJobs Then
            Return
        End If

        Dim safeStartIndex = ClampFrameIndex(startIndex)
        Dim safeEndIndex = ClampFrameIndex(safeStartIndex + frameCount - 1L)
        Dim safeFrameCount = CInt(Math.Max(0L, safeEndIndex - safeStartIndex + 1L))

        If safeFrameCount <= 0 OrElse IsBlockMostlyCachedUnderLock(safeStartIndex, safeFrameCount) OrElse HasOverlappingPrefetchUnderLock(safeStartIndex, safeFrameCount) Then
            Return
        End If

        Dim cancellation As New CancellationTokenSource()
        Dim token = cancellation.Token
        Dim queuedStart = safeStartIndex
        Dim queuedCount = safeFrameCount
        Dim work = Task.Run(
            Sub()
                DecodeBlock(queuedStart, queuedCount, token)
            End Sub,
            token)
        prefetchJobs.Add(New PrefetchJob(queuedStart, queuedCount, work, cancellation))
    End Sub

    Private Sub HarvestPrefetchUnderLock()
        For index = prefetchJobs.Count - 1 To 0 Step -1
            Dim job = prefetchJobs(index)

            If Not job.Work.IsCompleted Then
                Continue For
            End If

            prefetchJobs.RemoveAt(index)
            job.Cancellation.Dispose()

            Try
                job.Work.GetAwaiter().GetResult()
            Catch ex As OperationCanceledException
            Catch ex As Exception
                If logLine IsNot Nothing AndAlso Not disposed Then
                    logLine($"Scrub cache prefetch skipped: {ex.GetBaseException().Message}")
                End If
            End Try
        Next
    End Sub

    Private Sub CancelDistantPrefetchUnderLock(centerIndex As Long)
        Dim keepDistance = ImmediateFramesBehind + ImmediateFramesAhead + PrefetchFramesBehind + PrefetchFramesAhead

        For index = prefetchJobs.Count - 1 To 0 Step -1
            Dim job = prefetchJobs(index)
            Dim jobEnd = job.StartIndex + Math.Max(0, job.FrameCount - 1)

            If jobEnd < centerIndex - keepDistance OrElse job.StartIndex > centerIndex + keepDistance Then
                prefetchJobs.RemoveAt(index)
                job.Cancellation.Cancel()
                job.Cancellation.Dispose()
            End If
        Next
    End Sub

    Private Function HasOverlappingPrefetchUnderLock(startIndex As Long, frameCount As Integer) As Boolean
        Dim endIndex = startIndex + Math.Max(0, frameCount - 1)

        For Each job In prefetchJobs
            Dim jobEnd = job.StartIndex + Math.Max(0, job.FrameCount - 1)
            If startIndex <= jobEnd AndAlso endIndex >= job.StartIndex Then
                Return True
            End If
        Next

        Return False
    End Function

    Private Function IsBlockMostlyCached(startIndex As Long, frameCount As Integer) As Boolean
        SyncLock cacheGate
            Return IsBlockMostlyCachedUnderLock(startIndex, frameCount)
        End SyncLock
    End Function

    Private Function IsBlockMostlyCachedUnderLock(startIndex As Long, frameCount As Integer) As Boolean
        If frameCount <= 0 Then
            Return True
        End If

        Dim cachedCount = 0

        For index = 0 To frameCount - 1
            If cachedFrames.ContainsKey(ClampFrameIndex(startIndex + index)) Then
                cachedCount += 1
            End If
        Next

        Return cachedCount >= Math.Max(1, CInt(Math.Ceiling(frameCount * 0.85R)))
    End Function

    Private Function TryGetCachedFrame(frameIndex As Long, ByRef frame As ScrubPreviewFrame) As Boolean
        SyncLock cacheGate
            Dim node As LinkedListNode(Of CacheEntry) = Nothing

            If cachedFrames.TryGetValue(ClampFrameIndex(frameIndex), node) Then
                cacheOrder.Remove(node)
                cacheOrder.AddFirst(node)
                frame = node.Value.Frame
                Return True
            End If
        End SyncLock

        frame = Nothing
        Return False
    End Function

    Private Function FindNearestCachedFrame(frameIndex As Long, searchRadius As Integer) As ScrubPreviewFrame
        For offset = 1 To Math.Max(1, searchRadius)
            Dim frame As ScrubPreviewFrame = Nothing

            If TryGetCachedFrame(frameIndex - offset, frame) Then
                Return frame
            End If

            If TryGetCachedFrame(frameIndex + offset, frame) Then
                Return frame
            End If
        Next

        Return Nothing
    End Function

    Private Sub AddOrUpdateFrame(frameIndex As Long, frame As ScrubPreviewFrame)
        SyncLock cacheGate
            Dim safeFrameIndex = ClampFrameIndex(frameIndex)
            Dim node As LinkedListNode(Of CacheEntry) = Nothing

            If cachedFrames.TryGetValue(safeFrameIndex, node) Then
                cacheBytes -= node.Value.ByteCount
                node.Value.Frame = frame
                cacheBytes += node.Value.ByteCount
                cacheOrder.Remove(node)
                cacheOrder.AddFirst(node)
            Else
                Dim entry As New CacheEntry(safeFrameIndex, frame)
                node = New LinkedListNode(Of CacheEntry)(entry)
                cachedFrames(safeFrameIndex) = node
                cacheOrder.AddFirst(node)
                cacheBytes += entry.ByteCount
            End If

            TrimCacheUnderLock()
        End SyncLock
    End Sub

    Private Sub TrimCacheUnderLock()
        While cacheOrder.Count > maxCacheFrames OrElse cacheBytes > MaxCacheBytes
            Dim node = cacheOrder.Last

            If node Is Nothing Then
                Exit While
            End If

            cacheOrder.RemoveLast()
            cachedFrames.Remove(node.Value.FrameIndex)
            cacheBytes -= node.Value.ByteCount
        End While
    End Sub

    Private Function TimeToFrameIndex(value As TimeSpan) As Long
        If value <= TimeSpan.Zero Then
            Return 0
        End If

        Return CLng(Math.Round(value.Ticks / CDbl(frameInterval.Ticks), MidpointRounding.AwayFromZero))
    End Function

    Private Function FrameIndexToTime(frameIndex As Long) As TimeSpan
        Dim safeFrameIndex = ClampFrameIndex(frameIndex)
        Dim ticks = safeFrameIndex * frameInterval.Ticks

        If ticks < 0 OrElse ticks > TimeSpan.MaxValue.Ticks Then
            Return TimeSpan.MaxValue
        End If

        Return TimeSpan.FromTicks(ticks)
    End Function

    Private Function ClampFrameIndex(frameIndex As Long) As Long
        If frameIndex < 0 Then
            Return 0
        End If

        If duration.HasValue AndAlso duration.Value > TimeSpan.Zero Then
            Dim maxFrameIndex = Math.Max(0L, CLng(Math.Ceiling(duration.Value.Ticks / CDbl(frameInterval.Ticks))))
            If frameIndex > maxFrameIndex Then
                Return maxFrameIndex
            End If
        End If

        Return frameIndex
    End Function

    Private Sub ClearPrefetch()
        SyncLock cacheGate
            For Each job In prefetchJobs
                job.Cancellation.Cancel()
                job.Cancellation.Dispose()
            Next

            prefetchJobs.Clear()
        End SyncLock
    End Sub

    Private Sub ThrowIfDisposed()
        If disposed Then
            Throw New ObjectDisposedException(NameOf(ScrubPreviewFrameCache))
        End If
    End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        If disposed Then
            Return
        End If

        disposed = True
        ClearPrefetch()
        keyframeIndex?.Dispose()

        SyncLock cacheGate
            cachedFrames.Clear()
            cacheOrder.Clear()
            cacheBytes = 0
        End SyncLock
    End Sub

    Private Shared Function IsImageFile(filePath As String) As Boolean
        Dim extension = Path.GetExtension(filePath)
        Return String.Equals(extension, ".bmp", StringComparison.OrdinalIgnoreCase) OrElse
            String.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase) OrElse
            String.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase) OrElse
            String.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase)
    End Function

    Private NotInheritable Class CacheEntry
        Public Sub New(frameIndex As Long, frame As ScrubPreviewFrame)
            Me.FrameIndex = frameIndex
            Me.Frame = frame
        End Sub

        Public ReadOnly Property FrameIndex As Long
        Public Property Frame As ScrubPreviewFrame

        Public ReadOnly Property ByteCount As Long
            Get
                Return If(Frame?.Data Is Nothing, 0L, Frame.Data.LongLength)
            End Get
        End Property
    End Class

    Private NotInheritable Class PrefetchJob
        Public Sub New(startIndex As Long, frameCount As Integer, work As Task, cancellation As CancellationTokenSource)
            Me.StartIndex = startIndex
            Me.FrameCount = frameCount
            Me.Work = work
            Me.Cancellation = cancellation
        End Sub

        Public ReadOnly Property StartIndex As Long
        Public ReadOnly Property FrameCount As Integer
        Public ReadOnly Property Work As Task
        Public ReadOnly Property Cancellation As CancellationTokenSource
    End Class
End Class

Friend NotInheritable Class ScrubPreviewFrame
    Public Sub New(position As TimeSpan, width As Integer, height As Integer, pixelFormat As String, data As Byte())
        Me.Position = position
        Me.Width = width
        Me.Height = height
        Me.PixelFormat = pixelFormat
        Me.Data = data
    End Sub

    Public ReadOnly Property Position As TimeSpan
    Public ReadOnly Property Width As Integer
    Public ReadOnly Property Height As Integer
    Public ReadOnly Property PixelFormat As String
    Public ReadOnly Property Data As Byte()
End Class

Friend NotInheritable Class ScrubGopIndex
    Implements IDisposable

    Private ReadOnly indexCancellation As New CancellationTokenSource()
    Private ReadOnly keyframesTask As Task(Of List(Of TimeSpan))
    Private disposed As Boolean

    Public Sub New(ffprobePath As String, inputPath As String, logLine As Action(Of String))
        Dim token = indexCancellation.Token
        keyframesTask = Task.Run(Function() ProbeKeyframes(ffprobePath, inputPath, logLine, token), token)
    End Sub

    Public Function FindPreviousKeyframe(position As TimeSpan) As TimeSpan?
        If disposed OrElse Not keyframesTask.IsCompletedSuccessfully Then
            Return Nothing
        End If

        Dim keyframes = keyframesTask.Result

        If keyframes Is Nothing OrElse keyframes.Count = 0 Then
            Return Nothing
        End If

        Dim low = 0
        Dim high = keyframes.Count - 1
        Dim best = -1

        While low <= high
            Dim mid = low + ((high - low) \ 2)

            If keyframes(mid) <= position Then
                best = mid
                low = mid + 1
            Else
                high = mid - 1
            End If
        End While

        If best < 0 Then
            Return TimeSpan.Zero
        End If

        Return keyframes(best)
    End Function

    Private Shared Function ProbeKeyframes(ffprobePath As String, inputPath As String, logLine As Action(Of String), cancellationToken As CancellationToken) As List(Of TimeSpan)
        Dim keyframes As New List(Of TimeSpan)()

        Try
            Dim startInfo As New ProcessStartInfo() With {
                .FileName = ffprobePath,
                .UseShellExecute = False,
                .RedirectStandardOutput = True,
                .RedirectStandardError = True,
                .CreateNoWindow = True
            }

            startInfo.ArgumentList.Add("-v")
            startInfo.ArgumentList.Add("error")
            startInfo.ArgumentList.Add("-select_streams")
            startInfo.ArgumentList.Add("v:0")
            startInfo.ArgumentList.Add("-skip_frame")
            startInfo.ArgumentList.Add("nokey")
            startInfo.ArgumentList.Add("-show_entries")
            startInfo.ArgumentList.Add("frame=best_effort_timestamp_time,pkt_pts_time")
            startInfo.ArgumentList.Add("-of")
            startInfo.ArgumentList.Add("csv=p=0")
            startInfo.ArgumentList.Add(inputPath)

            Using process As New Process() With {.StartInfo = startInfo}
                cancellationToken.ThrowIfCancellationRequested()

                If Not process.Start() Then
                    Return keyframes
                End If

                Using cancellationToken.Register(Sub() TryKillProcess(process))
                    While Not process.StandardOutput.EndOfStream
                        cancellationToken.ThrowIfCancellationRequested()
                        Dim line = process.StandardOutput.ReadLine()
                        Dim seconds As Double

                        If TryParseProbeSeconds(line, seconds) AndAlso seconds >= 0 Then
                            Dim timestamp = TimeSpan.FromSeconds(seconds)

                            If keyframes.Count = 0 OrElse timestamp > keyframes(keyframes.Count - 1) Then
                                keyframes.Add(timestamp)
                            End If
                        End If
                    End While

                    Dim waitStartedAt = DateTime.UtcNow
                    While Not process.WaitForExit(25)
                        cancellationToken.ThrowIfCancellationRequested()

                        If DateTime.UtcNow - waitStartedAt >= TimeSpan.FromSeconds(20) Then
                            TryKillProcess(process)
                            Return keyframes
                        End If
                    End While
                End Using
            End Using
        Catch ex As OperationCanceledException
        Catch ex As Exception
            If logLine IsNot Nothing Then
                logLine($"Scrub GOP index unavailable: {ex.GetBaseException().Message}")
            End If
        End Try

        Return keyframes
    End Function

    Private Shared Function TryParseProbeSeconds(line As String, ByRef seconds As Double) As Boolean
        seconds = 0.0R

        If String.IsNullOrWhiteSpace(line) Then
            Return False
        End If

        For Each part In line.Split(","c)
            If Double.TryParse(part.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, seconds) Then
                Return True
            End If
        Next

        Return False
    End Function

    Private Shared Sub TryKillProcess(process As Process)
        Try
            If process IsNot Nothing AndAlso Not process.HasExited Then
                process.Kill(True)
            End If
        Catch
        End Try
    End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        If disposed Then
            Return
        End If

        disposed = True
        indexCancellation.Cancel()
        indexCancellation.Dispose()
    End Sub
End Class
