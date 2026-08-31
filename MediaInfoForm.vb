Imports System.Collections.Generic
Imports System.Drawing
Imports System.IO
Imports System.Threading
Imports System.Threading.Tasks
Imports System.Windows.Forms

Public NotInheritable Class MediaInfoForm
    Inherits Form

    Private Const PreferredWindowWidth As Integer = 480

    Private ReadOnly _path As String
    Private ReadOnly _loadCancellation As New CancellationTokenSource()
    Private ReadOnly _grid As New DataGridView()
    Private ReadOnly _titleLabel As New Label()
    Private ReadOnly _pathLabel As New Label()

    Public Sub New(path As String)
        _path = path

        Text = $"MediaInfo - {System.IO.Path.GetFileName(path)}"
        StartPosition = FormStartPosition.Manual
        Size = New Size(PreferredWindowWidth, 700)
        MinimumSize = New Size(PreferredWindowWidth, 480)
        BackColor = Color.FromArgb(22, 25, 29)
        Font = New Font("Segoe UI", 9.0F, FontStyle.Regular, GraphicsUnit.Point)
        ShowInTaskbar = False

        BuildUi()

        AddHandler FormClosed, Sub(s, e)
                                   _loadCancellation.Cancel()
                               End Sub
    End Sub

    Protected Overrides Async Sub OnShown(e As EventArgs)
        MyBase.OnShown(e)
        CenterNearOwner()
        Await LoadMediaInfoAsync()
    End Sub

    Private Sub BuildUi()
        Dim root As New TableLayoutPanel With {
            .Dock = DockStyle.Fill,
            .ColumnCount = 1,
            .RowCount = 2,
            .Padding = New Padding(14),
            .BackColor = BackColor
        }
        root.RowStyles.Add(New RowStyle(SizeType.Absolute, 58.0F))
        root.RowStyles.Add(New RowStyle(SizeType.Percent, 100.0F))
        Controls.Add(root)

        Dim header As New TableLayoutPanel With {
            .Dock = DockStyle.Fill,
            .ColumnCount = 1,
            .RowCount = 2,
            .Margin = New Padding(0, 0, 0, 10),
            .BackColor = BackColor
        }
        header.RowStyles.Add(New RowStyle(SizeType.Absolute, 30.0F))
        header.RowStyles.Add(New RowStyle(SizeType.Percent, 100.0F))

        _titleLabel.AutoSize = False
        _titleLabel.Dock = DockStyle.Fill
        _titleLabel.Text = System.IO.Path.GetFileName(_path)
        _titleLabel.Font = New Font("Segoe UI Semibold", 15.0F, FontStyle.Bold, GraphicsUnit.Point)
        _titleLabel.ForeColor = Color.FromArgb(239, 244, 248)
        _titleLabel.TextAlign = ContentAlignment.MiddleLeft

        _pathLabel.AutoSize = False
        _pathLabel.Dock = DockStyle.Fill
        _pathLabel.Text = _path
        _pathLabel.Font = New Font("Segoe UI", 8.5F, FontStyle.Regular, GraphicsUnit.Point)
        _pathLabel.ForeColor = Color.FromArgb(166, 179, 190)
        _pathLabel.TextAlign = ContentAlignment.MiddleLeft

        header.Controls.Add(_titleLabel, 0, 0)
        header.Controls.Add(_pathLabel, 0, 1)
        root.Controls.Add(header, 0, 0)

        _grid.Dock = DockStyle.Fill
        _grid.BackgroundColor = Color.FromArgb(13, 16, 19)
        _grid.GridColor = Color.FromArgb(54, 61, 68)
        _grid.BorderStyle = BorderStyle.FixedSingle
        _grid.AllowUserToAddRows = False
        _grid.AllowUserToDeleteRows = False
        _grid.AllowUserToResizeRows = False
        _grid.ReadOnly = True
        _grid.MultiSelect = True
        _grid.RowHeadersVisible = False
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect
        _grid.AutoGenerateColumns = False
        _grid.EnableHeadersVisualStyles = False
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing
        _grid.ColumnHeadersHeight = 28
        _grid.RowTemplate.Height = 24
        _grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.DisplayedCellsExceptHeaders
        _grid.ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithoutHeaderText
        _grid.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(38, 44, 50)
        _grid.ColumnHeadersDefaultCellStyle.ForeColor = Color.FromArgb(236, 241, 244)
        _grid.ColumnHeadersDefaultCellStyle.SelectionBackColor = Color.FromArgb(38, 44, 50)
        _grid.ColumnHeadersDefaultCellStyle.SelectionForeColor = Color.FromArgb(236, 241, 244)
        _grid.DefaultCellStyle.BackColor = Color.FromArgb(17, 20, 24)
        _grid.DefaultCellStyle.ForeColor = Color.FromArgb(226, 234, 238)
        _grid.DefaultCellStyle.SelectionBackColor = Color.FromArgb(32, 116, 190)
        _grid.DefaultCellStyle.SelectionForeColor = Color.White
        _grid.DefaultCellStyle.WrapMode = DataGridViewTriState.True
        _grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(29, 34, 39)

        _grid.Columns.Add(New DataGridViewTextBoxColumn With {
            .Name = "Property",
            .HeaderText = "Property",
            .Width = 130,
            .SortMode = DataGridViewColumnSortMode.NotSortable
        })
        _grid.Columns.Add(New DataGridViewTextBoxColumn With {
            .Name = "Value",
            .HeaderText = "Value",
            .AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
            .SortMode = DataGridViewColumnSortMode.NotSortable
        })

        _grid.Rows.Add("Status", "Loading...")
        root.Controls.Add(_grid, 0, 1)
    End Sub

    Private Async Function LoadMediaInfoAsync() As Task
        Try
            Dim rows = Await Task.Run(
                Function() MediaInfoProvider.Read(_path, _loadCancellation.Token),
                _loadCancellation.Token)

            If _loadCancellation.IsCancellationRequested OrElse IsDisposed Then
                Return
            End If

            PopulateRows(rows)
        Catch ex As OperationCanceledException
            ' Window closed
        Catch ex As Exception
            If Not IsDisposed Then
                Dim errList As New List(Of MediaInfoRow) From {
                    New MediaInfoRow("MediaInfo", "Error", ex.Message)
                }
                PopulateRows(errList)
            End If
        End Try
    End Function

    Private Sub PopulateRows(rows As IReadOnlyList(Of MediaInfoRow))
        _grid.SuspendLayout()
        Try
            _grid.Rows.Clear()

            If rows.Count = 0 Then
                AddSectionHeader("MediaInfo")
                _grid.Rows.Add("Status", "No information returned.")
                Return
            End If

            Dim currentSection As String = Nothing
            For Each row In rows
                If Not String.Equals(currentSection, row.Section, StringComparison.Ordinal) Then
                    currentSection = row.Section
                    AddSectionHeader(currentSection)
                End If

                _grid.Rows.Add(row.Property, row.Value)
            Next
        Finally
            _grid.ResumeLayout()
        End Try
    End Sub

    Private Sub AddSectionHeader(section As String)
        Dim rowIndex = _grid.Rows.Add(section, String.Empty)
        Dim row = _grid.Rows(rowIndex)
        row.DefaultCellStyle.BackColor = Color.FromArgb(38, 44, 50)
        row.DefaultCellStyle.ForeColor = Color.FromArgb(239, 244, 248)
        row.DefaultCellStyle.SelectionBackColor = Color.FromArgb(38, 44, 50)
        row.DefaultCellStyle.SelectionForeColor = Color.FromArgb(239, 244, 248)
        row.DefaultCellStyle.Font = New Font(_grid.Font, FontStyle.Bold)
        row.Height = 28
    End Sub

    Private Sub CenterNearOwner()
        ApplyPreferredWidth()
        If Owner Is Nothing Then
            Dim screenArea = Screen.FromControl(Me).WorkingArea
            Location = New Point(screenArea.Left + (screenArea.Width - Width) \ 2, screenArea.Top + (screenArea.Height - Height) \ 2)
            Return
        End If

        Dim ownerBounds = Owner.Bounds
        Dim area = Screen.FromControl(Owner).WorkingArea
        Dim x = ownerBounds.Left + (ownerBounds.Width - Width) \ 2
        Dim y = ownerBounds.Top + (ownerBounds.Height - Height) \ 2
        x = Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - Width))
        y = Math.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - Height))
        Location = New Point(x, y)
    End Sub

    Private Sub ApplyPreferredWidth()
        Dim area = If(Owner IsNot Nothing, Screen.FromControl(Owner).WorkingArea, Screen.FromControl(Me).WorkingArea)
        Dim preferredWidth = Math.Clamp(PreferredWindowWidth, MinimumSize.Width, area.Width)
        Width = preferredWidth
    End Sub
End Class
