Imports System.Drawing
Imports System.Drawing.Drawing2D
Imports System.Windows.Forms

Public NotInheritable Class PreviewFullscreenForm
    Inherits Form

    Private ReadOnly _previewSurface As New FullscreenPreviewSurface()

    Public Sub New()
        Text = "DeckLink Player Preview"
        StartPosition = FormStartPosition.Manual
        FormBorderStyle = FormBorderStyle.None
        WindowState = FormWindowState.Normal
        ShowInTaskbar = False
        TopMost = True
        KeyPreview = True
        BackColor = Color.Black
        Padding = New Padding(0)

        _previewSurface.Dock = DockStyle.Fill
        _previewSurface.Margin = New Padding(0)
        Controls.Add(_previewSurface)

        AddHandler KeyDown, Sub(s, e)
                                If e.KeyCode = Keys.Escape Then
                                    e.Handled = True
                                    Close()
                                End If
                            End Sub

        AddHandler _previewSurface.DoubleClick, Sub(s, e)
                                                    Close()
                                                End Sub
    End Sub

    Public Sub SetPreviewImage(image As Image)
        If IsDisposed Then
            image?.Dispose()
            Return
        End If

        If InvokeRequired Then
            Try
                BeginInvoke(Sub() SetPreviewImage(image))
            Catch ex As Exception
                image?.Dispose()
            End Try
            Return
        End If

        _previewSurface.SetPreviewImage(image)
    End Sub

    Public Sub ClearPreviewImage()
        If IsDisposed Then
            Return
        End If

        If InvokeRequired Then
            Try
                BeginInvoke(Sub() ClearPreviewImage())
            Catch ex As Exception
            End Try
            Return
        End If

        _previewSurface.ClearPreviewImage()
    End Sub

    Protected Overrides Sub Dispose(disposing As Boolean)
        If disposing Then
            _previewSurface.ClearPreviewImage()
        End If

        MyBase.Dispose(disposing)
    End Sub
End Class

Public NotInheritable Class FullscreenPreviewSurface
    Inherits Control

    Private _image As Image

    Public Sub New()
        BackColor = Color.Black
        SetStyle(
            ControlStyles.AllPaintingInWmPaint Or
            ControlStyles.UserPaint Or
            ControlStyles.OptimizedDoubleBuffer Or
            ControlStyles.ResizeRedraw,
            True)
        UpdateStyles()
    End Sub

    Public Sub SetPreviewImage(image As Image)
        Dim previous = _image
        _image = image
        previous?.Dispose()
        Invalidate()
    End Sub

    Public Sub ClearPreviewImage()
        Dim previous = _image
        _image = Nothing
        previous?.Dispose()
        Invalidate()
    End Sub

    Protected Overrides Sub OnPaint(e As PaintEventArgs)
        e.Graphics.Clear(Color.Black)

        Dim currentImage = _image
        If currentImage Is Nothing OrElse currentImage.Width <= 0 OrElse currentImage.Height <= 0 OrElse ClientSize.Width <= 0 OrElse ClientSize.Height <= 0 Then
            Return
        End If

        Dim destination = GetLetterboxedRectangle(currentImage.Size, ClientRectangle)
        e.Graphics.CompositingQuality = CompositingQuality.HighQuality
        e.Graphics.InterpolationMode = If(destination.Size = currentImage.Size,
            InterpolationMode.NearestNeighbor,
            InterpolationMode.HighQualityBicubic)
        e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality
        e.Graphics.SmoothingMode = SmoothingMode.None
        e.Graphics.DrawImage(currentImage, destination)
    End Sub

    Private Shared Function GetLetterboxedRectangle(imageSize As Size, bounds As Rectangle) As Rectangle
        Dim imageAspect = imageSize.Width / CDbl(imageSize.Height)
        Dim boundsAspect = bounds.Width / CDbl(bounds.Height)
        Dim width As Integer
        Dim height As Integer

        If imageAspect > boundsAspect Then
            width = bounds.Width
            height = CInt(Math.Round(bounds.Width / imageAspect))
        Else
            height = bounds.Height
            width = CInt(Math.Round(bounds.Height * imageAspect))
        End If

        Dim x = bounds.X + (bounds.Width - width) \ 2
        Dim y = bounds.Y + (bounds.Height - height) \ 2
        Return New Rectangle(x, y, width, height)
    End Function
End Class
