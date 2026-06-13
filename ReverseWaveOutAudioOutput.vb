Imports System.Buffers.Binary
Imports System.Runtime.InteropServices

Friend NotInheritable Class ReverseWaveOutAudioOutput
    Implements IDisposable

    Private Const AudioSampleRate As Integer = 48000
    Private Const SourceBytesPerSample As Integer = 4
    Private Const OutputBytesPerSample As Integer = 2
    Private Const WaveMapper As Integer = -1
    Private Const WaveFormatPcm As Integer = 1
    Private Const WhdrDone As Integer = &H1
    Private Const MaxPendingBuffers As Integer = 20

    Private ReadOnly gate As New Object()
    Private ReadOnly pendingBuffers As New List(Of WaveBuffer)()
    Private ReadOnly sourceChannels As Integer
    Private ReadOnly outputChannels As Integer
    Private ReadOnly gain As Double
    Private waveOut As IntPtr
    Private disposed As Boolean

    Public Sub New(sourceChannels As Integer, Optional gain As Double = 1.0R)
        If sourceChannels <= 0 Then
            Throw New ArgumentOutOfRangeException(NameOf(sourceChannels), "Audio channels must be positive.")
        End If

        Me.sourceChannels = sourceChannels
        Me.outputChannels = Math.Min(sourceChannels, 2)
        Me.gain = Math.Max(0.05R, Math.Min(1.0R, gain))

        Dim format As New WaveFormatEx() With {
            .FormatTag = CUShort(WaveFormatPcm),
            .Channels = CUShort(outputChannels),
            .SamplesPerSec = AudioSampleRate,
            .BitsPerSample = 16,
            .BlockAlign = CUShort(outputChannels * OutputBytesPerSample)
        }
        format.AvgBytesPerSec = format.SamplesPerSec * format.BlockAlign

        Dim result = waveOutOpen(waveOut, UInteger.MaxValue, format, IntPtr.Zero, IntPtr.Zero, 0UI)

        If result <> 0 Then
            Throw New InvalidOperationException($"Windows audio output open failed: {result}")
        End If
    End Sub

    Public Sub Enqueue(pcm32 As Byte(), byteCount As Integer)
        If disposed OrElse pcm32 Is Nothing OrElse byteCount <= 0 Then
            Return
        End If

        byteCount = Math.Min(byteCount, pcm32.Length)
        Dim sampleFrames = byteCount \ (sourceChannels * SourceBytesPerSample)

        If sampleFrames <= 0 Then
            Return
        End If

        Dim pcm16 = ConvertToPcm16(pcm32, sampleFrames)

        SyncLock gate
            If disposed Then
                Return
            End If

            CleanupCompletedBuffersUnderLock()

            If pendingBuffers.Count >= MaxPendingBuffers Then
                Return
            End If

            Dim buffer As New WaveBuffer(pcm16)
            PrepareAndWriteBuffer(buffer)
            pendingBuffers.Add(buffer)
        End SyncLock
    End Sub

    Private Function ConvertToPcm16(pcm32 As Byte(), sampleFrames As Integer) As Byte()
        Dim pcm16(sampleFrames * outputChannels * OutputBytesPerSample - 1) As Byte

        For frame = 0 To sampleFrames - 1
            For channel = 0 To outputChannels - 1
                Dim sourceOffset = (frame * sourceChannels + channel) * SourceBytesPerSample
                Dim sample32 = BinaryPrimitives.ReadInt32LittleEndian(pcm32.AsSpan(sourceOffset, SourceBytesPerSample))
                Dim sample16 = CShort(Math.Max(Short.MinValue, Math.Min(Short.MaxValue, sample32 / 65536.0R * gain)))
                Dim destinationOffset = (frame * outputChannels + channel) * OutputBytesPerSample
                BinaryPrimitives.WriteInt16LittleEndian(pcm16.AsSpan(destinationOffset, OutputBytesPerSample), sample16)
            Next
        Next

        Return pcm16
    End Function

    Private Sub PrepareAndWriteBuffer(buffer As WaveBuffer)
        Dim header As New WaveHeader() With {
            .Data = buffer.Data,
            .BufferLength = CUInt(buffer.Length)
        }
        Marshal.StructureToPtr(header, buffer.Header, False)

        Dim headerSize = Marshal.SizeOf(Of WaveHeader)()
        Dim result = waveOutPrepareHeader(waveOut, buffer.Header, CUInt(headerSize))

        If result <> 0 Then
            buffer.Dispose()
            Throw New InvalidOperationException($"Windows audio prepare failed: {result}")
        End If

        buffer.Prepared = True
        result = waveOutWrite(waveOut, buffer.Header, CUInt(headerSize))

        If result <> 0 Then
            UnprepareBuffer(buffer)
            buffer.Dispose()
            Throw New InvalidOperationException($"Windows audio write failed: {result}")
        End If
    End Sub

    Private Sub CleanupCompletedBuffersUnderLock()
        For index = pendingBuffers.Count - 1 To 0 Step -1
            Dim buffer = pendingBuffers(index)
            Dim header = Marshal.PtrToStructure(Of WaveHeader)(buffer.Header)

            If (header.Flags And WhdrDone) = 0 Then
                Continue For
            End If

            UnprepareBuffer(buffer)
            buffer.Dispose()
            pendingBuffers.RemoveAt(index)
        Next
    End Sub

    Private Sub UnprepareBuffer(buffer As WaveBuffer)
        If Not buffer.Prepared OrElse waveOut = IntPtr.Zero Then
            Return
        End If

        waveOutUnprepareHeader(waveOut, buffer.Header, CUInt(Marshal.SizeOf(Of WaveHeader)()))
        buffer.Prepared = False
    End Sub

    Public Sub Dispose() Implements IDisposable.Dispose
        SyncLock gate
            If disposed Then
                Return
            End If

            disposed = True

            If waveOut <> IntPtr.Zero Then
                waveOutReset(waveOut)

                For Each buffer In pendingBuffers
                    UnprepareBuffer(buffer)
                    buffer.Dispose()
                Next

                pendingBuffers.Clear()
                waveOutClose(waveOut)
                waveOut = IntPtr.Zero
            End If
        End SyncLock
    End Sub

    <DllImport("winmm.dll")>
    Private Shared Function waveOutOpen(ByRef waveOut As IntPtr, deviceId As UInteger, ByRef format As WaveFormatEx, callback As IntPtr, instance As IntPtr, flags As UInteger) As Integer
    End Function

    <DllImport("winmm.dll")>
    Private Shared Function waveOutPrepareHeader(waveOut As IntPtr, waveHeader As IntPtr, waveHeaderSize As UInteger) As Integer
    End Function

    <DllImport("winmm.dll")>
    Private Shared Function waveOutWrite(waveOut As IntPtr, waveHeader As IntPtr, waveHeaderSize As UInteger) As Integer
    End Function

    <DllImport("winmm.dll")>
    Private Shared Function waveOutUnprepareHeader(waveOut As IntPtr, waveHeader As IntPtr, waveHeaderSize As UInteger) As Integer
    End Function

    <DllImport("winmm.dll")>
    Private Shared Function waveOutReset(waveOut As IntPtr) As Integer
    End Function

    <DllImport("winmm.dll")>
    Private Shared Function waveOutClose(waveOut As IntPtr) As Integer
    End Function

    <StructLayout(LayoutKind.Sequential)>
    Private Structure WaveFormatEx
        Public FormatTag As UShort
        Public Channels As UShort
        Public SamplesPerSec As Integer
        Public AvgBytesPerSec As Integer
        Public BlockAlign As UShort
        Public BitsPerSample As UShort
        Public Size As UShort
    End Structure

    <StructLayout(LayoutKind.Sequential)>
    Private Structure WaveHeader
        Public Data As IntPtr
        Public BufferLength As UInteger
        Public BytesRecorded As UInteger
        Public User As IntPtr
        Public Flags As UInteger
        Public Loops As UInteger
        Public NextHeader As IntPtr
        Public Reserved As IntPtr
    End Structure

    Private NotInheritable Class WaveBuffer
        Implements IDisposable

        Public Sub New(data As Byte())
            Length = data.Length
            Me.Data = Marshal.AllocHGlobal(data.Length)
            Header = Marshal.AllocHGlobal(Marshal.SizeOf(Of WaveHeader)())
            Marshal.Copy(data, 0, Me.Data, data.Length)
        End Sub

        Public ReadOnly Property Data As IntPtr
        Public ReadOnly Property Header As IntPtr
        Public ReadOnly Property Length As Integer
        Public Property Prepared As Boolean

        Public Sub Dispose() Implements IDisposable.Dispose
            If Header <> IntPtr.Zero Then
                Marshal.FreeHGlobal(Header)
            End If

            If Data <> IntPtr.Zero Then
                Marshal.FreeHGlobal(Data)
            End If
        End Sub
    End Class
End Class
