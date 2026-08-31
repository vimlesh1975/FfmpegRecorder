Imports System.Collections
Imports System.Globalization
Imports System.IO
Imports System.Reflection
Imports System.Threading
Imports MediaInfo

Public NotInheritable Class MediaInfoRow
    Public Sub New(section As String, [property] As String, value As String)
        Me.Section = section
        Me.Property = [property]
        Me.Value = value
    End Sub

    Public Property Section As String
    Public Property [Property] As String
    Public Property Value As String
End Class

Public NotInheritable Class MediaInfoProvider
    Private Sub New()
    End Sub

    Public Shared Function Read(filePath As String, cancellationToken As CancellationToken) As IReadOnlyList(Of MediaInfoRow)
        If String.IsNullOrWhiteSpace(filePath) Then
            Throw New ArgumentException("Media path is empty.", NameOf(filePath))
        End If

        If Not File.Exists(filePath) Then
            Throw New FileNotFoundException("Media file was not found.", filePath)
        End If

        cancellationToken.ThrowIfCancellationRequested()

        Dim rows As New List(Of MediaInfoRow)()
        Dim fileInfo As New FileInfo(filePath)
        rows.Add(New MediaInfoRow("File", "Name", fileInfo.Name))
        rows.Add(New MediaInfoRow("File", "Folder", If(fileInfo.DirectoryName, String.Empty)))
        rows.Add(New MediaInfoRow("File", "Size", FormatBytes(fileInfo.Length)))
        rows.Add(New MediaInfoRow("File", "Modified", fileInfo.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)))

        Dim media As New MediaInfoWrapper(filePath, Nothing)
        rows.Add(New MediaInfoRow("MediaInfo", "Library Version", NullToText(media.Version)))
        rows.Add(New MediaInfoRow("MediaInfo", "Loaded", If(media.Success, "Yes", "No")))

        If media.Duration > 0 Then
            rows.Add(New MediaInfoRow("MediaInfo", "Duration", FormatDuration(TimeSpan.FromSeconds(media.Duration))))
        End If

        cancellationToken.ThrowIfCancellationRequested()

        Dim textRows = ParseMediaInfoText(media.Text)
        If textRows.Count > 0 Then
            rows.AddRange(textRows)
            Return rows
        End If

        If Not media.Success Then
            rows.Add(New MediaInfoRow("MediaInfo", "Error", "MediaInfo could not read this file."))
            Return rows
        End If

        AddPublicScalarProperties(rows, "Summary", media)
        AddEnumerableRows(rows, "Video", media.VideoStreams)
        AddEnumerableRows(rows, "Audio", media.AudioStreams)
        AddEnumerableRows(rows, "Subtitle", media.Subtitles)
        AddEnumerableRows(rows, "Chapter", media.Chapters)
        AddEnumerableRows(rows, "Menu", media.MenuStreams)
        AddPublicScalarProperties(rows, "Tags", media.Tags)

        Return rows
    End Function

    Private Shared Function ParseMediaInfoText(text As String) As List(Of MediaInfoRow)
        Dim rows As New List(Of MediaInfoRow)()
        If String.IsNullOrWhiteSpace(text) Then
            Return rows
        End If

        Dim section = "MediaInfo"
        Dim normalized = text.Replace(vbCrLf, vbLf).Replace(vbCr, vbLf)
        Dim lines = normalized.Split(New Char() {ControlChars.Lf}, StringSplitOptions.None)

        For Each rawLine In lines
            Dim line = rawLine.TrimEnd()
            If String.IsNullOrWhiteSpace(line) Then
                Continue For
            End If

            Dim separatorIndex = line.IndexOf(" : ", StringComparison.Ordinal)
            If separatorIndex < 0 Then
                section = line.Trim()
                Continue For
            End If

            Dim [property] = line.Substring(0, separatorIndex).Trim()
            Dim value = line.Substring(separatorIndex + 3).Trim()
            If [property].Length = 0 AndAlso value.Length = 0 Then
                Continue For
            End If

            rows.Add(New MediaInfoRow(section, [property], value))
        Next

        Return rows
    End Function

    Private Shared Sub AddEnumerableRows(rows As List(Of MediaInfoRow), section As String, items As IEnumerable)
        If items Is Nothing Then
            Return
        End If

        Dim index = 0
        For Each item In items
            If item Is Nothing Then
                Continue For
            End If

            index += 1
            AddPublicScalarProperties(rows, $"{section} {index}", item)
        Next
    End Sub

    Private Shared Sub AddPublicScalarProperties(rows As List(Of MediaInfoRow), section As String, source As Object)
        If source Is Nothing Then
            Return
        End If

        Dim properties = source.GetType().GetProperties(BindingFlags.Instance Or BindingFlags.Public)
        For Each [property] In properties
            If [property].GetIndexParameters().Length > 0 OrElse String.Equals([property].Name, "Text", StringComparison.Ordinal) Then
                Continue For
            End If

            Dim value As Object
            Try
                value = [property].GetValue(source)
            Catch ex As TargetInvocationException
                Continue For
            End Try

            If value Is Nothing Then
                Continue For
            End If

            If TypeOf value Is IEnumerable AndAlso Not (TypeOf value Is String) Then
                Continue For
            End If

            If Not IsScalarType([property].PropertyType) Then
                Continue For
            End If

            Dim displayValue = FormatValue(value)
            If String.IsNullOrWhiteSpace(displayValue) OrElse displayValue = "0" Then
                Continue For
            End If

            rows.Add(New MediaInfoRow(section, SplitPascalCase([property].Name), displayValue))
        Next
    End Sub

    Private Shared Function IsScalarType(type As Type) As Boolean
        Dim underlying = Nullable.GetUnderlyingType(type)
        If underlying IsNot Nothing Then
            type = underlying
        End If

        Return type.IsPrimitive OrElse
            type.IsEnum OrElse
            type Is GetType(String) OrElse
            type Is GetType(Decimal) OrElse
            type Is GetType(DateTime) OrElse
            type Is GetType(TimeSpan)
    End Function

    Private Shared Function FormatValue(value As Object) As String
        If TypeOf value Is Boolean Then
            Return If(CBool(value), "Yes", "No")
        End If
        If TypeOf value Is TimeSpan Then
            Return FormatDuration(CType(value, TimeSpan))
        End If
        If TypeOf value Is DateTime Then
            Return CType(value, DateTime).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
        End If
        If TypeOf value Is IFormattable Then
            Return CType(value, IFormattable).ToString(Nothing, CultureInfo.InvariantCulture)
        End If

        Return If(value?.ToString(), String.Empty)
    End Function

    Private Shared Function SplitPascalCase(value As String) As String
        If String.IsNullOrWhiteSpace(value) Then
            Return String.Empty
        End If

        Dim chars As New List(Of Char)(value.Length + 8) From {value(0)}
        For i = 1 To value.Length - 1
            If Char.IsUpper(value(i)) AndAlso Not Char.IsWhiteSpace(value(i - 1)) Then
                chars.Add(" "c)
            End If
            chars.Add(value(i))
        Next

        Return New String(chars.ToArray())
    End Function

    Private Shared Function FormatDuration(duration As TimeSpan) As String
        If duration.TotalHours >= 1 Then
            Return duration.ToString("hh\:mm\:ss\.fff", CultureInfo.InvariantCulture)
        Else
            Return duration.ToString("mm\:ss\.fff", CultureInfo.InvariantCulture)
        End If
    End Function

    Private Shared Function FormatBytes(bytes As Long) As String
        Dim units As String() = {"B", "KB", "MB", "GB", "TB"}
        Dim value As Double = bytes
        Dim unit = 0
        While value >= 1024.0R AndAlso unit < units.Length - 1
            value /= 1024.0R
            unit += 1
        End While

        Return String.Format(CultureInfo.InvariantCulture, "{0:0.##} {1} ({2:N0} bytes)", value, units(unit), bytes)
    End Function

    Private Shared Function NullToText(value As String) As String
        Return If(String.IsNullOrWhiteSpace(value), "--", value.Trim())
    End Function
End Class
