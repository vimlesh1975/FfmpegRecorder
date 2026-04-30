Imports System.Text

Friend Class RecorderOptions
    Public Property FfmpegPath As String
    Public Property DeviceName As String
    Public Property FormatCode As String
    Public Property AudioInput As String
    Public Property Channels As Integer
    Public Property OutputFolder As String
    Public Property FilePrefix As String
    Public Property ClipDurationSeconds As Integer
    Public Property ContainerExtension As String
    Public Property VideoFilter As String
    Public Property PreviewVideoFilter As String
    Public Property OutputOptions As String
    Public Property UseSonyCompatibleAudioLayout As Boolean

    Public Function BuildOutputPattern(Optional timestampToken As String = Nothing) As String
        Dim stamp = If(String.IsNullOrWhiteSpace(timestampToken), "%d%m%Y_%H%M%S", timestampToken)
        Dim safePrefix = If(String.IsNullOrWhiteSpace(FilePrefix), "clip", FilePrefix.Trim())
        Dim safeExtension = If(String.IsNullOrWhiteSpace(ContainerExtension), ".mov", ContainerExtension.Trim())

        Return IO.Path.Combine(OutputFolder, $"{safePrefix}_{stamp}{safeExtension}")
    End Function

    Public Function BuildArguments(outputPattern As String) As String
        Dim builder As New StringBuilder()

        AppendInputArguments(builder)

        If Not String.IsNullOrWhiteSpace(VideoFilter) Then
            builder.Append("-vf ").Append(Quote(VideoFilter.Trim())).Append(" ")
        End If

        If String.IsNullOrWhiteSpace(OutputOptions) Then
            builder.Append("-c:v prores_ks -profile:v 3 -pix_fmt yuv422p10le -vendor apl0 -bits_per_mb 2400 -c:a pcm_s16le -ar 48000 ")
        Else
            builder.Append(OutputOptions.Trim()).Append(" ")
        End If

        AppendSegmentBoundaryKeyframes(builder)

        builder.Append("-f segment ")
        builder.Append("-segment_time ").Append(Math.Max(1, ClipDurationSeconds)).Append(" ")
        builder.Append("-reset_timestamps 1 ")
        builder.Append("-segment_start_number 0 ")
        builder.Append("-segment_format ").Append(Quote(GetSegmentFormat())).Append(" ")
        builder.Append("-strftime 1 ")
        builder.Append(Quote(outputPattern))

        Return builder.ToString().Trim()
    End Function

    Public Function BuildRecordingWithPreviewArguments(outputPattern As String, previewPort As Integer, audioMonitorPort As Integer, Optional previewWidth As Integer = 960, Optional previewFrameRate As Integer = 8) As String
        Dim builder As New StringBuilder()
        Dim filterGraph = BuildRecordingPreviewFilterGraph(audioMonitorPort, previewWidth, previewFrameRate, VideoFilter, PreviewVideoFilter)

        AppendInputArguments(builder)
        builder.Append("-filter_complex ").Append(Quote(filterGraph)).Append(" ")

        builder.Append("-map ").Append(Quote("[rec_v]")).Append(" ")

        For Each audioLabel In GetRecordingAudioLabels()
            builder.Append("-map ").Append(Quote(audioLabel)).Append(" ")
        Next

        If String.IsNullOrWhiteSpace(OutputOptions) Then
            builder.Append("-c:v prores_ks -profile:v 3 -pix_fmt yuv422p10le -vendor apl0 -bits_per_mb 2400 -c:a pcm_s16le -ar 48000 ")
        Else
            builder.Append(OutputOptions.Trim()).Append(" ")
        End If

        AppendSegmentBoundaryKeyframes(builder)

        builder.Append("-f segment ")
        builder.Append("-segment_time ").Append(Math.Max(1, ClipDurationSeconds)).Append(" ")
        builder.Append("-reset_timestamps 1 ")
        builder.Append("-segment_start_number 0 ")
        builder.Append("-segment_format ").Append(Quote(GetSegmentFormat())).Append(" ")
        builder.Append("-strftime 1 ")
        builder.Append(Quote(outputPattern)).Append(" ")

        builder.Append("-map ").Append(Quote("[preview]")).Append(" ")
        builder.Append("-an -flush_packets 1 -c:v mjpeg -q:v 6 -f mjpeg ")
        builder.Append(Quote(BuildPreviewUrl(previewPort))).Append(" ")

        If audioMonitorPort > 0 Then
            builder.Append("-map ").Append(Quote("[mon_a]")).Append(" ")
            builder.Append("-vn -flush_packets 1 -c:a pcm_s16le -ar 48000 -ac ").Append(Math.Max(1, Channels)).Append(" -f s16le ")
            builder.Append(Quote(BuildAudioMonitorUrl(audioMonitorPort)))
        End If

        Return builder.ToString().Trim()
    End Function

    Public Function BuildPreviewArguments(Optional previewWidth As Integer = 960, Optional previewFrameRate As Integer = 8) As String
        Dim builder As New StringBuilder()
        Dim filterGraph = BuildLivePreviewFilterGraph(previewWidth, previewFrameRate, PreviewVideoFilter)

        builder.Append("-hide_banner -loglevel warning -fflags nobuffer -flags low_delay ")
        builder.Append("-f decklink ")
        AppendFormatCodeArgument(builder)
        builder.Append("-audio_input ").Append(Quote(AudioInput)).Append(" ")
        builder.Append("-channels ").Append(Channels).Append(" ")
        builder.Append("-i ").Append(Quote(DeviceName)).Append(" ")
        builder.Append("-filter_complex ").Append(Quote(filterGraph)).Append(" ")
        builder.Append("-map ").Append(Quote("[out]")).Append(" ")
        builder.Append("-an -flush_packets 1 -c:v mjpeg -q:v 6 -f mjpeg pipe:1")

        Return builder.ToString()
    End Function

    Public Function BuildPreviewWithAudioMonitorArguments(audioMonitorPort As Integer, Optional previewWidth As Integer = 960, Optional previewFrameRate As Integer = 8) As String
        If audioMonitorPort <= 0 Then
            Return BuildPreviewArguments(previewWidth, previewFrameRate)
        End If

        Dim builder As New StringBuilder()
        Dim filterGraph = BuildLivePreviewFilterGraph(previewWidth, previewFrameRate, PreviewVideoFilter)

        builder.Append("-hide_banner -loglevel warning -fflags nobuffer -flags low_delay ")
        builder.Append("-f decklink ")
        AppendFormatCodeArgument(builder)
        builder.Append("-audio_input ").Append(Quote(AudioInput)).Append(" ")
        builder.Append("-channels ").Append(Channels).Append(" ")
        builder.Append("-i ").Append(Quote(DeviceName)).Append(" ")
        builder.Append("-filter_complex ").Append(Quote(filterGraph)).Append(" ")
        builder.Append("-map ").Append(Quote("[out]")).Append(" ")
        builder.Append("-an -flush_packets 1 -c:v mjpeg -q:v 6 -f mjpeg pipe:1 ")
        builder.Append("-map 0:a ")
        builder.Append("-vn -flush_packets 1 -c:a pcm_s16le -ar 48000 -ac ").Append(Math.Max(1, Channels)).Append(" -f s16le ")
        builder.Append(Quote(BuildAudioMonitorUrl(audioMonitorPort)))

        Return builder.ToString()
    End Function

    Public Function BuildPreviewUrl(previewPort As Integer) As String
        Return $"tcp://127.0.0.1:{previewPort}?listen=1"
    End Function

    Public Function BuildAudioMonitorUrl(audioMonitorPort As Integer) As String
        Return $"tcp://127.0.0.1:{audioMonitorPort}?listen=1"
    End Function

    Private Function BuildRecordingPreviewFilterGraph(audioMonitorPort As Integer, previewWidth As Integer, previewFrameRate As Integer, recordingVideoFilter As String, previewVideoFilter As String) As String
        Dim previewHeight = GetPreviewHeight(previewWidth)
        Dim meterChannelWidth = GetMeterChannelWidth(previewWidth)
        Dim meterOutputWidth = GetMeterOutputWidth(previewWidth)
        Dim audioSplitOutputs = If(audioMonitorPort > 0, If(UseSonyCompatibleAudioLayout, "[source_a][meter_a][mon_a]", "[rec_a][meter_a][mon_a]"), If(UseSonyCompatibleAudioLayout, "[source_a][meter_a]", "[rec_a][meter_a]"))
        Dim audioSplitCount = If(audioMonitorPort > 0, 3, 2)
        Dim rightMeterPan = GetRightMeterPanExpression()
        Dim recordingChain = BuildRecordingVideoChain(recordingVideoFilter)
        Dim previewChain = BuildPreviewVideoChain(previewVideoFilter)
        Dim sonyCompatibleAudioChain = BuildSonyCompatibleAudioChain()

        Return $"[0:v]split=2[rec_src][preview_src];{recordingChain}{previewChain}[preview_stage]scale={previewWidth}:{previewHeight}:force_original_aspect_ratio=decrease,pad={previewWidth}:{previewHeight}:(ow-iw)/2:(oh-ih)/2,fps={Math.Max(1, previewFrameRate)},format=yuv420p[preview_video];[0:a]asplit={audioSplitCount}{audioSplitOutputs};{sonyCompatibleAudioChain}[meter_a]asplit=2[left_meter_src][right_meter_src];[left_meter_src]pan=mono|c0=c0,showvolume=r={Math.Max(1, previewFrameRate)}:w={meterChannelWidth}:h={previewHeight}:f=0.92:b=2:t=0:v=1:dm=1:o=v:ds=log:p=0.18:m=r[left_bar_src];[left_bar_src]scale={meterOutputWidth}:{previewHeight},format=yuv420p[left_bar];[right_meter_src]pan={rightMeterPan},showvolume=r={Math.Max(1, previewFrameRate)}:w={meterChannelWidth}:h={previewHeight}:f=0.92:b=2:t=0:v=1:dm=1:o=v:ds=log:p=0.18:m=r[right_bar_src];[right_bar_src]scale={meterOutputWidth}:{previewHeight},format=yuv420p[right_bar];[left_bar][preview_video][right_bar]hstack=inputs=3[preview]"
    End Function

    Private Function BuildSonyCompatibleAudioChain() As String
        If Not UseSonyCompatibleAudioLayout Then
            Return String.Empty
        End If

        Return "[source_a]channelsplit=channel_layout=stereo[rec_a1][rec_a2];anullsrc=r=48000:cl=mono[rec_a3];anullsrc=r=48000:cl=mono[rec_a4];anullsrc=r=48000:cl=mono[rec_a5];anullsrc=r=48000:cl=mono[rec_a6];anullsrc=r=48000:cl=mono[rec_a7];anullsrc=r=48000:cl=mono[rec_a8];"
    End Function

    Private Function GetRecordingAudioLabels() As IEnumerable(Of String)
        If Not UseSonyCompatibleAudioLayout Then
            Return New String() {"[rec_a]"}
        End If

        Return New String() {
            "[rec_a1]",
            "[rec_a2]",
            "[rec_a3]",
            "[rec_a4]",
            "[rec_a5]",
            "[rec_a6]",
            "[rec_a7]",
            "[rec_a8]"
        }
    End Function

    Private Function BuildRecordingVideoChain(recordingVideoFilter As String) As String
        If String.IsNullOrWhiteSpace(recordingVideoFilter) Then
            Return "[rec_src]null[rec_v];"
        End If

        Return $"[rec_src]{recordingVideoFilter.Trim()}[rec_v];"
    End Function

    Private Function BuildPreviewVideoChain(previewVideoFilter As String) As String
        If String.IsNullOrWhiteSpace(previewVideoFilter) Then
            Return "[preview_src]null[preview_stage];"
        End If

        Return $"[preview_src]{previewVideoFilter.Trim()}[preview_stage];"
    End Function

    Private Function BuildLivePreviewFilterGraph(previewWidth As Integer, previewFrameRate As Integer, previewVideoFilter As String) As String
        Dim previewHeight = GetPreviewHeight(previewWidth)
        Dim meterChannelWidth = GetMeterChannelWidth(previewWidth)
        Dim meterOutputWidth = GetMeterOutputWidth(previewWidth)
        Dim rightMeterPan = GetRightMeterPanExpression()
        Dim previewChain = BuildStandalonePreviewVideoChain(previewVideoFilter)

        Return $"{previewChain}[preview_stage]scale={previewWidth}:{previewHeight}:force_original_aspect_ratio=decrease,pad={previewWidth}:{previewHeight}:(ow-iw)/2:(oh-ih)/2,fps={Math.Max(1, previewFrameRate)},format=yuv420p[video];[0:a]asplit=2[left_meter_src][right_meter_src];[left_meter_src]pan=mono|c0=c0,showvolume=r={Math.Max(1, previewFrameRate)}:w={meterChannelWidth}:h={previewHeight}:f=0.92:b=2:t=0:v=1:dm=1:o=v:ds=log:p=0.18:m=r[left_bar_src];[left_bar_src]scale={meterOutputWidth}:{previewHeight},format=yuv420p[left_bar];[right_meter_src]pan={rightMeterPan},showvolume=r={Math.Max(1, previewFrameRate)}:w={meterChannelWidth}:h={previewHeight}:f=0.92:b=2:t=0:v=1:dm=1:o=v:ds=log:p=0.18:m=r[right_bar_src];[right_bar_src]scale={meterOutputWidth}:{previewHeight},format=yuv420p[right_bar];[left_bar][video][right_bar]hstack=inputs=3[out]"
    End Function

    Private Function BuildStandalonePreviewVideoChain(previewVideoFilter As String) As String
        If String.IsNullOrWhiteSpace(previewVideoFilter) Then
            Return "[0:v]null[preview_stage];"
        End If

        Return $"[0:v]{previewVideoFilter.Trim()}[preview_stage];"
    End Function

    Private Function GetPreviewHeight(previewWidth As Integer) As Integer
        Dim calculatedHeight = CInt(Math.Round(previewWidth * 9.0 / 16.0, MidpointRounding.AwayFromZero))

        If calculatedHeight Mod 2 <> 0 Then
            calculatedHeight -= 1
        End If

        Return Math.Max(180, calculatedHeight)
    End Function

    Private Function GetMeterChannelWidth(previewWidth As Integer) As Integer
        Return 80
    End Function

    Private Function GetMeterOutputWidth(previewWidth As Integer) As Integer
        Return Math.Max(20, GetMeterChannelWidth(previewWidth) \ 4)
    End Function

    Private Function GetRightMeterPanExpression() As String
        If Channels <= 1 Then
            Return QuotePan("mono|c0=c0")
        End If

        Return QuotePan("mono|c0=c1")
    End Function

    Private Function QuotePan(expression As String) As String
        Return expression.Replace("""", String.Empty)
    End Function

    Private Sub AppendSegmentBoundaryKeyframes(builder As StringBuilder)
        Dim segmentSeconds = Math.Max(1, ClipDurationSeconds)
        builder.Append("-force_key_frames ").Append(Quote($"expr:gte(t,n_forced*{segmentSeconds})")).Append(" ")
    End Sub

    Private Sub AppendInputArguments(builder As StringBuilder)
        builder.Append("-hide_banner ")
        builder.Append("-f decklink ")
        AppendFormatCodeArgument(builder)
        builder.Append("-audio_input ").Append(Quote(AudioInput)).Append(" ")
        builder.Append("-channels ").Append(Channels).Append(" ")
        builder.Append("-i ").Append(Quote(DeviceName)).Append(" ")
    End Sub

    Private Sub AppendFormatCodeArgument(builder As StringBuilder)
        If String.IsNullOrWhiteSpace(FormatCode) Then
            Return
        End If

        builder.Append("-format_code ").Append(Quote(FormatCode.Trim())).Append(" ")
    End Sub

    Private Function GetSegmentFormat() As String
        Return If(String.IsNullOrWhiteSpace(ContainerExtension), "mov", ContainerExtension.Trim().TrimStart("."c))
    End Function

    Private Shared Function Quote(value As String) As String
        Dim safeValue = If(value, String.Empty).Replace("""", String.Empty)
        Return $"""{safeValue}"""
    End Function
End Class
