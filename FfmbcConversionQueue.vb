Imports System.Collections.Generic
Imports System.Threading.Tasks

Public NotInheritable Class FfmbcConversionQueue
    Private Shared ReadOnly syncRoot As New Object()
    Private Shared ReadOnly pendingJobs As New Queue(Of Action)()
    Private Shared workerActive As Boolean

    Private Sub New()
    End Sub

    Public Shared Sub Enqueue(job As Action)
        If job Is Nothing Then
            Return
        End If

        Dim startWorker As Boolean

        SyncLock syncRoot
            pendingJobs.Enqueue(job)
            startWorker = Not workerActive

            If startWorker Then
                workerActive = True
            End If
        End SyncLock

        If startWorker Then
            Task.Run(AddressOf ProcessLoop)
        End If
    End Sub

    Private Shared Sub ProcessLoop()
        Do
            Dim nextJob As Action = Nothing

            SyncLock syncRoot
                If pendingJobs.Count = 0 Then
                    workerActive = False
                    Exit Do
                End If

                nextJob = pendingJobs.Dequeue()
            End SyncLock

            Try
                nextJob.Invoke()
            Catch
            End Try
        Loop
    End Sub
End Class
