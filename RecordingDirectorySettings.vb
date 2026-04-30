Imports System.IO

Public NotInheritable Class RecordingDirectorySettings
    Private Sub New()
    End Sub

    Public Shared ReadOnly Property DefaultRecordingDirectory As String
        Get
            Return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "FFmpegRecorder")
        End Get
    End Property

    Private Shared ReadOnly Property SettingsFilePath As String
        Get
            Return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FfmpegRecorder", "recording-directory.txt")
        End Get
    End Property

    Public Shared Function GetRecordingDirectory() As String
        Try
            If File.Exists(SettingsFilePath) Then
                Dim savedPath = File.ReadAllText(SettingsFilePath).Trim()

                If Not String.IsNullOrWhiteSpace(savedPath) Then
                    Return savedPath
                End If
            End If
        Catch
        End Try

        Return DefaultRecordingDirectory
    End Function

    Public Shared Function GetRecorderRecordingDirectory(recorderName As String) As String
        Dim rootDirectory = GetRecordingDirectory()
        Dim folderName = SanitizeDirectoryToken(recorderName, "Recorder")
        Return Path.Combine(rootDirectory, folderName)
    End Function

    Public Shared Function SaveRecordingDirectory(directoryPath As String) As String
        Dim normalizedPath = NormalizeRecordingDirectory(directoryPath)
        Directory.CreateDirectory(normalizedPath)
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsFilePath))
        File.WriteAllText(SettingsFilePath, normalizedPath)
        Return normalizedPath
    End Function

    Public Shared Function NormalizeRecordingDirectory(directoryPath As String) As String
        Dim candidatePath = If(directoryPath, String.Empty).Trim().Trim(""""c)

        If String.IsNullOrWhiteSpace(candidatePath) Then
            candidatePath = DefaultRecordingDirectory
        End If

        Return Path.GetFullPath(candidatePath)
    End Function

    Public Shared Function SanitizeDirectoryToken(value As String, fallbackValue As String) As String
        Dim safeValue = If(String.IsNullOrWhiteSpace(value), fallbackValue, value.Trim())

        For Each invalidCharacter In Path.GetInvalidFileNameChars()
            safeValue = safeValue.Replace(invalidCharacter, "_"c)
        Next

        Return If(String.IsNullOrWhiteSpace(safeValue), fallbackValue, safeValue)
    End Function
End Class
