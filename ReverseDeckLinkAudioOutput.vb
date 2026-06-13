Imports System.Threading
Imports System.Threading.Tasks

Friend NotInheritable Class ReverseDeckLinkAudioOutput
    Implements IDisposable

    Private Const AudioSampleRate As Integer = 48000
    Private Const MaxQueuedSampleFrames As Integer = AudioSampleRate \ 2
    Private Const MaxQueuedBuffers As Integer = 16

    Private ReadOnly runner As InProcessDeckLinkOutputRunner
    Private ReadOnly logLine As Action(Of String)
    Private ReadOnly gate As New Object()
    Private ReadOnly queue As New Queue(Of AudioPacket)()
    Private ReadOnly signal As New SemaphoreSlim(0)
    Private ReadOnly cancellation As New CancellationTokenSource()
    Private ReadOnly writerTask As Task
    Private queuedSampleFrames As Integer
    Private disposed As Boolean
    Private stopped As Boolean
    Private lastDropLogAt As DateTime = DateTime.MinValue

    Public Sub New(runner As InProcessDeckLinkOutputRunner, logLine As Action(Of String))
        Me.runner = runner
        Me.logLine = logLine
        writerTask = Task.Run(Function() WriteLoopAsync(cancellation.Token))
    End Sub

    Public Function Enqueue(pcm As Byte(), byteCount As Integer, sampleFrames As Integer) As Boolean
        If pcm Is Nothing OrElse byteCount <= 0 OrElse sampleFrames <= 0 OrElse disposed OrElse stopped Then
            Return False
        End If

        byteCount = Math.Min(byteCount, pcm.Length)
        Dim copy(byteCount - 1) As Byte
        Buffer.BlockCopy(pcm, 0, copy, 0, byteCount)

        Dim droppedSampleFrames = 0

        SyncLock gate
            If disposed OrElse stopped Then
                Return False
            End If

            While (queuedSampleFrames + sampleFrames > MaxQueuedSampleFrames OrElse queue.Count >= MaxQueuedBuffers) AndAlso queue.Count > 0
                Dim dropped = queue.Dequeue()
                queuedSampleFrames -= dropped.SampleFrames
                droppedSampleFrames += dropped.SampleFrames
            End While

            queue.Enqueue(New AudioPacket(copy, sampleFrames))
            queuedSampleFrames += sampleFrames
        End SyncLock

        If droppedSampleFrames > 0 Then
            LogDrop(droppedSampleFrames)
        End If

        signal.Release()
        Return True
    End Function

    Private Async Function WriteLoopAsync(cancellationToken As CancellationToken) As Task
        Try
            While Not cancellationToken.IsCancellationRequested
                Await signal.WaitAsync(cancellationToken)

                Dim packet As AudioPacket = Nothing
                While TryDequeue(packet)
                    Dim wrote = Await runner.WriteCachedAudioSamplesAsync(packet.Pcm, packet.Pcm.Length, packet.SampleFrames, cancellationToken)

                    If Not wrote Then
                        StopWriter("Reverse DeckLink audio stopped: audio write stalled or output is unavailable.")
                        Return
                    End If
                End While
            End While
        Catch ex As OperationCanceledException
        Catch ex As Exception
            StopWriter($"Reverse DeckLink audio stopped: {ex.Message}")
        End Try
    End Function

    Private Function TryDequeue(ByRef packet As AudioPacket) As Boolean
        SyncLock gate
            If queue.Count = 0 Then
                packet = Nothing
                Return False
            End If

            packet = queue.Dequeue()
            queuedSampleFrames -= packet.SampleFrames
            Return True
        End SyncLock
    End Function

    Private Sub StopWriter(message As String)
        SyncLock gate
            If stopped Then
                Return
            End If

            stopped = True
            queue.Clear()
            queuedSampleFrames = 0
        End SyncLock

        If logLine IsNot Nothing Then
            logLine(message)
        End If
    End Sub

    Private Sub LogDrop(droppedSampleFrames As Integer)
        Dim now = DateTime.UtcNow

        If now - lastDropLogAt < TimeSpan.FromSeconds(1) Then
            Return
        End If

        lastDropLogAt = now

        If logLine IsNot Nothing Then
            logLine($"Reverse DeckLink audio dropped {droppedSampleFrames} sample frames to keep shuttle responsive.")
        End If
    End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        SyncLock gate
            If disposed Then
                Return
            End If

            disposed = True
            queue.Clear()
            queuedSampleFrames = 0
        End SyncLock

        cancellation.Cancel()
        signal.Release()

        Try
            writerTask.Wait(1000)
        Catch
        End Try

        signal.Dispose()
        cancellation.Dispose()
    End Sub

    Private NotInheritable Class AudioPacket
        Public Sub New(pcm As Byte(), sampleFrames As Integer)
            Me.Pcm = pcm
            Me.SampleFrames = sampleFrames
        End Sub

        Public ReadOnly Property Pcm As Byte()
        Public ReadOnly Property SampleFrames As Integer
    End Class
End Class
